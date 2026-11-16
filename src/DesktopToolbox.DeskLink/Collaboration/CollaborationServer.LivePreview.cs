using DesktopToolbox.DeskLink.Collaboration.LivePreview;

namespace DesktopToolbox.DeskLink.Collaboration;

public sealed partial class CollaborationServer
{
    private readonly LivePreviewServer livePreview;
    public LivePreviewServer LivePreview => livePreview;
    internal DateTimeOffset LiveNow => clock.GetUtcNow();
    internal Task<byte[]> ReadLiveTokenAsync(CancellationToken ct) => ReadTokenAsync(ct);
}
