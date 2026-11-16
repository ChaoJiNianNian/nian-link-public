using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;

namespace DesktopToolbox.DeskLink.Preview;

/// <summary>A Windows viewer owns one remote upload demand, never the shared endpoint.</summary>
public sealed class RemoteUploadControlLivePreviewSource : IControlLivePreviewSource
{
    private sealed class Run(Guid epoch, LivePreviewServer server)
    {
        public Guid Epoch { get; } = epoch;
        public LivePreviewServer Server { get; } = server;
        public LivePreviewLease? Lease { get; set; }
        public Action<Guid, LivePreviewCapturePacket>? Packet { get; set; }
        public Action<Guid, string>? Closed { get; set; }
    }
    private readonly object gate = new();
    private readonly Func<CollaborationServer?> endpoint;
    private readonly Func<bool> available;
    private Run? current;
    private volatile bool disposed;
    public RemoteUploadControlLivePreviewSource(Func<CollaborationServer?> endpoint, Func<bool> available)
    { this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint)); this.available = available ?? throw new ArgumentNullException(nameof(available)); }
    public bool IsSupported => !disposed && available() && endpoint()?.LivePreview.RemotePeerSupportsLivePreview == true;
    public string UnavailableHint => "请连接支持实时画面的新版 Mac";
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
                var server = endpoint()?.LivePreview;
                if (!available() || server?.RemotePeerSupportsLivePreview != true) throw new NotSupportedException(UnavailableHint);
                started = new(operationId, server);
                var owned = started;
                owned.Packet = (id, packet) => ForwardPacket(owned, id, packet);
                owned.Closed = (id, reason) => ForwardClosed(owned, id, reason);
                // Subscribe before publishing the demand. The gate prevents an exceptionally
                // fast upload from being checked before its newly allocated ID is stored.
                server.PacketReceived += owned.Packet;
                server.StreamClosed += owned.Closed;
                current = owned;
                owned.Lease = server.RequestRemoteCapture(new());
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            if (started is not null) await ReleaseAsync(started).ConfigureAwait(false);
            throw;
        }
    }
    private void ForwardPacket(Run owned, Guid id, LivePreviewCapturePacket packet)
    {
        Action<Guid, LivePreviewCapturePacket>? callback;
        lock (gate) callback = ReferenceEquals(current, owned) && owned.Lease?.StreamId == id ? PacketReceived : null;
        if (callback is not null)
            foreach (Action<Guid, LivePreviewCapturePacket> listener in callback.GetInvocationList())
                try { listener(owned.Epoch, packet); } catch { }
    }
    private void ForwardClosed(Run owned, Guid id, string reason)
    {
        Action<Guid, string>? callback;
        lock (gate) callback = ReferenceEquals(current, owned) && owned.Lease?.StreamId == id ? StreamClosed : null;
        if (callback is not null)
            foreach (Action<Guid, string> listener in callback.GetInvocationList())
                try { listener(owned.Epoch, reason); } catch { }
    }
    public Task StopAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        Run? owned;
        lock (gate) owned = current?.Epoch == operationId ? current : null;
        return owned is null ? Task.CompletedTask : ReleaseAsync(owned);
    }
    private async Task ReleaseAsync(Run owned)
    {
        lock (gate)
        {
            if (!ReferenceEquals(current, owned)) return;
            current = null;
            owned.Server.PacketReceived -= owned.Packet;
            owned.Server.StreamClosed -= owned.Closed;
        }
        if (owned.Lease is { } lease) await lease.DisposeAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        Run? owned;
        lock (gate) { if (disposed) return; disposed = true; owned = current; }
        if (owned is not null) await ReleaseAsync(owned).ConfigureAwait(false);
    }
}
