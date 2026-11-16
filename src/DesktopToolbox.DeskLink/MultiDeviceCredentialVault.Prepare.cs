using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    private sealed record PrepareMarkerDocument(int Version, Guid StageId, PairingSessionIdentity Identity,
        string InvitationDigest, string PendingCiphertextHash);
    private static readonly string[] PrepareMarkerFields = ["version", "stageId", "identity", "invitationDigest", "pendingCiphertextHash"];
    private string PrepareMarkerPath => Path.Combine(directory, "pending-prepare.bin");

    // Caller holds gate. A marker means a PREPARE may have reached the peer and must not be discarded blindly.
    private void RejectExistingPrepareMarker()
    {
        RejectLinks(directory); RejectLinks(PrepareMarkerPath);
        if (File.Exists(PrepareMarkerPath) || Directory.Exists(PrepareMarkerPath))
            throw new InvalidOperationException("Pairing delivery requires authenticated recovery.");
    }
    /// <summary>
    /// Send one current frozen invitation on the owning confirmed TLS session.
    /// Returns only a non-secret receipt and does not activate trust.
    /// </summary>
    internal async Task<PairingPrepareReceipt> SendPrepareAndAwaitReadyAsync(PairingTlsConsentSession session,
        PairingTlsConsentSession.DeliveryLease lease, Guid stage, PendingFrozenInvitationMetadata frozen, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(lease); ArgumentNullException.ThrowIfNull(frozen);
        MultiPeerInvitation invitation;
        byte[] pendingHash;
        var receipt = new PairingPrepareReceipt(stage, frozen.Slot, frozen.DataPortBase, frozen.InvitationDigest);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); lease.EnsureCurrent(ct);
            var owned = lease.GetFrozenHost(ct);
            if (!ReferenceEquals(owned.Vault, this) || owned.StageId != stage || owned.Frozen != frozen || frozen.StageId != stage)
                throw new InvalidOperationException("The pending invitation does not belong to this pairing.");
            var snapshot = await ReadPendingGrantSnapshotAsync(ct).ConfigureAwait(false);
            var pending = snapshot.Document ?? throw new InvalidOperationException("The pending invitation is unavailable.");
            if (snapshot.Ciphertext is null || pending.StageId != stage || pending.Identity != lease.Identity ||
                pending.Offer != lease.Offer || pending.Frozen is null || FrozenMetadata(pending) != frozen ||
                ToMetadata(pending, DateTimeOffset.UtcNow).HasConflict)
                throw new InvalidOperationException("The pending invitation changed.");
            ValidatePendingBinding(pending.Identity, pending.Offer, DateTimeOffset.UtcNow);
            ValidateFrozenInvitation(pending);
            invitation = ToInvitation(pending, pending.Frozen.Host, pending.Frozen.ControlPort,
                pending.Frozen.KeyboardPort, pending.Frozen.DataPort);
            pendingHash = SHA256.HashData(snapshot.Ciphertext);
            await WritePrepareMarkerLockedAsync(lease, receipt, pendingHash, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        try
        {
            await RevalidatePendingBeforeOrAfterSendAsync(lease, stage, frozen, pendingHash, ct).ConfigureAwait(false);
            var ready = await session.SendPrepareCoreAsync(lease, invitation, receipt, ct).ConfigureAwait(false);
            await RevalidatePendingBeforeOrAfterSendAsync(lease, stage, frozen, pendingHash, ct).ConfigureAwait(false);
            return ready;
        }
        finally { CryptographicOperations.ZeroMemory(pendingHash); }
    }

    private async Task RevalidatePendingBeforeOrAfterSendAsync(PairingTlsConsentSession.DeliveryLease lease,
        Guid stage, PendingFrozenInvitationMetadata frozen, byte[] expectedHash, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); lease.EnsureCurrent(ct);
            var snapshot = await ReadPendingGrantSnapshotAsync(ct).ConfigureAwait(false);
            var pending = snapshot.Document;
            if (pending is null || snapshot.Ciphertext is null || pending.StageId != stage ||
                pending.Identity != lease.Identity || pending.Offer != lease.Offer || pending.Frozen is null ||
                FrozenMetadata(pending) != frozen || ToMetadata(pending, DateTimeOffset.UtcNow).HasConflict ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(snapshot.Ciphertext), expectedHash))
                throw new InvalidOperationException("The pending invitation changed during delivery.");
            ValidatePendingBinding(pending.Identity, pending.Offer, DateTimeOffset.UtcNow);
            ValidateFrozenInvitation(pending);
            await VerifyPrepareMarkerLockedAsync(lease, new PairingPrepareReceipt(stage, frozen.Slot,
                frozen.DataPortBase, frozen.InvitationDigest), ct).ConfigureAwait(false);
            lease.EnsureCurrent(ct);
        }
        finally { gate.Release(); }
    }

    // Both marker creation and verification run only while the original vault gate is held.
    private async Task WritePrepareMarkerLockedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingPrepareReceipt receipt, byte[] pendingHash, CancellationToken ct)
    {
        RejectExistingPrepareMarker(); lease.EnsureCurrent(ct);
        var marker = new PrepareMarkerDocument(1, receipt.StageId, lease.Identity, receipt.InvitationDigest,
            Convert.ToHexString(pendingHash));
        var plain = JsonSerializer.SerializeToUtf8Bytes(marker, Json);
        byte[] encrypted;
        try { encrypted = protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length is <= 0 or > MaxFileBytes) throw new InvalidDataException("Protected pairing marker is invalid.");
        var path = PrepareMarkerPath; var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var created = false;
        try
        {
            RejectLinks(directory); RejectLinks(path); RejectLinks(temporary);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                created = true;
                await file.WriteAsync(encrypted, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }
            RejectExistingPrepareMarker(); lease.EnsureCurrent(ct);
            File.Move(temporary, path, false);
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task VerifyPrepareMarkerLockedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingPrepareReceipt receipt, CancellationToken ct, bool allowCommitted = false)
    {
        lease.EnsureCurrent(ct); RejectLinks(directory); RejectLinks(PrepareMarkerPath);
        if (!File.Exists(PrepareMarkerPath) || Directory.Exists(PrepareMarkerPath))
            throw new InvalidOperationException("Pairing delivery marker is unavailable.");
        var encrypted = await ReadBoundedVaultFileAsync(PrepareMarkerPath, ct).ConfigureAwait(false);
        var plain = protector.Unprotect(encrypted);
        try
        {
            if (plain.Length is <= 0 or > MaxFileBytes) throw new InvalidDataException("Pairing marker is invalid.");
            using var parsed = JsonDocument.Parse(plain, new JsonDocumentOptions { MaxDepth = 8 });
            var root = parsed.RootElement;
            if (!HasExactPendingFields(root, PrepareMarkerFields) ||
                !HasExactPendingFields(root.GetProperty("identity"), PendingIdentityFields))
                throw new InvalidDataException("Pairing marker fields are invalid.");
            var marker = root.Deserialize<PrepareMarkerDocument>(Json) ?? throw new InvalidDataException("Pairing marker is empty.");
            var snapshot = await ReadPendingGrantSnapshotAsync(ct).ConfigureAwait(false);
            var pending = snapshot.Document;
            if (marker.Version != 1 || marker.StageId != receipt.StageId || marker.Identity != lease.Identity ||
                marker.InvitationDigest != receipt.InvitationDigest || marker.PendingCiphertextHash is not { Length: 64 } ||
                marker.PendingCiphertextHash.Any(c => !char.IsAsciiHexDigitUpper(c)) || pending is null || snapshot.Ciphertext is null ||
                pending.StageId != receipt.StageId || pending.Identity != lease.Identity || pending.Offer != lease.Offer ||
                pending.Frozen is null || pending.Slot != receipt.Slot || pending.Frozen.DataPortBase != receipt.DataPortBase ||
                pending.Frozen.InvitationDigest != receipt.InvitationDigest ||
                (!allowCommitted && ToMetadata(pending, DateTimeOffset.UtcNow).HasConflict) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(marker.PendingCiphertextHash), SHA256.HashData(snapshot.Ciphertext)))
                throw new InvalidOperationException("Pairing delivery evidence changed.");
            ValidatePendingBinding(pending.Identity, pending.Offer, DateTimeOffset.UtcNow);
            ValidateFrozenInvitation(pending);
            lease.EnsureCurrent(ct);
        }
        catch (JsonException error) { throw new InvalidDataException("Pairing marker is invalid.", error); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
