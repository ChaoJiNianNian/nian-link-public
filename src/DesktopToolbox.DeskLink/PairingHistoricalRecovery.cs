using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

// Derived only from each endpoint's own protected records. Contains no credential.
internal sealed record PairingHistoricalBinding(PairingSessionIdentity Identity, Guid StageId, int Slot,
    int DataPortBase, string Host, int ControlPort, int KeyboardPort, int DataPort, string InvitationDigest)
{
    internal string Commitment()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, JsonLineProtocol.Json);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public override string ToString() => "[protected historical pairing binding]";
}

// Local hashes make a later re-read detect changes; these hashes never go on the wire.
internal sealed record PairingHistoricalSnapshot(object Owner, PairingRecoveryPhase Phase,
    PairingHistoricalBinding Binding, string PendingHash, string MarkerHash, string? ActiveHash, string? PromotionHash = null, string? ReservationHash = null)
{
    public override string ToString() => "[local historical pairing snapshot]";
}

internal sealed record PairingHistoricalRecoveryResult(bool Matched, PairingRecoveryPhase LocalPhase)
{
    public override string ToString() => "[historical pairing observation]";
}

internal static class PairingHistoricalRecoveryRules
{
    internal static void ValidateBinding(PairingHistoricalBinding value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var id = value.Identity ?? throw new InvalidDataException("Historical identity is missing.");
        DeskflowProfile.ValidateName(id.HostName); DeskflowProfile.ValidateName(id.PeerName);
        if (id.SessionId == Guid.Empty || id.OfferNonce == Guid.Empty || id.TransactionId == Guid.Empty || id.ExpiresAtUtc == default ||
            id.HostName == id.PeerName || DeskLinkFingerprint.Normalize(id.HostFingerprint) != id.HostFingerprint ||
            DeskLinkFingerprint.Normalize(id.PeerFingerprint) != id.PeerFingerprint || value.StageId == Guid.Empty ||
            value.Slot is < 1 or > 4 || value.DataPortBase is < 1 or > 65532 || value.DataPort != value.DataPortBase + value.Slot - 1 ||
            value.ControlPort is < 1 or > 65535 || value.KeyboardPort is < 1 or > 65535 ||
            !System.Net.IPAddress.TryParse(value.Host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            value.InvitationDigest is not { Length: 64 } || value.InvitationDigest.Any(c => !char.IsAsciiHexDigitUpper(c)))
            throw new InvalidDataException("Historical binding is invalid.");
    }

    internal static void RequireFreshConfirmedIdentity(PairingTlsConsentSession.DeliveryLease lease,
        PairingSessionIdentity old, string hostAddress)
    {
        lease.EnsureCurrent();
        var current = lease.Identity;
        if (current.SessionId == old.SessionId || current.OfferNonce == old.OfferNonce ||
            current.TransactionId == old.TransactionId || current.HostName != old.HostName || current.PeerName != old.PeerName ||
            !DeskLinkFingerprint.Matches(current.HostFingerprint, old.HostFingerprint) ||
            !DeskLinkFingerprint.Matches(current.PeerFingerprint, old.PeerFingerprint) ||
            hostAddress != (lease.IsHost ? lease.LocalAddress : lease.RemoteAddress).ToString())
            throw new InvalidOperationException("The fresh connection does not match the protected historical pairing.");
    }
}
