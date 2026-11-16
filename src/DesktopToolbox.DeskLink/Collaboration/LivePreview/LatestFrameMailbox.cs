using System.Diagnostics;
using System.Threading.Channels;
namespace DesktopToolbox.DeskLink.Collaboration.LivePreview;

/// <summary>Owns only the newest queued item. A reader takes ownership; replaced items are released.</summary>
public sealed class LatestFrameMailbox<T> : IDisposable where T : class
{
    private readonly Channel<T> channel;
    private readonly Action<T> release;
    private int disposed;
    public LatestFrameMailbox(Action<T>? release = null)
    {
        this.release = release ?? (item => (item as IDisposable)?.Dispose());
        channel = Channel.CreateBounded<T>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false }, Release);
    }
    public bool Publish(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Volatile.Read(ref disposed) == 0 && channel.Writer.TryWrite(item)) return true;
        Release(item); return false;
    }
    public ValueTask<T> ReadAsync(CancellationToken cancellationToken = default) => channel.Reader.ReadAsync(cancellationToken);
    public bool TryRead(out T? item) => channel.Reader.TryRead(out item);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        channel.Writer.TryComplete();
        while (channel.Reader.TryRead(out var item)) Release(item);
    }
    private void Release(T item)
    {
        try { release(item); }
        catch (Exception error) { Trace.TraceError("Live frame release failed ({0}).", error.GetType().Name); }
    }
}
