using DesktopToolbox.DeskLink.Collaboration;

namespace DesktopToolbox.Mac.Models;

/// <summary>Freshness uses local monotonic receipt time. A repeated remote frame never renews its own lifetime.</summary>
public sealed class MacPreviewFreshness(int refreshSeconds = PreviewCaptureCadence.DefaultRefreshSeconds)
{
    private int refreshSeconds = PreviewCaptureCadence.NormalizeRefreshSeconds(refreshSeconds);
    private DateTimeOffset? lastCaptured;
    private long receivedAt;
    private bool valid;
    public bool Observe(DateTimeOffset capturedAt, long now, bool forceNew = false)
    {
        if (forceNew || lastCaptured != capturedAt) { lastCaptured = capturedAt; receivedAt = now; valid = true; }
        return IsFresh(now);
    }
    public bool IsFresh(long now) => valid && now - receivedAt >= 0 && now - receivedAt < refreshSeconds * 1000L + 7000;
    public void SetRefreshSeconds(int seconds) => refreshSeconds = PreviewCaptureCadence.NormalizeRefreshSeconds(seconds);
    public void Invalidate() => valid = false;
    public void BeginGeneration() { lastCaptured = null; valid = false; }
}
