using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using DesktopToolbox.DeskLink.Collaboration;
using Nian.Input.Windows;

namespace DesktopToolbox.Windows.DeskSwitch.Collaboration;

/// <summary>Bounded desktop capture; never stores desktop images on disk.</summary>
public sealed class WindowsPreviewCaptureProvider
{
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly Func<WindowsDesktopSourceGeometry?> currentSource;
    public WindowsPreviewCaptureProvider(Func<WindowsDesktopSourceGeometry?>? currentSource=null)
        => this.currentSource=currentSource??WindowsDesktopSource.ReadCurrentPreviewSource;
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern bool StretchBlt(nint destination,int x,int y,int width,int height,nint source,int sourceX,int sourceY,int sourceWidth,int sourceHeight,uint operation);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc,int mode);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool GetUserObjectInformationW(nint handle,int index,StringBuilder information,int length,out int needed);

    public async Task<PreviewFrame> CaptureAsync(CancellationToken token=default,int maximumWidth=960,int maximumHeight=540)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(()=>Capture(token,maximumWidth,maximumHeight),token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private PreviewFrame Capture(CancellationToken token,int maximumWidth,int maximumHeight)
    {
        token.ThrowIfCancellationRequested();
        if(!IsInteractiveDesktop()) return PreviewFrame.Off("locked","Windows 已锁屏或正在显示安全桌面。");
        try
        {
            using var dpi=new PhysicalPixelScope();
            var sourceBefore=ReadSource();
            if(!HasPhysicalBounds(sourceBefore)) return PreviewFrame.Off("display-unavailable","Windows 没有可确认的活动显示器。");
            var bounds=sourceBefore!;
            var size=PreviewCapturePolicy.Fit((int)bounds.Width,(int)bounds.Height,maximumWidth,maximumHeight);
            using var bitmap=new Bitmap(size.Width,size.Height,PixelFormat.Format24bppRgb);
            using(var graphics=Graphics.FromImage(bitmap))
            {
                var target=graphics.GetHdc(); var source=GetDC(0);
                try
                {
                    if(source==0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    SetStretchBltMode(target,4); // HALFTONE; scale while copying, avoiding a full-size desktop allocation.
                    if(!StretchBlt(target,0,0,size.Width,size.Height,source,(int)bounds.Left,(int)bounds.Top,(int)bounds.Width,(int)bounds.Height,0x40CC0020))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { if(source!=0) ReleaseDC(0,source); graphics.ReleaseHdc(target); }
            }
            token.ThrowIfCancellationRequested();
            if(!IsInteractiveDesktop()) return PreviewFrame.Off("locked","Windows 已锁屏。");
            using var stream=new MemoryStream();
            var codec=ImageCodecInfo.GetImageEncoders().First(encoder=>encoder.FormatID==ImageFormat.Jpeg.Guid);
            using var options=new EncoderParameters(1);
            options.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,65L);
            bitmap.Save(stream,codec,options);
            token.ThrowIfCancellationRequested();
            if(!IsInteractiveDesktop()) return PreviewFrame.Off("locked","Windows 已锁屏。");
            var geometry=PreviewCapturePolicy.BindGeometry(sourceBefore,ReadSource(),bitmap.Width,bitmap.Height);
            if(geometry is null) return PreviewFrame.Off("display-unavailable","显示器布局在采集期间发生变化，请等待新画面。");
            return new PreviewFrame { State="live",Width=bitmap.Width,Height=bitmap.Height,JpegBytes=stream.ToArray(),CapturedAtUtc=DateTimeOffset.UtcNow,Geometry=geometry };
        }
        catch(Exception error) when(error is Win32Exception or ExternalException or ArgumentException)
        { return PreviewFrame.Off("capture-error","Windows 画面暂时不可用，请检查显示器连接或重新开启预览。"); }
    }
    private WindowsDesktopSourceGeometry? ReadSource() { try { return currentSource(); } catch { return null; } }
    private static bool HasPhysicalBounds(WindowsDesktopSourceGeometry? value) => value?.ToPreviewGeometry(1,1).IsValid==true &&
        value.Left==Math.Truncate(value.Left) && value.Top==Math.Truncate(value.Top) &&
        value.Width==Math.Truncate(value.Width) && value.Height==Math.Truncate(value.Height) &&
        value.Width<=32768 && value.Height<=32768;
    private sealed class PhysicalPixelScope : IDisposable
    {
        private readonly nint previous=SetThreadDpiAwarenessContext(-4);
        public PhysicalPixelScope() { if(previous==0)throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public void Dispose() => SetThreadDpiAwarenessContext(previous);
    }
    public static bool IsInteractiveDesktop()
    {
        var desktop=OpenInputDesktop(0,false,0x0001);
        if(desktop==0) return false;
        try { var name=new StringBuilder(256); return GetUserObjectInformationW(desktop,2,name,name.Capacity*2,out _) && string.Equals(name.ToString(),"Default",StringComparison.OrdinalIgnoreCase); }
        finally { CloseDesktop(desktop); }
    }
}
