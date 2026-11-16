using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using Nian.Input.Windows;

namespace DesktopToolbox.Windows.DeskSwitch.Collaboration.LivePreview;

/// <summary>One owner thread reuses all desktop/DC/bitmap/codec resources until control ends.</summary>
internal sealed class GdiLivePreviewSession : IWindowsLivePreviewSession
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly WindowsDesktopSourceGeometry source;
    private GdiLivePreviewNative.PhysicalPixelScope? dpi;
    private GdiLivePreviewSurface? surface;
    private GdiLivePreviewJpegEncoder? encoder;
    private readonly GdiLivePreviewCursor cursor = new();
    private nint screenDc;
    private bool disposed;
    public GdiLivePreviewSession(LivePreviewCaptureOptions options)
    {
        options.Validate();
        try
        {
            dpi = new();
            var before = ReadSource();
            var failure = WindowsLiveCapturePolicy.Failure(GdiLivePreviewNative.IsInteractiveDesktop(), before, before);
            if (failure is not null) throw new WindowsLiveCaptureUnavailableException(failure);
            source = before!;
            var size = PreviewCapturePolicy.Fit(checked((int)source.Width), checked((int)source.Height), options.MaxWidth, options.MaxHeight);
            screenDc = GdiLivePreviewNative.GetDC(0);
            if (screenDc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            surface = new(size.Width, size.Height); encoder = new(options.JpegQuality);
        }
        catch { Dispose(); throw; }
    }
    public WindowsLivePreviewSample Capture(CancellationToken cancellationToken)
    {
        RequireOwner(); ObjectDisposedException.ThrowIf(disposed, this); cancellationToken.ThrowIfCancellationRequested();
        var failure = ReadFailure();
        if (failure is not null) return Off(failure);
        var target = surface!;
        if (!GdiLivePreviewNative.StretchBlt(target.DeviceContext, 0, 0, target.Bitmap.Width, target.Bitmap.Height, screenDc,
            checked((int)source.Left), checked((int)source.Top), checked((int)source.Width), checked((int)source.Height), 0x40CC0020))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        cursor.Draw(target.DeviceContext, source, target.Bitmap.Width, target.Bitmap.Height);
        if (!GdiLivePreviewNative.GdiFlush()) throw new Win32Exception(Marshal.GetLastWin32Error());
        var captured = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        failure = ReadFailure();
        if (failure is not null) return Off(failure);
        var bytes = encoder!.Encode(target.Bitmap);
        cancellationToken.ThrowIfCancellationRequested();
        failure = ReadFailure();
        return failure is null ? new(captured, target.Bitmap.Width, target.Bitmap.Height, bytes) : Off(failure);
    }
    private string? ReadFailure() => WindowsLiveCapturePolicy.Failure(GdiLivePreviewNative.IsInteractiveDesktop(), source, ReadSource());
    private static WindowsDesktopSourceGeometry? ReadSource() { try { return WindowsDesktopSource.ReadCurrentPreviewSource(); } catch { return null; } }
    private static WindowsLivePreviewSample Off(string code) => new(default, 0, 0, [], code);
    private void RequireOwner() { if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("GDI capture must stay on its native owner thread."); }
    public void Dispose()
    {
        RequireOwner(); if (disposed) return; disposed = true;
        try { encoder?.Dispose(); }
        finally
        {
            try { surface?.Dispose(); }
            finally
            {
                if (screenDc != 0) { GdiLivePreviewNative.ReleaseDC(0, screenDc); screenDc = 0; }
                dpi?.Dispose();
            }
        }
    }
}
