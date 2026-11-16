using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.DeskLink.Collaboration;
using Nian.Input.Control;

namespace DesktopToolbox.Mac.Views;

/// <summary>Owns decoded frames and image coordinates, independently of the viewer's toolbar and window chrome.</summary>
public sealed partial class MacPreviewSurface : Grid, IDisposable
{
    private readonly PreviewControlSession? control;
    private readonly Image image=new(){Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};
    private readonly TextBlock message=new(){Text="正在等待画面…",HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,FontSize=14,TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,Margin=new Avalonia.Thickness(24),IsHitTestVisible=false};
    private Bitmap? bitmap;
    private byte[]? frameBytes;
    private int displayedWidth,displayedHeight;
    private bool disposed;
    private readonly MacPreviewFreshness freshness=new();
    private readonly DispatcherTimer expiry=new(){Interval=TimeSpan.FromSeconds(1)};
    public event Action? FrameChanged;
    public string FrameStatus {get;private set;}="";
    public bool HasLiveFrame=>bitmap is not null&&image.Source is not null;
    public MacPreviewSurface():this(null){}
    public MacPreviewSurface(PreviewControlSession? control)
    {
        this.control=control;Children.Add(image);Children.Add(message);Focusable=true;if(control is not null)ConfigurePointerInput();
        if(control is not null)control.Changed+=OnControlChanged;
        expiry.Tick+=(_,_)=>{if(liveEpoch is null&&!retainedLiveFrame&&bitmap is not null&&!freshness.IsFresh(Environment.TickCount64))Clear("正在等待新画面…");};expiry.Start();SetPalette(false);
    }
    public void SetPalette(bool dark)
    {var colors=PreviewControlPalette.ForDark(dark);Background=new SolidColorBrush(Color.Parse(colors.Soft));message.Foreground=new SolidColorBrush(Color.Parse(colors.Muted));}
    public void ShowFrame(PreviewFrame frame,int refreshSeconds=PreviewCaptureCadence.DefaultRefreshSeconds)
    {
        if(disposed||liveEpoch is not null)return;freshness.SetRefreshSeconds(refreshSeconds);
        if(frame.State=="live"&&frame.JpegBytes.Length>0)
        {
            if(passiveGenerationSupported)
            {
                if(passiveRequestId is null||frame.RequestId!=passiveRequestId||passiveSource?.Availability!="enabled"||frame.SourceStatus!=passiveSource||
                    frame.CaptureSequence is not >0||frame.CaptureSequence<=lastPassiveCaptureSequence)return;
            }
            else if(hasLiveTimestampFloor&&frame.CapturedAtUtc<=lastCapturedAt)return;
            if(!freshness.Observe(frame.CapturedAtUtc,Environment.TickCount64,forceNew:passiveGenerationSupported)){Clear("正在等待新画面…");return;}
            if(ReferenceEquals(frameBytes,frame.JpegBytes))
            {if(passiveGenerationSupported)lastPassiveCaptureSequence=frame.CaptureSequence!.Value;if(hasLiveTimestampFloor)lastCapturedAt=frame.CapturedAtUtc;retainedLiveFrame=false;ObserveDisplayed(frame);var stamp="更新于 "+frame.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss");if(FrameStatus!=stamp){FrameStatus=stamp;FrameChanged?.Invoke();}return;}
            try
            {
                using var stream=new MemoryStream(frame.JpegBytes);var next=new Bitmap(stream);var previous=bitmap;
                if(next.PixelSize.Width!=frame.Width||next.PixelSize.Height!=frame.Height){next.Dispose();throw new InvalidDataException("编码画面尺寸不一致。");}
                bitmap=next;image.Source=next;liveImage=null;frameBytes=frame.JpegBytes;message.IsVisible=false;previous?.Dispose();
                if(passiveGenerationSupported)lastPassiveCaptureSequence=frame.CaptureSequence!.Value;if(hasLiveTimestampFloor)lastCapturedAt=frame.CapturedAtUtc;retainedLiveFrame=false;
                FrameStatus="更新于 "+frame.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss");ObserveDisplayed(frame);FrameChanged?.Invoke();return;
            }
            catch(Exception error)when(error is ArgumentException or InvalidOperationException or IOException or NotSupportedException){}
        }
        // Delayed polling state must not erase the last frame retained after a normal return.
        if(retainedLiveFrame&&(frame.State is "loading" or "waiting"||passiveGenerationSupported&&frame.State=="off"))return;
        retainedLiveFrame=false;freshness.Invalidate();
        Clear(frame.State switch
        {
            "local-off"=>"画面已关闭，可在小窗手动开启",
            "off"=>"对方已关闭画面，请在 Windows 开启",
            "loading" or "waiting"=>"正在等待新画面…",
            "locked"=>"Windows 已锁屏，画面暂停",
            "permission"=>"请在对方设备允许屏幕采集",
            "offline" or "disconnected"=>"Windows 已断开，画面暂停",
            _=>"画面暂不可用，请检查设备连接"
        });
    }
    private void ObserveDisplayed(PreviewFrame frame)
    {displayedWidth=frame.Width;displayedHeight=frame.Height;control?.ObserveDisplayedFrame(frame.ControlProof,frame.JpegBytes,frame.Width,frame.Height);}
    private void Clear(string text)
    {
        image.Source=null;liveImage=null;bitmap?.Dispose();bitmap=null;frameBytes=null;retainedLiveFrame=false;displayedWidth=displayedHeight=0;FrameStatus="";message.IsVisible=true;message.Text=text;
        var active=control?.IsActive==true;control?.ObserveDisplayedFrame(null,[],0,0);if(active)_=StopControlAsync("画面暂不可用，操作已停止");FrameChanged?.Invoke();
    }
    private void OnControlChanged()=>Dispatcher.UIThread.Post(()=>{if(!disposed&&control?.IsActive!=true)ResetPointerCapture();});
    public void Dispose()
    {if(disposed)return;disposed=true;expiry.Stop();freshness.Invalidate();if(control is not null)control.Changed-=OnControlChanged;Clear("画面已关闭");_=StopControlAsync("窗口已关闭，操作已停止");}
}
