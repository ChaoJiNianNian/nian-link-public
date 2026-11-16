using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;

namespace DesktopToolbox.DeskLink.Preview;

/// <summary>A Mac viewer owns a receive task, never the shared collaboration client.</summary>
public sealed class RemoteSubscriptionControlLivePreviewSource : IControlLivePreviewSource
{
    private sealed class Run(Guid epoch, LivePreviewClient client)
    {
        public Guid Epoch { get; } = epoch;
        public LivePreviewClient Client { get; } = client;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Receiver { get; set; } = Task.CompletedTask;
    }
    private readonly object gate = new();
    private readonly Func<CollaborationClient?> endpoint;
    private readonly Func<bool> available;
    private Run? current;
    private volatile bool disposed;
    public RemoteSubscriptionControlLivePreviewSource(Func<CollaborationClient?> endpoint, Func<bool> available)
    { this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint)); this.available = available ?? throw new ArgumentNullException(nameof(available)); }
    public bool IsSupported => !disposed && available() && endpoint() is not null;
    public string UnavailableHint => "请连接支持实时画面的新版 Windows";
    public event Action<Guid, LivePreviewCapturePacket>? PacketReceived;
    public event Action<Guid, string>? StreamClosed;

    public async Task StartAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A preview operation is required.", nameof(operationId));
        cancellationToken.ThrowIfCancellationRequested();
        Run? started = null;
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (current is not null) throw new InvalidOperationException("This preview window already owns a stream.");
                var client = endpoint()?.LivePreview;
                if (!available() || client is null) throw new NotSupportedException(UnavailableHint);
                started = new(operationId, client);
                var owned = started;
                current = owned;
                // Lifetime belongs to this epoch, not the short-lived startup token.
                owned.Receiver = Task.Run(() => ReceiveAsync(owned));
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            if (started is not null) await ReleaseAsync(started).ConfigureAwait(false);
            throw;
        }
    }
    private async Task ReceiveAsync(Run owned)
    {
        var reason = "disconnected";
        try
        {
            await owned.Client.ReceiveAsync(owned.Epoch, new(),
                (id, packet) => ForwardPacket(owned, id, packet), owned.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (owned.Cancellation.IsCancellationRequested) { reason = "stopped"; }
        catch (Exception error)
        {
            reason = error is UnauthorizedAccessException ? "unauthorized" : "disconnected";
            System.Diagnostics.Trace.TraceError("Live preview receive ended ({0}).", error.GetType().Name);
        }
        finally
        {
            Action<Guid, string>? callback;
            lock (gate) callback = ReferenceEquals(current, owned) && !owned.Cancellation.IsCancellationRequested ? StreamClosed : null;
            if (callback is not null)
                foreach (Action<Guid, string> listener in callback.GetInvocationList())
                    try { listener(owned.Epoch, reason); } catch { }
        }
    }
    private void ForwardPacket(Run owned, Guid id, LivePreviewCapturePacket packet)
    {
        Action<Guid, LivePreviewCapturePacket>? callback;
        lock (gate) callback = ReferenceEquals(current, owned) && owned.Epoch == id && !owned.Cancellation.IsCancellationRequested ? PacketReceived : null;
        if (callback is not null)
            foreach (Action<Guid, LivePreviewCapturePacket> listener in callback.GetInvocationList())
                try { listener(owned.Epoch, packet); } catch { }
    }
    public async Task StopAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        Run? owned;
        lock (gate) owned = current?.Epoch == operationId ? current : null;
        if (owned is not null) await ReleaseAsync(owned).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    private async Task ReleaseAsync(Run owned)
    {
        lock (gate)
        {
            if (!ReferenceEquals(current, owned)) return;
            current = null;
        }
        owned.Cancellation.Cancel();
        try { await owned.Receiver.ConfigureAwait(false); }
        finally { owned.Cancellation.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        Run? owned;
        lock (gate) { if (disposed) return; disposed = true; owned = current; }
        if (owned is not null) await ReleaseAsync(owned).ConfigureAwait(false);
    }
}
