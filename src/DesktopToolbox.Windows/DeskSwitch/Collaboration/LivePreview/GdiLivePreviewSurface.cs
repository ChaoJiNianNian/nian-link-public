using System.Drawing;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopToolbox.Windows.DeskSwitch.Collaboration.LivePreview;

/// <summary>A reusable private DIB target; it never reads the desktop by itself.</summary>
internal sealed class GdiLivePreviewSurface : IDisposable
{
    private nint dc, dib, previousBitmap;
    private Bitmap? bitmap;
    private bool disposed;
    public GdiLivePreviewSurface(int width, int height)
    {
        if (width is < 1 or > 1920 || height is < 1 or > 1080) throw new ArgumentOutOfRangeException(nameof(width));
        try
        {
            dc = GdiLivePreviewNative.CreateCompatibleDC(0);
            if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new GdiLivePreviewNative.BitmapInfo { Header = new()
            {
                Size = (uint)Marshal.SizeOf<GdiLivePreviewNative.BitmapInfoHeader>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32, SizeImage = checked((uint)(width * height * 4))
            } };
            dib = GdiLivePreviewNative.CreateDIBSection(dc, ref info, 0, out var pixels, 0, 0);
            if (dib == 0 || pixels == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previousBitmap = GdiLivePreviewNative.SelectObject(dc, dib);
            if (previousBitmap == 0 || previousBitmap == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (GdiLivePreviewNative.SetStretchBltMode(dc, 4) == 0 || !GdiLivePreviewNative.SetBrushOrgEx(dc, 0, 0, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // A scan-pointer bitmap avoids holding Graphics.GetHdc during GDI+ JPEG encoding.
            bitmap = new Bitmap(width, height, checked(width * 4), PixelFormat.Format32bppRgb, pixels);
        }
        catch { Dispose(); throw; }
    }
    public Bitmap Bitmap { get { ObjectDisposedException.ThrowIf(disposed, this); return bitmap!; } }
    public nint DeviceContext { get { ObjectDisposedException.ThrowIf(disposed, this); return dc; } }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        GdiLivePreviewNative.GdiFlush();
        try { bitmap?.Dispose(); }
        finally
        {
            bitmap = null;
            if (dc != 0 && previousBitmap != 0 && previousBitmap != -1) GdiLivePreviewNative.SelectObject(dc, previousBitmap);
            if (dib != 0) { GdiLivePreviewNative.DeleteObject(dib); dib = 0; }
            if (dc != 0) { GdiLivePreviewNative.DeleteDC(dc); dc = 0; }
        }
    }
}
