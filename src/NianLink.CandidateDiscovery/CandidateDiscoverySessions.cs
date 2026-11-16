using DesktopToolbox.DeskLink;

namespace NianLink.CandidateDiscovery;

/// <summary>Windows candidate's explicitly requested, in-memory discovery sessions.</summary>
public sealed class CandidateDiscoverySessions
{
    private CandidateDiscoverySessions(DiscoveryNetworkBinding binding) => Binding = binding;
    public DiscoveryNetworkBinding Binding { get; }

    public static CandidateDiscoverySessions Open(VerifiedLanBinding approvedNetwork)
    {
        ArgumentNullException.ThrowIfNull(approvedNetwork);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Candidate discovery is Windows-only.");
        return new(DiscoveryNetworkBinding.CreateForCurrentNetwork(approvedNetwork));
    }

    public DiscoveryScanSession StartScan(TimeSpan duration) => DiscoveryScanSession.Create(Binding, duration);
    public DiscoveryResponderSession StartResponder(PeerIdentityOffer offer) => DiscoveryResponderSession.Create(Binding, offer);
}
