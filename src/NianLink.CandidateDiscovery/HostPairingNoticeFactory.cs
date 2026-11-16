using DesktopToolbox.DeskLink;

namespace NianLink.CandidateDiscovery;

/// <summary>Creates one non-secret request for the actual selected candidate and approved network.</summary>
internal static class HostPairingNoticeFactory
{
    internal static PairingDiscoveryNotice Create(CandidateReviewSelection selection, DiscoveryNetworkBinding binding,
        string hostName, string hostFingerprint, int port, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(selection); ArgumentNullException.ThrowIfNull(binding);
        if (!selection.Lease.IsBoundTo(binding) || !selection.Lease.IsFresh || !binding.IsStillAvailable() ||
            selection.ExpiresAtUtc <= now || selection.Offer.ExpiresAtUtc <= now ||
            !new DiscoverySubnet(binding.Address, binding.SubnetMask).Allows(selection.SourceAddress))
            throw new InvalidOperationException("The selected device or approved network is no longer available.");
        selection.Offer.Validate(now);
        var expires = new[] { now.AddSeconds(25), selection.Offer.ExpiresAtUtc }.Min();
        // Leave a small margin for a peer whose wall clock is behind the host.
        var sessionExpires = new[] { now.AddSeconds(110), selection.Offer.ExpiresAtUtc }.Min();
        var notice = new PairingDiscoveryNotice(hostName, selection.Offer.Name, hostFingerprint,
            Guid.ParseExact(selection.Offer.Nonce, "N"), Guid.NewGuid(), Guid.NewGuid(), port, expires, sessionExpires);
        _ = notice.Encode(now); // Validate before exposing any request to the transport.
        return notice;
    }
}
