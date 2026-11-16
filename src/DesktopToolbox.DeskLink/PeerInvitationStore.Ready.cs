using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class PeerInvitationStore
{
    private const string ReadyEvidenceFile = "peer-ready-evidence.bin";
    private string ReadyEvidencePath => Path.Combine(directory, ReadyEvidenceFile);

    private sealed record PeerReadyEvidence(int Version, PairingSessionIdentity Identity, Guid StageId,
        int Slot, int DataPortBase, string InvitationDigest, string PendingCiphertextHash, string? BaseCiphertextHash,
        string? ReservationCiphertextHash);

    // Called with the store gate held. Unknown or partially completed exchanges
    // are never cleared by the existing manual pairing path.
    private void RejectExistingPendingForManualSave()
    {
        RejectExistingFinalizationEvidence();
        RejectUnfinishedPromotion();
        RejectExistingReservationEvidence();
        RejectUnretiredCompletionEvidence();
        RejectLinks(directory);
        var pendingPath = Path.Combine(directory, PendingInvitationFile);
        RejectLinks(pendingPath); RejectLinks(ReadyEvidencePath);
        RejectExistingPromotionEvidence();
        if (File.Exists(pendingPath) || Directory.Exists(pendingPath) ||
            File.Exists(ReadyEvidencePath) || Directory.Exists(ReadyEvidencePath))
            throw new InvalidOperationException("Pending pairing must be resolved before replacing the current invitation.");
    }

    private void RejectExistingReadyEvidence(bool allowReservation = false)
    {
        RejectExistingFinalizationEvidence();
        RejectUnfinishedPromotion();
        if (!allowReservation) RejectExistingReservationEvidence();
        RejectUnretiredCompletionEvidence();
        RejectLinks(directory); RejectLinks(ReadyEvidencePath);
        if (File.Exists(ReadyEvidencePath) || Directory.Exists(ReadyEvidencePath))
            throw new InvalidOperationException("Pairing acknowledgement may have been sent; authenticated recovery is required.");
    }

    private void RejectUnfinishedPromotion()
    {
        if (Volatile.Read(ref unfinishedPromotion) != 0)
            throw new InvalidOperationException("An unfinished pairing promotion requires recovery.");
    }

    private void RejectUnretiredCompletionEvidence()
    {
        if (Directory.EnumerateFileSystemEntries(directory, "peer-completed-*.bin").Any())
            throw new InvalidOperationException("上次配对尚在等待主控最终确认，请先恢复。");
    }

    private void RejectExistingPromotionEvidence()
    {
        var path = Path.Combine(directory, "peer-promotion.bin");
        RejectLinks(directory); RejectLinks(path);
        if (File.Exists(path) || Directory.Exists(path))
            throw new InvalidOperationException("A pairing promotion requires authenticated recovery.");
    }

    internal async Task RecordReadyEvidenceAsync(PairingTlsConsentSession.DeliveryLease delivery,
        PairingPrepareReceipt receipt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery); ArgumentNullException.ThrowIfNull(receipt);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            delivery.EnsureCurrent(ct);
            if (delivery.IsHost) throw new InvalidOperationException("Only the peer can record READY.");
            var reservation = await ReadReservationAsync(ct).ConfigureAwait(false);
            if (delivery.RequiresPeerReservation(ct) && reservation is null)
                throw new InvalidOperationException("The confirmed pairing reservation is missing.");
            RejectExistingReadyEvidence(allowReservation: reservation is not null);
            var pending = await ReadPendingAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The pending invitation is missing.");
            if (reservation is not null) ValidateReservedInvitation(delivery, reservation, pending.Identity, pending.Invitation, receipt, ct);
            await RejectAbortedHistoricalIdentityAsync(pending.Identity, ct).ConfigureAwait(false);
            await EnsurePairingHistoryCapacityAsync(ct).ConfigureAwait(false);
            if (pending.Identity != delivery.Identity || !DigestMatches(pending.InvitationDigest, receipt.InvitationDigest) ||
                receipt.StageId == Guid.Empty || receipt.Slot is < 1 or > 4 || receipt.DataPortBase is < 1 or > 65532 ||
                pending.Invitation.DataPort != receipt.DataPortBase + receipt.Slot - 1)
                throw new InvalidOperationException("The pending invitation differs from this session.");
            var currentDigest = invitation is null ? null : ComputePendingDigest(invitation);
            if (currentDigest != pending.BaseInvitationDigest)
                throw new InvalidOperationException("The active invitation changed during pairing.");
            var pendingPath = Path.Combine(directory, PendingInvitationFile);
            var pendingCipher = await ReadReadyEvidenceFileAsync(pendingPath, ct).ConfigureAwait(false);
            var reservationCipher = reservation is null ? null : await ReadReadyEvidenceFileAsync(ReservationPath, ct).ConfigureAwait(false);
            string? baseCipherHash = null;
            RejectLinks(file);
            if (pending.BaseInvitationDigest is null)
            {
                if (File.Exists(file) || Directory.Exists(file)) throw new InvalidOperationException("The active invitation changed.");
            }
            else
            {
                var baseCipher = await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false);
                var basePlain = protector.Unprotect(baseCipher);
                try
                {
                    var active = MultiPeerInvitation.Parse(new UTF8Encoding(false, true).GetString(basePlain));
                    if (!DigestMatches(ComputePendingDigest(active), pending.BaseInvitationDigest))
                        throw new InvalidOperationException("The saved active invitation changed.");
                    baseCipherHash = Convert.ToHexString(SHA256.HashData(baseCipher));
                }
                finally { CryptographicOperations.ZeroMemory(basePlain); }
            }
            var evidence = new PeerReadyEvidence(1, delivery.Identity, receipt.StageId, receipt.Slot,
                receipt.DataPortBase, receipt.InvitationDigest, Convert.ToHexString(SHA256.HashData(pendingCipher)), baseCipherHash,
                reservationCipher is null ? null : Convert.ToHexString(SHA256.HashData(reservationCipher)));
            var plain = JsonSerializer.SerializeToUtf8Bytes(evidence, PendingJson);
            byte[] encrypted;
            try { encrypted = protector.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected pairing evidence is invalid.");
            var temporary = ReadyEvidencePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
                var currentReservation = await ReadReservationAsync(ct).ConfigureAwait(false);
                if (currentReservation != reservation) throw new InvalidOperationException("Pairing reservation changed before READY.");
                if (reservationCipher is not null)
                {
                    var currentCipher = await ReadReadyEvidenceFileAsync(ReservationPath, ct).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(reservationCipher, currentCipher))
                        throw new InvalidOperationException("Pairing reservation evidence changed before READY.");
                }
                if (reservation is not null) ValidateReservedInvitation(delivery, reservation, pending.Identity, pending.Invitation, receipt, ct);
                RejectExistingReadyEvidence(allowReservation: reservation is not null);
                delivery.PublishPeerReadyEvidence(temporary, ReadyEvidencePath, ct);
            }
            finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private async Task<byte[]> ReadReadyEvidenceFileAsync(string path, CancellationToken ct)
    {
        RejectLinks(path);
        if (Directory.Exists(path)) throw new InvalidDataException("Protected pairing evidence path is a directory.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var bytes = new byte[MaximumBytes + 1]; var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), ct).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected pairing evidence is invalid or oversized.");
        return bytes.AsSpan(0, count).ToArray();
    }

    // A received COMMITTED frame cannot authorize promotion until this store's
    // original READY evidence and the still-current invitation baseline agree.
    internal async Task VerifyReadyForCommittedAsync(PairingTlsConsentSession.DeliveryLease delivery,
        PairingPrepareReceipt receipt, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            delivery.EnsureCurrent(ct);
            var status = await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
            if (status.Phase != PairingRecoveryPhase.AwaitingPeer)
                throw new InvalidOperationException("Peer pairing evidence is unavailable.");
            var pending = await ReadPendingAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Peer pending invitation is unavailable.");
            var encrypted = await ReadReadyEvidenceFileAsync(ReadyEvidencePath, ct).ConfigureAwait(false);
            var plain = protector.Unprotect(encrypted);
            try
            {
                if (plain.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Pairing evidence is invalid.");
                using var json = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(plain), new JsonDocumentOptions { MaxDepth = 8 });
                RejectDuplicateFields(json.RootElement); RequireExactFields(json.RootElement, RecoveryReadyFields);
                RequireExactFields(json.RootElement.GetProperty("identity"), IdentityFields);
                var ready = json.RootElement.Deserialize<PeerReadyEvidence>(PendingJson)
                    ?? throw new InvalidDataException("Pairing evidence is empty.");
                if (delivery.IsHost || ready.Identity != delivery.Identity || pending.Identity != delivery.Identity ||
                    ready.StageId != receipt.StageId || ready.Slot != receipt.Slot || ready.DataPortBase != receipt.DataPortBase ||
                    !DigestMatches(ready.InvitationDigest, receipt.InvitationDigest))
                    throw new InvalidOperationException("Peer pairing evidence differs from this session.");
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            delivery.EnsureCurrent(ct);
        }
        finally { gate.Release(); }
    }
}

internal sealed partial class PairingTlsConsentSession
{
    internal sealed partial class DeliveryLease
    {
        internal void PublishPeerReadyEvidence(string temporary, string destination, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                File.Move(temporary, destination, false);
            }
        }
    }
}
