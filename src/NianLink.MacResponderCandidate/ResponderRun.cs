using System.Net;
using DesktopToolbox.DeskLink;

namespace NianLink.MacResponderCandidate;

// These interfaces are internal: callers cannot substitute a fake network or packet source into a real run.
internal interface IResponderNetwork : IDisposable
{
    CancellationToken RevocationToken { get; }
    Task<bool> RevalidateAsync(CancellationToken ct);
}
internal interface IResponderPolicy : IDisposable { byte[]? Reply(ResponderPacket packet); }
internal interface IResponderTransport : IAsyncDisposable
{
    Task<ResponderPacket> ReceiveAsync(CancellationToken ct);
    Task SendReplyAsync(ResponderPacket packet, byte[] data, CancellationToken ct);
}
internal sealed record ResponderPacket(byte[] Data, IPEndPoint? Source = null, IPAddress? Destination = null, int InterfaceIndex = 0);

/// <summary>One bounded run. A stopped run cannot be restarted.</summary>
internal sealed class ResponderRun : IAsyncDisposable
{
    private readonly IResponderNetwork network;
    private readonly IResponderPolicy policy;
    private readonly IResponderTransport transport;
    private readonly CancellationTokenSource stopped;
    private readonly PairingNoticeInbox? pairingInbox;
    private int stopRequested, sourceDisposed;
    public Task Completion { get; }
    public Exception? Error { get; private set; }

    private ResponderRun(IResponderNetwork network, IResponderPolicy policy, IResponderTransport transport,
        TimeSpan duration, CancellationToken cancellation, DiscoveryNetworkBinding? binding = null, PeerIdentityOffer? offer = null,
        Func<CancellationToken, Task<PairingNetworkHandoff>>? acquire = null)
    {
        ArgumentNullException.ThrowIfNull(network); ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(transport);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(duration));
        cancellation.ThrowIfCancellationRequested();
        this.network = network; this.policy = policy; this.transport = transport;
        stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellation, network.RevocationToken);
        if (binding is not null || offer is not null)
        {
            if (binding is null || offer is null) { stopped.Dispose(); throw new ArgumentException("Incomplete pairing binding."); }
            try { pairingInbox = new PairingNoticeInbox(binding, offer, duration, stopped.Token, acquireNetwork: acquire); }
            catch { stopped.Dispose(); throw; }
        }
        stopped.CancelAfter(duration);
        Completion = RunAsync();
    }

    // This internal constructor is not reachable from a public production API.
    internal static ResponderRun StartForTest(IResponderNetwork network, IResponderPolicy policy,
        IResponderTransport transport, TimeSpan duration) => new(network, policy, transport, duration, CancellationToken.None);

    internal static ResponderRun StartReal(IResponderNetwork network, IResponderPolicy policy,
        IResponderTransport transport, CancellationToken cancellation, DiscoveryNetworkBinding binding, PeerIdentityOffer offer,
        Func<CancellationToken, Task<PairingNetworkHandoff>> acquire)
        => new(network, policy, transport, TimeSpan.FromSeconds(30), cancellation, binding, offer, acquire);

    internal static ResponderRun StartForPairingTest(IResponderNetwork network, IResponderPolicy policy,
        IResponderTransport transport, TimeSpan duration, DiscoveryNetworkBinding binding, PeerIdentityOffer offer)
        => new(network, policy, transport, duration, CancellationToken.None, binding, offer);

    internal PairingIncomingRequest? GetPendingPairingRequest() => pairingInbox?.GetPending();
    internal bool RejectPairingRequest(Guid id) => pairingInbox?.Reject(id) == true;

    private async Task<bool> CheckAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Even if a platform API ignores cancellation, no late completion can revive this run.
        var valid = await network.RevalidateAsync(token).WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!valid) throw new InvalidOperationException("Approved network is unavailable.");
        return valid;
    }

    private async Task RunAsync()
    {
        try
        {
            var token = stopped.Token;
            while (true)
            {
                await CheckAsync(token).ConfigureAwait(false);
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(token);
                tick.CancelAfter(TimeSpan.FromSeconds(1));
                ResponderPacket packet;
                try { packet = await transport.ReceiveAsync(tick.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                await CheckAsync(token).ConfigureAwait(false);
                if (pairingInbox is not null && !DiscoveryProtocol.MulticastGroup.Equals(packet.Destination))
                {
                    if (packet.Source is not null && packet.Destination is not null)
                        pairingInbox.Observe(packet.Data, packet.Source, packet.Destination, packet.InterfaceIndex);
                    continue;
                }
                var response = policy.Reply(packet);
                if (response is null) continue;
                await CheckAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await transport.SendReplyAsync(packet, response, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        catch (Exception error) { Error = error; }
        finally
        {
            try { stopped.Cancel(); } catch (Exception error) { Error ??= error; }
            try { pairingInbox?.Dispose(); } catch (Exception error) { Error ??= error; }
            try { policy.Dispose(); } catch (Exception error) { Error ??= error; }
            try { await transport.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { Error ??= error; }
            try { network.Dispose(); } catch (Exception error) { Error ??= error; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref stopRequested, 1) == 0) stopped.Cancel();
        await Completion.ConfigureAwait(false);
        if (Interlocked.Exchange(ref sourceDisposed, 1) == 0) stopped.Dispose();
    }
}
