namespace NianLink.Windows;

internal readonly record struct DetectionResult<T>(bool Accepted, T Value);

/// <summary>Independent detection; stale or cancelled probes can never publish their result.</summary>
internal sealed class DisplayDetectionCoordinator<T>(Func<CancellationToken, Task<T>> probe) : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? active;
    private long version;
    private bool disposed;

    internal async Task<DetectionResult<T>> DetectAsync()
    {
        var next = new CancellationTokenSource();
        long ticket;
        CancellationTokenSource? previous;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            previous = active;
            active = next;
            ticket = ++version;
        }
        TryCancel(previous);
        try
        {
            var value = await probe(next.Token);
            lock (gate) return new(!disposed && ticket == version && !next.IsCancellationRequested, value);
        }
        catch (OperationCanceledException) when (next.IsCancellationRequested) { return new(false, default!); }
        catch (Exception)
        {
            lock (gate) if (disposed || ticket != version) return new(false, default!);
            throw;
        }
        finally
        {
            lock (gate) if (ReferenceEquals(active, next)) active = null;
            next.Dispose();
        }
    }

    internal void Invalidate()
    {
        CancellationTokenSource? previous;
        lock (gate) { if (disposed) return; version++; previous = active; active = null; }
        TryCancel(previous);
    }

    internal long CaptureVersion() { lock (gate) return version; }
    internal bool IsCurrent(long ticket) { lock (gate) return !disposed && ticket == version; }

    private static void TryCancel(CancellationTokenSource? value)
    {
        try { value?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        CancellationTokenSource? previous;
        lock (gate) { if (disposed) return; disposed = true; version++; previous = active; active = null; }
        TryCancel(previous);
    }
}
