using Nian.Input.Control;
using Nian.Input.Runtime;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Services;

public sealed partial class MacNativeLinkService
{
    public PreviewFrameProof? BindCapturedFrame(byte[] jpeg, PreviewSourceGeometry? geometry)
    {
        if (Volatile.Read(ref running) == 0 || geometry is null || ApprovedEndpoint(DevicePermission.PreviewControl) is null) { InvalidateCapturedFrames(); return null; }
        try { return frames.Register(jpeg, geometry); } catch { InvalidateCapturedFrames(); return null; }
    }
    public void InvalidateCapturedFrames() { frames.Invalidate(); input.InvalidatePreview(); }
    public IPreviewControlChannel CreatePreviewChannel() { ThrowIfDisposed(); return new LazyPreviewChannel(this); }

    private async Task PreparePreviewAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await gate.WaitAsync(deadline.Token);
        try
        {
            ThrowIfDisposed(); var epoch = Volatile.Read(ref policyEpoch);
            var endpoint = ApprovedEndpoint(DevicePermission.PreviewControl) ?? throw new InvalidOperationException("请先开始已批准的原生共享。");
            // This revokes only input ownership. It never requests a full switch or writes display intent.
            arbiter.StopAll("preview-requested");
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (epoch != Volatile.Read(ref policyEpoch) || endpoint != ApprovedEndpoint(DevicePermission.PreviewControl))
                    throw new InvalidOperationException("原生共享或本机允许已变化。");
                var state = await WithWindowsClientAsync((client, ct) => client.GetStateAsync(ct), deadline.Token);
                ValidateWindowsNames(state);
                if (!state.Available) throw new InvalidOperationException("Windows 原生接收器尚未就绪。");
                if (epoch != Volatile.Read(ref policyEpoch) || endpoint != ApprovedEndpoint(DevicePermission.PreviewControl))
                    throw new InvalidOperationException("原生共享或本机允许已变化。");
                if (!state.InputActive) return;
                // The old physical sender needs its rejected heartbeat ACK before it can restore capture.
                await Task.Delay(50, deadline.Token);
            }
        }
        finally { gate.Release(); }
    }
    private NativeInputConnection CreatePreviewTransport()
    {
        lock (connectionsGate)
        {
            ThrowIfDisposed();
            if (identity is null || trust is null || ApprovedEndpoint(DevicePermission.PreviewControl) is null) throw new InvalidOperationException("原生预览控制尚未就绪。");
            var connection = new NativeInputConnection(identity, trust, () => ApprovedEndpoint(DevicePermission.PreviewControl), DesktopControlMode.Preview, arbiter, options.Clock);
            connections.Add(connection); return connection;
        }
    }
    private async Task RemovePreviewTransportAsync(NativeInputConnection connection)
    {
        try { await connection.DisposeAsync(); }
        finally { lock (connectionsGate) connections.Remove(connection); }
    }
    private sealed class LazyPreviewChannel(MacNativeLinkService owner) : IPreviewControlChannel, IAsyncDisposable
    {
        private NativeInputConnection? current;
        private long generation;
        private int disposed;
        public async Task BeginAsync(CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var attempt = Interlocked.Increment(ref generation);
            await owner.PreparePreviewAsync(token);
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (attempt != Volatile.Read(ref generation)) throw new OperationCanceledException();
            var connection = owner.CreatePreviewTransport(); var previous = Interlocked.Exchange(ref current, connection);
            if (previous is not null) { previous.Abort("preview-replaced"); await owner.RemovePreviewTransportAsync(previous); }
            try
            {
                if (attempt != Volatile.Read(ref generation)) throw new OperationCanceledException();
                await ((IPreviewControlChannel)connection).BeginAsync(token);
            }
            catch { Interlocked.CompareExchange(ref current, null, connection); await owner.RemovePreviewTransportAsync(connection); throw; }
        }
        public Task SendAsync(PreviewInputCommand command, CancellationToken token) => ((IPreviewControlChannel)(Volatile.Read(ref current) ?? throw new InvalidOperationException("预览控制尚未开始。"))).SendAsync(command, token);
        public Task HeartbeatAsync(CancellationToken token) => ((IPreviewControlChannel)(Volatile.Read(ref current) ?? throw new InvalidOperationException("预览控制尚未开始。"))).HeartbeatAsync(token);
        public async Task EndAsync(CancellationToken token)
        {
            Interlocked.Increment(ref generation); var connection = Interlocked.Exchange(ref current, null); if (connection is null) return;
            try { await ((IPreviewControlChannel)connection).EndAsync(token); }
            finally { await owner.RemovePreviewTransportAsync(connection); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await EndAsync(deadline.Token);
        }
    }
}
