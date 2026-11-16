using System.Text.Json;
using Nian.Input.Control;

namespace DesktopToolbox.Mac.Services;

public interface IMacMediaBridge : IAsyncDisposable
{
    Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default);
    Task<MacScreenCaptureReply> CaptureAsync(int width = 960, int height = 540, uint? windowId = null, CancellationToken cancellationToken = default);
    Task<PreviewSourceGeometry?> ReadPreviewGeometryAsync(int imageWidth = 1, int imageHeight = 1, CancellationToken cancellationToken = default)
        => Task.FromResult<PreviewSourceGeometry?>(null);
}
