namespace DesktopToolbox.DeskLink.Collaboration.LivePreview;

/// <summary>
/// One unsent packet only. Source idle is never allowed to erase an unsent
/// image; terminal failure always wins. Images still replace older images.
/// </summary>
internal sealed class LivePreviewOutboundMailbox : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim available = new(0, 1);
    private LivePreviewCapturePacket? pending;
    private bool disposed;

    internal bool Publish(LivePreviewCapturePacket packet)
    {
        lock (gate)
        {
            if (disposed) return false;
            if (pending is null)
            {
                pending = packet;
                available.Release();
            }
            else if (pending.Kind != LivePreviewCaptureKind.Unavailable &&
                     !(pending.Kind == LivePreviewCaptureKind.Image && packet.Kind == LivePreviewCaptureKind.HealthyIdle))
            {
                pending = packet;
            }
            return true;
        }
    }

    internal async ValueTask<LivePreviewCapturePacket> ReadAsync(CancellationToken cancellationToken)
    {
        await available.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            var packet = pending ?? throw new InvalidOperationException("A live packet was signalled without an item.");
            pending = null;
            return packet;
        }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; pending = null; }
        available.Dispose();
    }
}
