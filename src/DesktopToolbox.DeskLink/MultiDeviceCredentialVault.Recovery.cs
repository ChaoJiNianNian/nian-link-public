using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

/// <summary>Local evidence only; none of these states authenticates a peer.</summary>
public enum PairingRecoveryPhase
{
    None,
    Unsent,
    AwaitingPeer,
    CommittedAwaitingPeer,
    AppliedCleanup,
    AbortedCleanup,
    CompletedAwaitingHost,
    EvidenceProblem
}

/// <summary>A non-secret view issued by the store that read the protected evidence.</summary>
public sealed class PairingRecoveryStatus
{
    private readonly object owner;
    private readonly Guid transactionId;
    private readonly Guid? stageId;
    private readonly byte[]? pendingDigest;
    private readonly byte[]? markerDigest;
    internal PairingRecoveryStatus(object owner, PairingRecoveryPhase phase, string? peerName,
        Guid transactionId = default, Guid? stageId = null, byte[]? pendingDigest = null, byte[]? markerDigest = null)
    {
        this.owner = owner; Phase = phase; PeerName = peerName; this.transactionId = transactionId;
        this.stageId = stageId; this.pendingDigest = pendingDigest?.ToArray(); this.markerDigest = markerDigest?.ToArray();
    }
    public PairingRecoveryPhase Phase { get; }
    public string? PeerName { get; }
    internal bool MatchesFinishedDelivery(object expectedOwner, Guid expectedTransaction, Guid expectedStage) =>
        ReferenceEquals(owner, expectedOwner) && Phase == PairingRecoveryPhase.AppliedCleanup &&
        transactionId != Guid.Empty && transactionId == expectedTransaction && stageId == expectedStage;
    internal bool MatchesAbortedDelivery(object expectedOwner, Guid expectedTransaction, Guid expectedStage) =>
        ReferenceEquals(owner, expectedOwner) && Phase == PairingRecoveryPhase.AbortedCleanup &&
        transactionId != Guid.Empty && transactionId == expectedTransaction && stageId == expectedStage;
    internal bool Matches(PairingRecoveryStatus other) => ReferenceEquals(owner, other.owner) &&
        Phase == PairingRecoveryPhase.Unsent && other.Phase == PairingRecoveryPhase.Unsent &&
        transactionId != Guid.Empty && transactionId == other.transactionId && stageId == other.stageId &&
        pendingDigest is not null && other.pendingDigest is not null &&
        CryptographicOperations.FixedTimeEquals(pendingDigest, other.pendingDigest);
    internal bool MatchesAppliedCleanup(PairingRecoveryStatus other) => ReferenceEquals(owner, other.owner) &&
        Phase == PairingRecoveryPhase.AppliedCleanup && other.Phase == PairingRecoveryPhase.AppliedCleanup &&
        transactionId != Guid.Empty && transactionId == other.transactionId && stageId == other.stageId &&
        pendingDigest is not null && other.pendingDigest is not null &&
        CryptographicOperations.FixedTimeEquals(pendingDigest, other.pendingDigest) &&
        (markerDigest is null ? other.markerDigest is null : other.markerDigest is not null &&
            CryptographicOperations.FixedTimeEquals(markerDigest, other.markerDigest));
    internal bool MatchesAbortedCleanup(PairingRecoveryStatus other) => ReferenceEquals(owner, other.owner) &&
        Phase == PairingRecoveryPhase.AbortedCleanup && other.Phase == PairingRecoveryPhase.AbortedCleanup &&
        transactionId != Guid.Empty && transactionId == other.transactionId && stageId == other.stageId &&
        pendingDigest is not null && other.pendingDigest is not null &&
        CryptographicOperations.FixedTimeEquals(pendingDigest, other.pendingDigest) &&
        (markerDigest is null ? other.markerDigest is null : other.markerDigest is not null &&
            CryptographicOperations.FixedTimeEquals(markerDigest, other.markerDigest));
}

public sealed partial class MultiDeviceCredentialVault
{
    public async Task<PairingRecoveryStatus> GetPairingRecoveryStatusAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Discards only an actual local pending grant whose protected sending marker does not exist.
    /// The caller cannot supply a snapshot or identity as authority. Existing trust is unchanged.
    /// </summary>
    public async Task<bool> AbandonUnsentPairingAsync(PairingRecoveryStatus reviewed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var status = await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
            if (!reviewed.Matches(status)) return false;
            ct.ThrowIfCancellationRequested();
            // PREPARE creates its marker under this same gate before sending any invitation.
            RejectExistingPrepareMarker();
            RejectLinks(PendingGrantPath);
            File.Delete(PendingGrantPath);
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
            if (Volatile.Read(ref writeUncertain) != 0) return Problem();
            RejectLinks(directory); RejectLinks(PendingGrantPath); RejectLinks(PrepareMarkerPath); RejectLinks(file);
            if (Directory.Exists(PendingGrantPath) || Directory.Exists(PrepareMarkerPath) || Directory.Exists(file)) return Problem();
            if (!await ActiveVaultMatchesDiskLockedAsync(ct).ConfigureAwait(false)) return Problem();
            var snapshot = await ReadPendingGrantSnapshotAsync(ct).ConfigureAwait(false);
            var pending = snapshot.Document;
            var markerExists = File.Exists(PrepareMarkerPath);
            if (pending is null)
            {
                // Neither COMMITTED nor APPLIED proves that the final exchange was
                // received by both sides. Missing evidence must remain an error.
                if (markerExists || vaultSnapshot.Transactions.Any(x => x.State is "committed" or "applied" or "finished")) return Problem();
                return new(this, PairingRecoveryPhase.None, null);
            }

            // Transaction identifiers already present in the active ledger cannot be ignored,
            // even if the marker is missing or an entry has been revoked.
            var related = vaultSnapshot.Transactions.Where(x => x.TransactionId == pending.Identity.TransactionId ||
                x.StageId == pending.StageId || x.SessionId == pending.Identity.SessionId ||
                x.OfferNonce == pending.Identity.OfferNonce).ToArray();
            if (!markerExists)
            {
                if (snapshot.Ciphertext is not null && related.Length == 1 && related[0].State == "aborted" &&
                    MatchesCommittedRecovery(related[0] with { State = "committed" }, pending))
                    return new(this, PairingRecoveryPhase.AbortedCleanup, pending.Identity.PeerName,
                        pending.Identity.TransactionId, pending.StageId, SHA256.HashData(snapshot.Ciphertext));
                if (snapshot.Ciphertext is not null && related.Length == 1 && related[0].State == "retired" &&
                    MatchesCommittedRecovery(related[0], pending))
                {
                    ValidateFrozenInvitation(pending);
                    return new(this, PairingRecoveryPhase.AppliedCleanup, pending.Identity.PeerName,
                        pending.Identity.TransactionId, pending.StageId, SHA256.HashData(snapshot.Ciphertext));
                }
                if (snapshot.Ciphertext is null || related.Length != 0 || ToMetadata(pending, DateTimeOffset.UtcNow).HasConflict) return Problem();
                return new(this, PairingRecoveryPhase.Unsent, pending.Identity.PeerName, pending.Identity.TransactionId,
                    pending.StageId, SHA256.HashData(snapshot.Ciphertext));
            }
            if (snapshot.Ciphertext is null || pending.Frozen is null) return Problem();
            ValidateFrozenInvitation(pending);
            var markerBytes = await ReadBoundedVaultFileAsync(PrepareMarkerPath, ct).ConfigureAwait(false);
            var plain = protector.Unprotect(markerBytes);
            PrepareMarkerDocument marker;
            try
            {
                if (plain.Length is <= 0 or > MaxFileBytes) return Problem();
                using var json = JsonDocument.Parse(plain, new JsonDocumentOptions { MaxDepth = 8 });
                var root = json.RootElement;
                if (!HasExactPendingFields(root, PrepareMarkerFields) ||
                    !HasExactPendingFields(root.GetProperty("identity"), PendingIdentityFields)) return Problem();
                marker = root.Deserialize<PrepareMarkerDocument>(Json) ?? throw new InvalidDataException("Pairing evidence is empty.");
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (marker.Version != 1 || marker.StageId != pending.StageId || marker.Identity != pending.Identity ||
                !FixedDigestEquals(marker.InvitationDigest, pending.Frozen.InvitationDigest) ||
                marker.PendingCiphertextHash is not { Length: 64 } || marker.PendingCiphertextHash.Any(c => !char.IsAsciiHexDigitUpper(c)) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(marker.PendingCiphertextHash), SHA256.HashData(snapshot.Ciphertext)))
                return Problem();

            if (related.Length == 0)
                return ToMetadata(pending, DateTimeOffset.UtcNow).HasConflict ? Problem() :
                    new(this, PairingRecoveryPhase.AwaitingPeer, pending.Identity.PeerName, pending.Identity.TransactionId, pending.StageId);
            if (related.Length == 1 && related[0].State == "aborted" &&
                MatchesCommittedRecovery(related[0] with { State = "committed" }, pending))
                return new(this, PairingRecoveryPhase.AbortedCleanup, pending.Identity.PeerName,
                    pending.Identity.TransactionId, pending.StageId, SHA256.HashData(snapshot.Ciphertext), SHA256.HashData(markerBytes));
            if (related.Length != 1 || !MatchesCommittedRecovery(related[0], pending)) return Problem();
            if (related[0].State == "retired")
                return new(this, PairingRecoveryPhase.AppliedCleanup, pending.Identity.PeerName,
                    pending.Identity.TransactionId, pending.StageId, SHA256.HashData(snapshot.Ciphertext), SHA256.HashData(markerBytes));
            return new(this, PairingRecoveryPhase.CommittedAwaitingPeer, pending.Identity.PeerName, pending.Identity.TransactionId, pending.StageId);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or
            JsonException or ArgumentException or FormatException or InvalidOperationException or OverflowException)
        { return Problem(); }
    }

    private async Task<bool> ActiveVaultMatchesDiskLockedAsync(CancellationToken ct)
    {
        if (!File.Exists(file))
            return vaultSnapshot.Version == 1 && vaultSnapshot.Devices.Length == 0 && vaultSnapshot.Transactions.Length == 0;
        var encrypted = await ReadBoundedVaultFileAsync(file, ct, MaxVaultFileBytes).ConfigureAwait(false);
        var plain = protector.Unprotect(encrypted);
        try
        {
            var disk = ParseVaultSnapshot(plain);
            return disk.Version == vaultSnapshot.Version && disk.Devices.SequenceEqual(vaultSnapshot.Devices) &&
                disk.Transactions.SequenceEqual(vaultSnapshot.Transactions) &&
                (disk.Retired ?? []).SequenceEqual(vaultSnapshot.Retired ?? []);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static bool MatchesCommittedRecovery(VaultTransaction transaction, PendingGrantDocument pending)
    {
        var identity = pending.Identity;
        var frozen = pending.Frozen;
        return frozen is not null && (transaction.State is "committed" or "applied" or "finished" or "retired") && transaction.TransactionId == identity.TransactionId &&
            transaction.StageId == pending.StageId && transaction.SessionId == identity.SessionId && transaction.OfferNonce == identity.OfferNonce &&
            transaction.HostName == identity.HostName && transaction.HostFingerprint == identity.HostFingerprint &&
            transaction.PeerName == identity.PeerName && transaction.PeerFingerprint == identity.PeerFingerprint &&
            transaction.Platform == pending.Offer.Platform && transaction.Slot == pending.Slot &&
            transaction.ExpiresAtUtc == identity.ExpiresAtUtc && FixedDigestEquals(transaction.InvitationDigest, frozen.InvitationDigest) &&
            FixedDigestEquals(transaction.CredentialDigest, VaultTokenDigest(pending.Token));
    }
}
