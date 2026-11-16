using DesktopToolbox.DeskLink.Collaboration.LivePreview;

namespace DesktopToolbox.Windows.DeskSwitch.Collaboration.LivePreview;

internal sealed record WindowsLivePreviewSample(DateTimeOffset CapturedAtUtc, int Width, int Height, byte[] JpegBytes, string? FailureCode = null);

internal interface IWindowsLivePreviewSession : IDisposable
{
    WindowsLivePreviewSample Capture(CancellationToken cancellationToken);
}

internal interface IWindowsLivePreviewSessionFactory
{
    bool IsSupported { get; }
    IWindowsLivePreviewSession Open(LivePreviewCaptureOptions options);
}

internal sealed class WindowsLiveCaptureUnavailableException(string code) : Exception("Windows live capture is unavailable.")
{
    public string Code { get; } = code;
}

internal sealed class GdiLivePreviewSessionFactory : IWindowsLivePreviewSessionFactory
{
    public bool IsSupported => OperatingSystem.IsWindows();
    public IWindowsLivePreviewSession Open(LivePreviewCaptureOptions options) => new GdiLivePreviewSession(options);
}
