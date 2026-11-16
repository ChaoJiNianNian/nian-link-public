using DesktopToolbox.DeskLink;
using NianLink.MacCandidateDiscovery;

namespace NianLink.MacResponderCandidate;

/// <summary>An explicitly started, short-lived Mac discovery responder.</summary>
public sealed class MacDiscoveryResponder : IAsyncDisposable
{
    private readonly ResponderRun run;
    private MacDiscoveryResponder(ResponderRun run) => this.run = run;
    public Task Completion => run.Completion;
    public Exception? Error => run.Error;
    public PairingIncomingRequest? GetPendingPairingRequest() => run.GetPendingPairingRequest();
    public bool RejectPairingRequest(Guid id) => run.RejectPairingRequest(id);

    public static async Task<MacDiscoveryResponder> StartAsync(VerifiedLanBinding approved, PeerIdentityOffer offer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approved); ArgumentNullException.ThrowIfNull(offer);
        if (!OperatingSystem.IsMacOS() || offer.Platform != "mac")
            throw new InvalidOperationException("A Mac identity and macOS are required.");
        offer.Validate(DateTimeOffset.UtcNow);
        ct.ThrowIfCancellationRequested();
        MacCandidateNetworkBinding? binding = null;
        MacResponderPolicy? policy = null;
        MacResponderSocket? socket = null;
        try
        {
            binding = await MacCandidateNetworkBinding.CreateForCurrentNetworkAsync(approved, ct).ConfigureAwait(false);
            var network = new BoundNetwork(binding);
            if (!binding.HasLiveNetworkOrigin || !await network.RevalidateAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Approved Mac network is unavailable.");
            ct.ThrowIfCancellationRequested();
            var shared = binding.SharedBinding;
            var session = DiscoveryResponderSession.Create(shared, offer);
            policy = new MacResponderPolicy(session);
            socket = MacResponderSocket.Open(shared);
            ct.ThrowIfCancellationRequested();
            if (binding.RevocationToken.IsCancellationRequested)
                throw new InvalidOperationException("Approved Mac network changed.");
            var run = ResponderRun.StartReal(network, policy, socket, ct, shared, offer,
                token => MacPairingNetworkHandoff.CreateAsync(approved, shared, token));
            binding = null; policy = null; socket = null; // Ownership transferred to the run.
            return new MacDiscoveryResponder(run);
        }
        catch (Exception startup)
        {
            var errors = new List<Exception>();
            try { if (socket is not null) await socket.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { policy?.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { binding?.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("Discovery startup cleanup failed.", [startup, .. errors]);
            throw;
        }
    }

    public ValueTask DisposeAsync() => run.DisposeAsync();

    private sealed class BoundNetwork(MacCandidateNetworkBinding binding) : IResponderNetwork
    {
        public CancellationToken RevocationToken => binding.RevocationToken;
        public async Task<bool> RevalidateAsync(CancellationToken ct)
        {
            if (!await binding.IsStillAvailableAsync(ct).ConfigureAwait(false)) return false;
            ct.ThrowIfCancellationRequested();
            var id = binding.InterfaceId; var index = binding.InterfaceIndex; var address = binding.Address;
            var matches = await Task.Run(() => SingleIPv4Policy.ReadAndMatch(id, index, address), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return matches && !binding.RevocationToken.IsCancellationRequested;
        }
        public void Dispose() => binding.Dispose();
    }
}
