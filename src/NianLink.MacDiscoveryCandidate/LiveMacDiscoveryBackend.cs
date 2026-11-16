using System.Net;
using DesktopToolbox.DeskLink;
using NianLink.MacResponderCandidate;

namespace NianLink.MacDiscoveryCandidate;

internal sealed class LiveMacDiscoveryBackend : IMacDiscoveryUiBackend
{
    public async Task<IReadOnlyList<IPAddress>> GetNetworksAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Mac 局域网发现仅支持 macOS。");
        return await Task.Run(VerifiedLanBinding.GetEligibleAddresses, ct);
    }

    public async Task<IMacDiscoveryUiSession> StartAsync(IPAddress address, PeerIdentityOffer offer, TimeSpan duration, CancellationToken ct)
    {
        if (duration != TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(duration));
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Mac 局域网发现仅支持 macOS。");
        ct.ThrowIfCancellationRequested();
        var approved = await Task.Run(() => VerifiedLanBinding.VerifyCurrentNetwork(address, userApproved: true), ct);
        ct.ThrowIfCancellationRequested();
        try { return new Session(await MacDiscoveryResponder.StartAsync(approved, offer, ct)); }
        catch (AggregateException error) { throw new MacDiscoveryStartupCleanupException(error); }
    }

    private sealed class Session(MacDiscoveryResponder responder) : IMacDiscoveryUiSession
    {
        public Task Completion => responder.Completion;
        public Exception? Error => responder.Error;
#if FILELESS_PAIRING_CANDIDATE
        public PairingIncomingRequest? GetPendingPairingRequest() => responder.GetPendingPairingRequest();
        public bool RejectPairingRequest(Guid id) => responder.RejectPairingRequest(id);
#endif
        public ValueTask DisposeAsync() => responder.DisposeAsync();
    }
}
