namespace DesktopToolbox.DeskLink.Collaboration.LivePreview;

/// <summary>Owns at most one explicitly authorized upload; withdrawn demand cannot restart itself.</summary>
public sealed class LivePreviewUploadController : IAsyncDisposable
{
    private readonly Func<LivePreviewDemand, CancellationToken, Task> upload;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<Guid> attempted = [];
    private CancellationTokenSource? cancellation;
    private Task? running;
    private Guid? current;
    private bool disposed;

    public LivePreviewUploadController(Func<LivePreviewDemand, CancellationToken, Task> upload) =>
        this.upload = upload ?? throw new ArgumentNullException(nameof(upload));

    public async Task ObserveAsync(LivePreviewDemand? demand, bool allowed, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (allowed && demand is not null && demand.StreamId == current) return;
            await StopInsideAsync();
            if (!allowed || demand is null || demand.StreamId == Guid.Empty || attempted.Count >= 1024 || !attempted.Add(demand.StreamId)) return;
            demand.Options.Validate();
            current = demand.StreamId;
            var owned = new CancellationTokenSource(); cancellation = owned;
            running = Task.Run(async () =>
            {
                try { await upload(demand, owned.Token); }
                catch (OperationCanceledException) when (owned.IsCancellationRequested) { }
                catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Live upload stopped ({0}).", error.GetType().Name); }
            });
        }
        finally { gate.Release(); }
    }

    private async Task StopInsideAsync()
    {
        var owned = cancellation; var task = running;
        cancellation = null; running = null; current = null;
        if (owned is null) return;
        owned.Cancel();
        try { if (task is not null) await task; }
        finally { owned.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (!disposed) { await StopInsideAsync(); disposed = true; } }
        finally { gate.Release(); }
    }
}
