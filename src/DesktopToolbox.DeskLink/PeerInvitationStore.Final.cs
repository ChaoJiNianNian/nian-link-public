using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class PeerInvitationStore
{
    private static readonly string[] FinalEvidenceFields = ["version", "identity", "stageId", "slot", "dataPortBase", "host", "controlPort", "keyboardPort", "dataPort", "invitationDigest", "pendingHash", "readyHash", "promotionHash", "baseHash", "reservationHash"];
    private sealed record PeerFinalEvidence(int Version, PairingSessionIdentity Identity, Guid StageId, int Slot,
        int DataPortBase, string Host, int ControlPort, int KeyboardPort, int DataPort,
        string InvitationDigest, string PendingHash, string ReadyHash, string PromotionHash, string? BaseHash, string? ReservationHash);
    private string FinalizingPath => Path.Combine(directory, "peer-finalizing.bin");
    private string CompletedPath(Guid id) => Path.Combine(directory, "peer-completed-" + id.ToString("N") + ".bin");
    // Internal fault injection for crash-boundary tests. Product callers never set it.
    internal Action<string>? FinalizationCheckpointForTesting { get; set; }

    private void RejectExistingFinalizationEvidence()
    {
        RejectLinks(FinalizingPath);
        if (File.Exists(FinalizingPath) || Directory.Exists(FinalizingPath) || Volatile.Read(ref unsafePromotion) != 0)
            throw new InvalidOperationException("Pairing finalization requires recovery.");
    }

    // FINAL authority comes exclusively from the same live, mutually confirmed session.
    // The protected write-ahead record is published before changing the active file.
    internal async Task FinalizeCommittedAsync(PairingTlsConsentSession.DeliveryLease delivery, PairingPrepareReceipt receipt, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FinalizeCommittedLockedAsync(delivery, delivery.Identity, receipt,
                () => delivery.RequireVerifiedPeerFinal(this, receipt, ct), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task FinalizeCommittedLockedAsync(PairingTlsConsentSession.DeliveryLease delivery,
        PairingSessionIdentity oldIdentity, PairingPrepareReceipt receipt, Action requireAuthority, CancellationToken ct)
    {
            ObjectDisposedException.ThrowIf(disposed, this);
            requireAuthority();
            RejectExistingFinalizationEvidence();
            var completed = Directory.EnumerateFileSystemEntries(directory, "peer-completed-*.bin").ToArray();
            if (completed.Length >= 64) throw new InvalidOperationException("Pairing completion history is full.");
            foreach (var path in completed) RejectLinks(path);
            var baseBytes = File.Exists(file) ? await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false) : null;
            MultiPeerInvitation? baseline = null;
            if (baseBytes is not null) baseline = ParseProtectedInvitation(baseBytes);
            var promoted = await ReadPromotedInvitationAsync(baseline, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Promoted invitation is unavailable.");
            var pending = await ReadPendingAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Pending invitation is unavailable.");
            var readyBytes = await ReadReadyEvidenceFileAsync(ReadyEvidencePath, ct).ConfigureAwait(false);
            var ready = ParsePromotionReady(readyBytes);
            if (promoted != Volatile.Read(ref invitation) || pending.Identity != oldIdentity || ready.Identity != oldIdentity ||
                ready.StageId != receipt.StageId || ready.Slot != receipt.Slot || ready.DataPortBase != receipt.DataPortBase ||
                !DigestMatches(ready.InvitationDigest, receipt.InvitationDigest))
                throw new InvalidOperationException("Final pairing differs from promotion evidence.");
            var pendingBytes = await ReadReadyEvidenceFileAsync(Path.Combine(directory, PendingInvitationFile), ct).ConfigureAwait(false);
            var promotionBytes = await ReadReadyEvidenceFileAsync(RecoveryPromotionPath, ct).ConfigureAwait(false);
            var reservation = await ReadReservationAsync(ct).ConfigureAwait(false);
            byte[]? reservationBytes = null;
            if (reservation is not null)
            {
                if (reservation.Decision != "reserved" || reservation.Binding.Identity != oldIdentity || reservation.Binding.StageId != receipt.StageId ||
                    reservation.Binding.Slot != receipt.Slot || reservation.Binding.DataPortBase != receipt.DataPortBase ||
                    !DigestMatches(reservation.Binding.InvitationDigest, receipt.InvitationDigest) || reservation.Binding.Host != promoted.Host ||
                    reservation.Binding.ControlPort != promoted.Port || reservation.Binding.KeyboardPort != promoted.KeyboardPort ||
                    reservation.Binding.DataPort != promoted.DataPort)
                    throw new InvalidOperationException("Final pairing differs from its reservation.");
                reservationBytes = await ReadReadyEvidenceFileAsync(ReservationPath, ct).ConfigureAwait(false);
            }
            var evidence = new PeerFinalEvidence(1, oldIdentity, receipt.StageId, receipt.Slot, receipt.DataPortBase,
                promoted.Host, promoted.Port, promoted.KeyboardPort, promoted.DataPort,
                receipt.InvitationDigest, HashFinal(pendingBytes), HashFinal(readyBytes), HashFinal(promotionBytes),
                baseBytes is null ? null : HashFinal(baseBytes), reservationBytes is null ? null : HashFinal(reservationBytes));
            var plain = JsonSerializer.SerializeToUtf8Bytes(evidence, PendingJson);
            byte[] encrypted;
            try { encrypted = protector.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Finalization evidence is invalid.");
            var temporary = FinalizingPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await WriteNewFinalFileAsync(temporary, encrypted, ct).ConfigureAwait(false);
                // Revalidate all source evidence after async writes and before publishing authority.
                var checkedPromotion = await ReadPromotedInvitationAsync(baseline, ct).ConfigureAwait(false);
                if (checkedPromotion != promoted || HashFinal(await ReadReadyEvidenceFileAsync(RecoveryPromotionPath, ct)) != evidence.PromotionHash ||
                    HashFinal(await ReadReadyEvidenceFileAsync(ReadyEvidencePath, ct)) != evidence.ReadyHash ||
                    HashFinal(await ReadReadyEvidenceFileAsync(Path.Combine(directory, PendingInvitationFile), ct)) != evidence.PendingHash ||
                    (reservationBytes is null ? File.Exists(ReservationPath) :
                        HashFinal(await ReadReadyEvidenceFileAsync(ReservationPath, ct)) != evidence.ReservationHash))
                    throw new InvalidOperationException("Finalization source changed.");
                requireAuthority();
                Volatile.Write(ref unsafePromotion, 1);
                delivery.PublishPeerFinalEvidence(temporary, FinalizingPath, ct);
                FinalizationCheckpointForTesting?.Invoke("journal");
                await ResumeFinalizationLockedAsync(ct).ConfigureAwait(false);
                requireAuthority();
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string HashFinal(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private MultiPeerInvitation ParseProtectedInvitation(byte[] encrypted)
    {
        var plain = protector.Unprotect(encrypted);
        try { return MultiPeerInvitation.Parse(new UTF8Encoding(false, true).GetString(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private PeerFinalEvidence ParseFinalEvidence(byte[] encrypted)
    {
        var plain = protector.Unprotect(encrypted);
        try
        {
            if (plain.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Finalization evidence is invalid.");
            using var json = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(plain), new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateFields(json.RootElement); RequireExactFields(json.RootElement, FinalEvidenceFields);
            RequireExactFields(json.RootElement.GetProperty("identity"), IdentityFields);
            var value = json.RootElement.Deserialize<PeerFinalEvidence>(PendingJson) ?? throw new InvalidDataException("Finalization evidence is empty.");
            if (value.Version != 1 || value.Identity is null || value.Identity.TransactionId == Guid.Empty || value.StageId == Guid.Empty ||
                value.Slot is < 1 or > 4 || value.DataPortBase is < 1 or > 65532 ||
                value.ControlPort is < 1 or > 65535 || value.KeyboardPort is < 1 or > 65535 ||
                value.DataPort != value.DataPortBase + value.Slot - 1 || string.IsNullOrWhiteSpace(value.Host))
                throw new InvalidDataException("Finalization identity is invalid.");
            ValidateDigest(value.InvitationDigest); ValidateDigest(value.PendingHash); ValidateDigest(value.ReadyHash); ValidateDigest(value.PromotionHash);
            if (value.BaseHash is not null) ValidateDigest(value.BaseHash);
            if (value.ReservationHash is not null) ValidateDigest(value.ReservationHash);
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task WriteNewFinalFileAsync(string path, byte[] bytes, CancellationToken ct)
    {
        RejectLinks(directory); RejectLinks(path);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await output.WriteAsync(bytes, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    // Each deletion is safe to retry only after the exact completion record and
    // the exact candidate are durable. Unknown/mismatching evidence is retained.
    private async Task ResumeFinalizationLockedAsync(CancellationToken ct)
    {
        RejectLinks(FinalizingPath);
        if (Directory.Exists(FinalizingPath)) throw new InvalidDataException("Finalization path is invalid.");
        if (!File.Exists(FinalizingPath)) return;
        Volatile.Write(ref unsafePromotion, 1);
        var finalBytes = await ReadReadyEvidenceFileAsync(FinalizingPath, ct).ConfigureAwait(false);
        var evidence = ParseFinalEvidence(finalBytes);
        var completedPath = CompletedPath(evidence.Identity.TransactionId);
        RejectLinks(completedPath);
        if (Directory.Exists(completedPath)) throw new InvalidDataException("Completion path is invalid.");
        var completed = File.Exists(completedPath);
        if (completed)
        {
            var completedBytes = await ReadReadyEvidenceFileAsync(completedPath, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(finalBytes, completedBytes))
                throw new InvalidDataException("Completion record differs from finalization.");
        }
        var pendingPath = Path.Combine(directory, PendingInvitationFile);
        var paths = new[] { pendingPath, ReadyEvidencePath, RecoveryPromotionPath };
        var hashes = new[] { evidence.PendingHash, evidence.ReadyHash, evidence.PromotionHash };
        for (var i = 0; i < paths.Length; i++)
        {
            RejectLinks(paths[i]);
            if (Directory.Exists(paths[i])) throw new InvalidDataException("Finalization source path is invalid.");
            if (!File.Exists(paths[i])) { if (!completed) throw new InvalidDataException("Finalization source is missing."); continue; }
            if (!DigestMatches(HashFinal(await ReadReadyEvidenceFileAsync(paths[i], ct)), hashes[i]))
                throw new InvalidDataException("Finalization source changed.");
        }
        RejectLinks(ReservationPath);
        if (Directory.Exists(ReservationPath)) throw new InvalidDataException("Reservation path is invalid.");
        if (evidence.ReservationHash is null ? File.Exists(ReservationPath) :
            !File.Exists(ReservationPath) ? !completed :
            !DigestMatches(HashFinal(await ReadReadyEvidenceFileAsync(ReservationPath, ct)), evidence.ReservationHash))
            throw new InvalidDataException("Finalization reservation changed.");
        RejectLinks(file);
        var activeBytes = File.Exists(file) ? await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false) : null;
        var active = activeBytes is null ? null : ParseProtectedInvitation(activeBytes);
        var activeIsCandidate = active is not null && DigestMatches(ComputePendingDigest(active), evidence.InvitationDigest);
        var activeIsBase = evidence.BaseHash is null ? activeBytes is null : activeBytes is not null && DigestMatches(HashFinal(activeBytes), evidence.BaseHash);
        if (!activeIsCandidate && (!activeIsBase || completed)) throw new InvalidDataException("Finalization active invitation changed.");
        var pending = await ReadPendingAsync(ct).ConfigureAwait(false);
        var candidate = pending?.Invitation ?? (activeIsCandidate ? active : null)
            ?? throw new InvalidDataException("Finalization candidate is missing.");
        ValidatePending(evidence.Identity, candidate, evidence.InvitationDigest);
        if (candidate.Host != evidence.Host || candidate.Port != evidence.ControlPort || candidate.KeyboardPort != evidence.KeyboardPort ||
            candidate.DataPort != evidence.DataPort || pending is not null && pending.Identity != evidence.Identity)
            throw new InvalidDataException("Finalization candidate differs from the transaction.");
        if (!activeIsCandidate)
        {
            var plain = Encoding.UTF8.GetBytes(candidate.ToJson());
            byte[] encrypted;
            try { encrypted = protector.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected invitation is invalid.");
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await WriteNewFinalFileAsync(temporary, encrypted, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested(); RejectLinks(file);
                FinalizationCheckpointForTesting?.Invoke("active-prepublish");
                var currentBytes = File.Exists(file) ? await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false) : null;
                if (activeBytes is null ? currentBytes is not null : currentBytes is null ||
                    !CryptographicOperations.FixedTimeEquals(activeBytes, currentBytes))
                    throw new InvalidOperationException("The active invitation changed before finalization.");
                ct.ThrowIfCancellationRequested(); RejectLinks(file);
                File.Move(temporary, file, true);
                FinalizationCheckpointForTesting?.Invoke("active");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (!completed)
        {
            var temporary = completedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await WriteNewFinalFileAsync(temporary, finalBytes, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested(); RejectLinks(completedPath);
                File.Move(temporary, completedPath, false);
                FinalizationCheckpointForTesting?.Invoke("receipt");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        async Task VerifyDurableCompletionAsync()
        {
            var currentActive = ParseProtectedInvitation(await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false));
            if (currentActive != candidate) throw new InvalidOperationException("The finalized invitation changed.");
            var currentReceipt = await ReadReadyEvidenceFileAsync(completedPath, ct).ConfigureAwait(false);
            var currentJournal = await ReadReadyEvidenceFileAsync(FinalizingPath, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(finalBytes, currentReceipt) ||
                !CryptographicOperations.FixedTimeEquals(finalBytes, currentJournal))
                throw new InvalidOperationException("The finalization authority changed.");
        }
        var cleanup = new List<(string Path, string Hash, string Name)> {
            (RecoveryPromotionPath, evidence.PromotionHash, "promotion"),
            (ReadyEvidencePath, evidence.ReadyHash, "ready"),
            (pendingPath, evidence.PendingHash, "pending") };
        if (evidence.ReservationHash is not null) cleanup.Insert(2, (ReservationPath, evidence.ReservationHash, "reservation"));
        foreach (var (path, hash, name) in cleanup)
        {
            ct.ThrowIfCancellationRequested(); RejectLinks(path);
            FinalizationCheckpointForTesting?.Invoke(name + "-predelete");
            await VerifyDurableCompletionAsync().ConfigureAwait(false);
            if (File.Exists(path))
            {
                if (!DigestMatches(HashFinal(await ReadReadyEvidenceFileAsync(path, ct).ConfigureAwait(false)), hash))
                    throw new InvalidOperationException("Finalization source changed before cleanup.");
                RejectLinks(path); File.Delete(path);
            }
            FinalizationCheckpointForTesting?.Invoke(name);
        }
        await VerifyDurableCompletionAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); RejectLinks(FinalizingPath); File.Delete(FinalizingPath);
        FinalizationCheckpointForTesting?.Invoke("journal-deleted");
        SetInvitation(candidate);
        Volatile.Write(ref unfinishedPromotion, 0);
        Volatile.Write(ref unsafePromotion, 0);
    }
}
