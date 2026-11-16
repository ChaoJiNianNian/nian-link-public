using Nian.Input.Control;

namespace Nian.Input.Runtime;

public sealed partial class PreviewOnlyDeviceService
{
    public IPreviewControlChannel CreatePreviewChannel() { ThrowIfDisposed(); return new PreviewChannel(this); }
    private NativeInputConnection CreateConnection(long generation)
    {
        lock (channelsGate)
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref started) == 0 || identity is null || trust is null) throw new InvalidOperationException("Start and approve preview control first.");
            if (generation != Interlocked.Read(ref controlGeneration)) throw new OperationCanceledException("Preview control was superseded.");
            var approved = ApprovedPeer() ?? throw new InvalidOperationException("Approve this preview device locally first.");
            var connection = new NativeInputConnection(identity, trust, () => ReadSendEndpoint(approved, generation), DesktopControlMode.Preview, arbiter, options.Clock);
            connection.Stopped += _ => QueueStatus(); channels.Add(connection); return connection;
        }
    }
    private async Task RemoveConnectionAsync(NativeInputConnection connection)
    {
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        finally { lock (channelsGate) channels.Remove(connection); QueueStatus(); }
    }
    private sealed class PreviewChannel(PreviewOnlyDeviceService owner) : IPreviewControlChannel, IAsyncDisposable
    {
        private readonly object gate = new();
        private NativeInputConnection? current;
        private long generation;
        private int disposed;
        public async Task BeginAsync(CancellationToken token)
        {
            var controlGeneration = Interlocked.Read(ref owner.controlGeneration);
            long attempt; NativeInputConnection connection; NativeInputConnection? previous;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                attempt = ++generation; connection = owner.CreateConnection(controlGeneration);
                previous = current; current = connection;
            }
            if (previous is not null) { previous.Abort("preview-replaced"); await owner.RemoveConnectionAsync(previous).ConfigureAwait(false); }
            try
            {
                Task beginning;
                lock (gate)
                {
                    if (attempt != generation || !ReferenceEquals(current, connection)) throw new OperationCanceledException("Preview control was stopped.");
                    // Publish the NativeInputConnection run before End can detach this bridge generation.
                    beginning = ((IPreviewControlChannel)connection).BeginAsync(token);
                }
                await beginning.ConfigureAwait(false);
                if (attempt != Interlocked.Read(ref generation) || !ReferenceEquals(Volatile.Read(ref current), connection)) throw new OperationCanceledException("Preview control was stopped.");
                owner.SetStatus("预览操作中", clearError: true);
            }
            catch
            {
                Interlocked.CompareExchange(ref current, null, connection); connection.Abort("preview-begin-failed");
                await owner.RemoveConnectionAsync(connection).ConfigureAwait(false); throw;
            }
        }
        public Task SendAsync(PreviewInputCommand command, CancellationToken token) => Channel.SendAsync(command, token);
        public Task HeartbeatAsync(CancellationToken token) => Channel.HeartbeatAsync(token);
        private IPreviewControlChannel Channel => (IPreviewControlChannel)(Volatile.Read(ref current) ?? throw new InvalidOperationException("Preview control is inactive."));
        public async Task EndAsync(CancellationToken token)
        {
            NativeInputConnection? connection;
            lock (gate) { ++generation; connection = current; current = null; }
            if (connection is null) return;
            try { await ((IPreviewControlChannel)connection).EndAsync(token).ConfigureAwait(false); }
            finally { await owner.RemoveConnectionAsync(connection).ConfigureAwait(false); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) await EndAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
