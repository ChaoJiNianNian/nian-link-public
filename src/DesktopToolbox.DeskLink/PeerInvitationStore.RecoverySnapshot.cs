using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class PeerInvitationStore
{
    internal async Task<PairingHistoricalSnapshot> CaptureHistoricalRecoveryAsync(
        PairingTlsConsentSession.DeliveryLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<PairingHistoricalSnapshot> CaptureHistoricalRecoveryLockedAsync(
        PairingTlsConsentSession.DeliveryLease lease, CancellationToken ct)
    {
            ObjectDisposedException.ThrowIf(disposed, this); lease.EnsureCurrent(ct);
            if (lease.IsHost) throw new InvalidOperationException("Only the peer can inspect its recovery evidence.");
            var state = await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
            byte[]? promotionCipher = null;
            var phase = state.Phase;
            RejectLinks(RecoveryPromotionPath);
            if (File.Exists(RecoveryPromotionPath))
            {
                if (Volatile.Read(ref unsafePromotion) != 0)
                    throw new InvalidOperationException("Pairing promotion evidence is unavailable.");
                MultiPeerInvitation? baseline = null;
                if (File.Exists(file))
                {
                    var cipher = await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false);
                    var plain = protector.Unprotect(cipher);
                    try { baseline = MultiPeerInvitation.Parse(new UTF8Encoding(false, true).GetString(plain)); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                var promoted = await ReadPromotedInvitationAsync(baseline, ct).ConfigureAwait(false);
                if (promoted is null || promoted != Volatile.Read(ref this.invitation))
                    throw new InvalidOperationException("Pairing promotion changed.");
                promotionCipher = await ReadReadyEvidenceFileAsync(RecoveryPromotionPath, ct).ConfigureAwait(false);
                phase = PairingRecoveryPhase.CommittedAwaitingPeer;
            }
            else if (state.Phase != PairingRecoveryPhase.AwaitingPeer)
                throw new InvalidOperationException("Historical peer pairing evidence is unavailable.");
            var pending = await ReadPendingAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Historical pending invitation is unavailable.");
            var readyCipher = await ReadReadyEvidenceFileAsync(ReadyEvidencePath, ct).ConfigureAwait(false);
            var readyPlain = protector.Unprotect(readyCipher);
            PeerReadyEvidence ready;
            try { ready = JsonSerializer.Deserialize<PeerReadyEvidence>(readyPlain, PendingJson) ?? throw new InvalidDataException("Pairing evidence is empty."); }
            finally { CryptographicOperations.ZeroMemory(readyPlain); }
            var invitation = pending.Invitation;
            PairingHistoricalRecoveryRules.RequireFreshConfirmedIdentity(lease, pending.Identity, invitation.Host);
            var binding = new PairingHistoricalBinding(pending.Identity, ready.StageId, ready.Slot,
                ready.DataPortBase, invitation.Host, invitation.Port, invitation.KeyboardPort,
                invitation.DataPort, pending.InvitationDigest);
            var pendingCipher = await ReadReadyEvidenceFileAsync(Path.Combine(directory, PendingInvitationFile), ct).ConfigureAwait(false);
            var active = File.Exists(file) ? await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false) : null;
            var reservation = File.Exists(ReservationPath) ? await ReadReadyEvidenceFileAsync(ReservationPath, ct).ConfigureAwait(false) : null;
            lease.EnsureCurrent(ct);
            return new(this, phase, binding, Convert.ToHexString(SHA256.HashData(pendingCipher)),
                Convert.ToHexString(SHA256.HashData(readyCipher)), active is null ? null : Convert.ToHexString(SHA256.HashData(active)),
                promotionCipher is null ? null : Convert.ToHexString(SHA256.HashData(promotionCipher)),
                reservation is null ? null : Convert.ToHexString(SHA256.HashData(reservation)));
    }
}
