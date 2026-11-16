using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class PeerInvitationStore
{
    private static readonly string[] RecoveryReadyFields = ["version", "identity", "stageId", "slot", "dataPortBase",
        "invitationDigest", "pendingCiphertextHash", "baseCiphertextHash", "reservationCiphertextHash"];
    private string RecoveryPromotionPath => Path.Combine(directory, "peer-promotion.bin");

    /// <summary>Reads this peer's own protected evidence; the result does not authenticate the host.</summary>
    public async Task<PairingRecoveryStatus> GetPairingRecoveryStatusAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>Explicitly discard only a pending invitation for which READY has not been recorded.</summary>
    public async Task<bool> AbandonUnsentPairingAsync(PairingRecoveryStatus reviewed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var status = await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
            if (!reviewed.Matches(status)) return false;
            ct.ThrowIfCancellationRequested();
            // READY records evidence under this same gate before any acknowledgement can be sent.
            RejectExistingReadyEvidence();
            RejectLinks(RecoveryPromotionPath);
            if (File.Exists(RecoveryPromotionPath) || Directory.Exists(RecoveryPromotionPath)) return false;
            var path = Path.Combine(directory, PendingInvitationFile);
            RejectLinks(path); File.Delete(path);
            return true;
        }
        finally { gate.Release(); }
    }

    private async Task<PairingRecoveryStatus> InspectPairingRecoveryLockedAsync(CancellationToken ct)
    {
        PairingRecoveryStatus Problem() => new(this, PairingRecoveryPhase.EvidenceProblem, null);
        try
        {
            ct.ThrowIfCancellationRequested();
            _ = await ReadRetiredAsync(ct).ConfigureAwait(false);
            var pendingPath = Path.Combine(directory, PendingInvitationFile);
            RejectLinks(directory); RejectLinks(file); RejectLinks(pendingPath);
            RejectLinks(ReadyEvidencePath); RejectLinks(RecoveryPromotionPath); RejectLinks(FinalizingPath);
            if (Volatile.Read(ref unsafePromotion) != 0 || File.Exists(FinalizingPath) || Directory.Exists(FinalizingPath)) return Problem();
            if (Directory.Exists(file) || Directory.Exists(pendingPath) || Directory.Exists(ReadyEvidencePath) ||
                Directory.Exists(RecoveryPromotionPath)) return Problem();

            var reservation = await ReadReservationAsync(ct).ConfigureAwait(false);
            if (reservation is not null)
            {
                if (Directory.EnumerateFileSystemEntries(directory, "peer-completed-*.bin").Any()) return Problem();
                if (!File.Exists(pendingPath))
                {
                    if (File.Exists(ReadyEvidencePath) || File.Exists(RecoveryPromotionPath)) return Problem();
                    var baseline = File.Exists(file) ? ParseProtectedInvitation(await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false)) : null;
                    if (baseline != Volatile.Read(ref invitation)) return Problem();
                    return new(this, PairingRecoveryPhase.AwaitingPeer, reservation.Binding.Identity.HostName,
                        reservation.Binding.Identity.TransactionId, reservation.Binding.StageId);
                }
                var reservedPending = await ReadPendingAsync(ct).ConfigureAwait(false);
                var binding = reservation.Binding;
                if (reservation.Decision == "refused" && (File.Exists(ReadyEvidencePath) || File.Exists(RecoveryPromotionPath))) return Problem();
                if (reservedPending is null || reservedPending.Identity != binding.Identity ||
                    !DigestMatches(reservedPending.InvitationDigest, binding.InvitationDigest) ||
                    reservedPending.Invitation.Host != binding.Host || reservedPending.Invitation.Port != binding.ControlPort ||
                    reservedPending.Invitation.KeyboardPort != binding.KeyboardPort || reservedPending.Invitation.DataPort != binding.DataPort)
                    return Problem();
            }

            if (File.Exists(RecoveryPromotionPath))
            {
                MultiPeerInvitation? baseline = null;
                if (File.Exists(file)) baseline = ParseProtectedInvitation(await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false));
                var promoted = await ReadPromotedInvitationAsync(baseline, ct).ConfigureAwait(false);
                if (promoted is null || promoted != Volatile.Read(ref invitation)) return Problem();
                var pendingPromoted = await ReadPendingAsync(ct).ConfigureAwait(false);
                if (pendingPromoted is null) return Problem();
                var readyPromoted = ParsePromotionReady(await ReadReadyEvidenceFileAsync(ReadyEvidencePath, ct).ConfigureAwait(false));
                if (reservation is not null && (readyPromoted.StageId != reservation.Binding.StageId || readyPromoted.Slot != reservation.Binding.Slot ||
                    readyPromoted.DataPortBase != reservation.Binding.DataPortBase)) return Problem();
                return new(this, PairingRecoveryPhase.CommittedAwaitingPeer, pendingPromoted.Identity.HostName,
                    pendingPromoted.Identity.TransactionId, readyPromoted.StageId);
            }

            byte[]? activeCipher = null;
            MultiPeerInvitation? diskActive = null;
            if (File.Exists(file))
            {
                activeCipher = await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false);
                var activePlain = protector.Unprotect(activeCipher);
                try { diskActive = MultiPeerInvitation.Parse(new UTF8Encoding(false, true).GetString(activePlain)); }
                finally { CryptographicOperations.ZeroMemory(activePlain); }
            }
            if (diskActive != invitation) return Problem();

            var pending = await ReadPendingAsync(ct).ConfigureAwait(false);
            var readyExists = File.Exists(ReadyEvidencePath);
            if (pending is null)
            {
                if (readyExists) return Problem();
                var completions = Directory.EnumerateFileSystemEntries(directory, "peer-completed-*.bin").ToArray();
                if (completions.Length > 64) return Problem();
                PeerFinalEvidence? matching = null;
                foreach (var path in completions)
                {
                    var record = ParseFinalEvidence(await ReadReadyEvidenceFileAsync(path, ct).ConfigureAwait(false));
                    if (!string.Equals(path, CompletedPath(record.Identity.TransactionId), StringComparison.OrdinalIgnoreCase)) return Problem();
                    if (diskActive is not null && DigestMatches(ComputePendingDigest(diskActive), record.InvitationDigest))
                    {
                        if (matching is not null) return Problem();
                        ValidatePending(record.Identity, diskActive, record.InvitationDigest);
                        matching = record;
                    }
                }
                if (completions.Length == 0) return new(this, PairingRecoveryPhase.None, null);
                return matching is null ? Problem() : new(this, PairingRecoveryPhase.CompletedAwaitingHost,
                    matching.Identity.HostName, matching.Identity.TransactionId, matching.StageId);
            }
            var activeDigest = diskActive is null ? null : ComputePendingDigest(diskActive);
            if (pending.BaseInvitationDigest is null ? activeDigest is not null :
                activeDigest is null || !DigestMatches(pending.BaseInvitationDigest, activeDigest)) return Problem();
            var pendingCipher = await ReadReadyEvidenceFileAsync(pendingPath, ct).ConfigureAwait(false);
            if (!readyExists) return reservation is null
                ? new(this, PairingRecoveryPhase.Unsent, pending.Identity.HostName,
                    pending.Identity.TransactionId, pendingDigest: SHA256.HashData(pendingCipher))
                : new(this, PairingRecoveryPhase.AwaitingPeer, pending.Identity.HostName,
                    pending.Identity.TransactionId, reservation.Binding.StageId, SHA256.HashData(pendingCipher));

            var readyCipher = await ReadReadyEvidenceFileAsync(ReadyEvidencePath, ct).ConfigureAwait(false);
            var readyPlain = protector.Unprotect(readyCipher);
            PeerReadyEvidence ready;
            try
            {
                if (readyPlain.Length is <= 0 or > MaximumBytes) return Problem();
                using var json = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(readyPlain), new JsonDocumentOptions { MaxDepth = 8 });
                var root = json.RootElement;
                RejectDuplicateFields(root); RequireExactFields(root, RecoveryReadyFields);
                RequireExactFields(root.GetProperty("identity"), IdentityFields);
                ready = root.Deserialize<PeerReadyEvidence>(PendingJson) ?? throw new InvalidDataException("Pairing evidence is empty.");
            }
            finally { CryptographicOperations.ZeroMemory(readyPlain); }
            if (ready.Version != 1 || ready.Identity != pending.Identity || ready.StageId == Guid.Empty ||
                ready.Slot is < 1 or > 4 || ready.DataPortBase is < 1 or > 65532 ||
                pending.Invitation.DataPort != ready.DataPortBase + ready.Slot - 1 ||
                !DigestMatches(ready.InvitationDigest, pending.InvitationDigest)) return Problem();
            if (reservation is not null && (ready.StageId != reservation.Binding.StageId || ready.Slot != reservation.Binding.Slot ||
                ready.DataPortBase != reservation.Binding.DataPortBase)) return Problem();
            var reservationBytes = reservation is null ? null : await ReadReadyEvidenceFileAsync(ReservationPath, ct).ConfigureAwait(false);
            if (ready.ReservationCiphertextHash is null ? reservationBytes is not null : reservationBytes is null ||
                !DigestMatches(ready.ReservationCiphertextHash, Convert.ToHexString(SHA256.HashData(reservationBytes)))) return Problem();
            if (!DigestMatches(ready.PendingCiphertextHash, Convert.ToHexString(SHA256.HashData(pendingCipher))) ||
                (ready.BaseCiphertextHash is null ? activeCipher is not null :
                    activeCipher is null || !DigestMatches(ready.BaseCiphertextHash, Convert.ToHexString(SHA256.HashData(activeCipher)))))
                return Problem();
            return new(this, PairingRecoveryPhase.AwaitingPeer, pending.Identity.HostName, pending.Identity.TransactionId, ready.StageId);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or
            JsonException or ArgumentException or FormatException or InvalidOperationException or OverflowException or DecoderFallbackException)
        { return Problem(); }
    }
}
