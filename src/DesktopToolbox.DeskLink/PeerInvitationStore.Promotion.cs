using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class PeerInvitationStore
{
    private static readonly string[] PromotionFields = ["version", "identity", "stageId", "slot", "dataPortBase",
        "invitationDigest", "pendingCiphertextHash", "readyCiphertextHash", "baseCiphertextHash"];
    private sealed record PeerPromotionEvidence(int Version, PairingSessionIdentity Identity, Guid StageId,
        int Slot, int DataPortBase, string InvitationDigest, string PendingCiphertextHash,
        string ReadyCiphertextHash, string? BaseCiphertextHash);

    // The original active invitation and both prepared records remain untouched.
    // The protected promotion record only names the already encrypted pending file.
    internal async Task PromoteCommittedAsync(PairingTlsConsentSession.DeliveryLease delivery,
        PairingPrepareReceipt receipt, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            delivery.RequireVerifiedPeerPromotion(this, receipt, ct);
            RejectExistingPromotionEvidence();
            var status = await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
            if (status.Phase != PairingRecoveryPhase.AwaitingPeer)
                throw new InvalidOperationException("Peer pairing evidence is unavailable.");
            var pending = await ReadPendingAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Peer pending invitation is unavailable.");
            var readyCipher = await ReadReadyEvidenceFileAsync(ReadyEvidencePath, ct).ConfigureAwait(false);
            var ready = ParsePromotionReady(readyCipher);
            if (ready.Identity != delivery.Identity || pending.Identity != delivery.Identity ||
                ready.StageId != receipt.StageId || ready.Slot != receipt.Slot || ready.DataPortBase != receipt.DataPortBase ||
                !DigestMatches(ready.InvitationDigest, receipt.InvitationDigest))
                throw new InvalidOperationException("Peer pairing evidence differs from this session.");
            await PublishPromotionLockedAsync(delivery, delivery.Identity, receipt, pending, ready, readyCipher,
                () => delivery.RequireVerifiedPeerPromotion(this, receipt, ct), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task PublishPromotionLockedAsync(PairingTlsConsentSession.DeliveryLease delivery,
        PairingSessionIdentity identity, PairingPrepareReceipt receipt, PendingDocument pending, PeerReadyEvidence ready,
        byte[] readyCipher, Action requireAuthority, CancellationToken ct)
    {
            var evidence = new PeerPromotionEvidence(1, identity, receipt.StageId, receipt.Slot,
                receipt.DataPortBase, receipt.InvitationDigest, ready.PendingCiphertextHash,
                Convert.ToHexString(SHA256.HashData(readyCipher)), ready.BaseCiphertextHash);
            var plain = JsonSerializer.SerializeToUtf8Bytes(evidence, PendingJson);
            byte[] encrypted;
            try { encrypted = protector.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected promotion is invalid.");
            var temporary = RecoveryPromotionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var created = false;
            try
            {
                RejectLinks(temporary);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                {
                    created = true;
                    await output.WriteAsync(encrypted, ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }
                // Recheck after the potentially asynchronous protected write, before publication.
                var status = await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
                if (status.Phase != PairingRecoveryPhase.AwaitingPeer)
                    throw new InvalidOperationException("Peer pairing evidence changed.");
                var recheckedReady = await ReadReadyEvidenceFileAsync(ReadyEvidencePath, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(readyCipher, recheckedReady))
                    throw new InvalidOperationException("Peer pairing acknowledgement changed.");
                RejectExistingPromotionEvidence();
                requireAuthority();
                Volatile.Write(ref unsafePromotion, 1);
                delivery.PublishPeerPromotion(temporary, RecoveryPromotionPath, ct);
                // If this update fails after publication, reopening validates the durable record.
                Volatile.Write(ref unfinishedPromotion, 1);
                SetInvitation(pending.Invitation);
                Volatile.Write(ref unsafePromotion, 0);
            }
            finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }

    private PeerReadyEvidence ParsePromotionReady(byte[] encrypted)
    {
        var plain = protector.Unprotect(encrypted);
        try
        {
            if (plain.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Pairing evidence is invalid.");
            using var json = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(plain), new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateFields(json.RootElement); RequireExactFields(json.RootElement, RecoveryReadyFields);
            RequireExactFields(json.RootElement.GetProperty("identity"), IdentityFields);
            return json.RootElement.Deserialize<PeerReadyEvidence>(PendingJson)
                ?? throw new InvalidDataException("Pairing evidence is empty.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // Called once during OpenAsync after the original active invitation has been read.
    // An unknown/damaged promotion must fail closed rather than resurrect old trust.
    private Task<MultiPeerInvitation?> ReadPromotedInvitationAsync(CancellationToken ct) =>
        ReadPromotedInvitationAsync(invitation, ct);

    private async Task<MultiPeerInvitation?> ReadPromotedInvitationAsync(MultiPeerInvitation? baseline, CancellationToken ct)
    {
        RejectLinks(RecoveryPromotionPath);
        if (Directory.Exists(RecoveryPromotionPath)) throw new InvalidDataException("Pairing promotion path is invalid.");
        if (!File.Exists(RecoveryPromotionPath)) return null;
        var encrypted = await ReadReadyEvidenceFileAsync(RecoveryPromotionPath, ct).ConfigureAwait(false);
        var plain = protector.Unprotect(encrypted);
        PeerPromotionEvidence promotion;
        try
        {
            if (plain.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Pairing promotion is invalid.");
            using var json = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(plain), new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateFields(json.RootElement); RequireExactFields(json.RootElement, PromotionFields);
            RequireExactFields(json.RootElement.GetProperty("identity"), IdentityFields);
            promotion = json.RootElement.Deserialize<PeerPromotionEvidence>(PendingJson)
                ?? throw new InvalidDataException("Pairing promotion is empty.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var pending = await ReadPendingAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Pairing promotion pending invitation is missing.");
        var pendingCipher = await ReadReadyEvidenceFileAsync(Path.Combine(directory, PendingInvitationFile), ct).ConfigureAwait(false);
        var readyCipher = await ReadReadyEvidenceFileAsync(ReadyEvidencePath, ct).ConfigureAwait(false);
        var ready = ParsePromotionReady(readyCipher);
        var reservationCipher = File.Exists(ReservationPath) ? await ReadReadyEvidenceFileAsync(ReservationPath, ct).ConfigureAwait(false) : null;
        byte[]? activeCipher = null;
        RejectLinks(file);
        if (File.Exists(file)) activeCipher = await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false);
        if (promotion.Version != 1 || promotion.Identity != pending.Identity || ready.Version != 1 || ready.Identity != pending.Identity ||
            promotion.StageId == Guid.Empty || promotion.StageId != ready.StageId || promotion.Slot != ready.Slot ||
            promotion.DataPortBase != ready.DataPortBase || promotion.Slot is < 1 or > 4 || promotion.DataPortBase is < 1 or > 65532 ||
            pending.Invitation.DataPort != promotion.DataPortBase + promotion.Slot - 1 ||
            !DigestMatches(promotion.InvitationDigest, pending.InvitationDigest) || !DigestMatches(ready.InvitationDigest, pending.InvitationDigest) ||
            !DigestMatches(promotion.PendingCiphertextHash, Convert.ToHexString(SHA256.HashData(pendingCipher))) ||
            !DigestMatches(ready.PendingCiphertextHash, promotion.PendingCiphertextHash) ||
            !DigestMatches(promotion.ReadyCiphertextHash, Convert.ToHexString(SHA256.HashData(readyCipher))) ||
            promotion.BaseCiphertextHash != ready.BaseCiphertextHash ||
            (ready.ReservationCiphertextHash is null ? reservationCipher is not null : reservationCipher is null ||
                !DigestMatches(ready.ReservationCiphertextHash, Convert.ToHexString(SHA256.HashData(reservationCipher)))) ||
            (promotion.BaseCiphertextHash is null ? activeCipher is not null : activeCipher is null ||
                !DigestMatches(promotion.BaseCiphertextHash, Convert.ToHexString(SHA256.HashData(activeCipher)))) ||
            (pending.BaseInvitationDigest is null ? baseline is not null : baseline is null ||
                !DigestMatches(pending.BaseInvitationDigest, ComputePendingDigest(baseline))))
            throw new InvalidDataException("Pairing promotion evidence changed.");
        return pending.Invitation;
    }
}

internal sealed partial class PairingTlsConsentSession
{
    internal sealed partial class DeliveryLease
    {
        internal void PublishPeerPromotion(string temporary, string destination, CancellationToken ct)
        {
            lock (owner.gate) { owner.CheckDelivery(this, ct); File.Move(temporary, destination, false); }
        }
    }
}
