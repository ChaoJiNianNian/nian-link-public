namespace DesktopToolbox.DeskLink.Collaboration.LivePreview;

public sealed record LivePreviewDemand(Guid StreamId, LivePreviewCaptureOptions Options);

/// <summary>A local viewer's generation. Releasing an old lease must not affect a subsequent viewer.</summary>
public sealed class LivePreviewLease : IAsyncDisposable
{
    private readonly Func<Guid, ValueTask> release;
    private int disposed;
    internal LivePreviewLease(Guid streamId, LivePreviewCaptureOptions options, Func<Guid, ValueTask> release)
    { StreamId = streamId; Options = options; this.release = release; }
    public Guid StreamId { get; }
    public LivePreviewCaptureOptions Options { get; }
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) == 0 ? release(StreamId) : ValueTask.CompletedTask;
}
