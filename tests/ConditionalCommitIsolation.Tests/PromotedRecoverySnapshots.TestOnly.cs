using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

// Read-only promoted-state experiments, compiled only into the isolated test assembly.
public sealed partial class MultiDeviceCredentialVault
{
    internal async Task<OfflineRecoverySnapshot?> CapturePromotedRecoveryTestAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await CapturePromotedRecoveryCoreTestAsync(now, ct); }
        finally { gate.Release(); }
    }

    private async Task<OfflineRecoverySnapshot?> CapturePromotedRecoveryCoreTestAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            ThrowIfDisposed(); if (activePrepareSession is not null) return null;
            RejectLinks(directory); RejectLinks(PrepareMarkerPath);
            if (!File.Exists(PrepareMarkerPath) || Directory.Exists(PrepareMarkerPath)) return null;
            var markerBytes = await ReadBoundedVaultFileAsync(PrepareMarkerPath, ct); var markerPlain = protector.Unprotect(markerBytes); PrepareUnknownMarker marker;
            try
            {
                if (markerPlain.Length is <= 0 or > MaxFileBytes) return null;
                using var json = JsonDocument.Parse(markerPlain, new JsonDocumentOptions { MaxDepth = 8 }); var root = json.RootElement;
                RequireVaultFields(root, RecoveryMarkerV2Fields); RequireVaultFields(root.GetProperty("identity"), PendingIdentityFields);
                RequireVaultFields(root.GetProperty("offer"), PendingOfferFields);
                marker = root.Deserialize<PrepareUnknownMarker>(StrictVaultJson) ?? throw new InvalidDataException("Invalid marker.");
            }
            finally { CryptographicOperations.ZeroMemory(markerPlain); }
            var captured = await ReadPendingGrantSnapshotAsync(ct); var pending = captured.Document;
            if (marker.Version != 2 || marker.Identity is null || marker.Offer is null || pending is null || pending.Version != 2 ||
                pending.Frozen is null || captured.Ciphertext is null || marker.StageId != pending.StageId || marker.Identity != pending.Identity || marker.Offer != pending.Offer ||
                !IsVaultDigest(marker.InvitationDigest) || !IsVaultDigest(marker.TokenDigest) || !IsVaultDigest(marker.PendingCiphertextDigest) ||
                !FixedDigestEquals(marker.InvitationDigest, pending.Frozen.InvitationDigest) || !FixedDigestEquals(marker.TokenDigest, VaultTokenDigest(pending.Token)) ||
                !FixedDigestEquals(marker.PendingCiphertextDigest!, Convert.ToHexString(SHA256.HashData(captured.Ciphertext))) || now < pending.CreatedAtUtc) return null;
            ValidatePendingGrant(pending); ValidateFrozenInvitation(pending);
            var current = vaultSnapshot;
            var key = new IsolatedGrantKey(pending.Identity, pending.StageId, pending.Offer.Platform, pending.Slot, pending.Frozen.InvitationDigest, VaultTokenDigest(pending.Token));
            if (InspectIsolatedCore(key) != "committed") return null;
            var activeHash = await VerifyOfflineActiveVaultAsync(current, ct); if (activeHash is null) return null;
            var canonical = pending.Offer with { KeyboardFingerprint = DeskLinkFingerprint.Normalize(pending.Offer.KeyboardFingerprint), Nonce = Guid.ParseExact(pending.Offer.Nonce, "N").ToString("N"), ExpiresAtUtc = pending.Offer.ExpiresAtUtc.ToUniversalTime() };
            var f = pending.Frozen;
            var binding = new OfflineRecoveryBinding(pending.Identity, canonical, pending.StageId, pending.Slot, f.DataPortBase, f.Host, f.ControlPort, f.KeyboardPort, f.DataPort, f.InvitationDigest);
            return new(recoveryOwner, recoveryGenerations.GetValue(current, _ => new object()), binding, SHA256.HashData(markerBytes), SHA256.HashData(captured.Ciphertext), activeHash,
                now >= pending.Identity.ExpiresAtUtc || now >= pending.Offer.ExpiresAtUtc, marker.Version);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or FormatException or InvalidOperationException)
        { return null; }
    }

    internal async Task<bool> IsPromotedRecoveryCurrentTestAsync(OfflineRecoverySnapshot snapshot, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!ReferenceEquals(snapshot.Owner, recoveryOwner)) return false;
        var fresh = await CapturePromotedRecoveryTestAsync(now, ct);
        return fresh is not null && ReferenceEquals(snapshot.Generation, fresh.Generation) && snapshot.HasSameBinding(fresh) &&
            CryptographicOperations.FixedTimeEquals(snapshot.FirstHash, fresh.FirstHash) && CryptographicOperations.FixedTimeEquals(snapshot.SecondHash, fresh.SecondHash) &&
            EqualOptionalHash(snapshot.ActiveHash, fresh.ActiveHash) && snapshot.IsExpired == fresh.IsExpired && snapshot.MarkerVersion == fresh.MarkerVersion;
    }
}

public sealed partial class PeerInvitationStore
{
    private static readonly string[] RecoveryPromotionFields = ["version", "stageId", "identity", "offer", "slot", "dataPortBase", "invitation", "invitationDigest", "v3CipherDigest", "baseCipherDigest"];

    internal async Task<OfflineRecoverySnapshot?> CapturePromotedRecoveryTestAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await CapturePromotedRecoveryCoreTestAsync(now, ct); }
        finally { gate.Release(); }
    }

    private async Task<OfflineRecoverySnapshot?> CapturePromotedRecoveryCoreTestAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); RejectLinks(directory); RejectLinks(PromotionPath); RejectLinks(RecoveryPath);
            if (File.Exists(RecoveryPath) || Directory.Exists(RecoveryPath) || Volatile.Read(ref promotionUnknown) == 0 || !File.Exists(PromotionPath) || Directory.Exists(PromotionPath) || Volatile.Read(ref liveStageUncertain) != 0) return null;
            var encrypted = await ReadOfflinePeerFileAsync(PromotionPath, ct); var plain = protector.Unprotect(encrypted); IsolatedPromotionRecord record;
            try
            {
                if (plain.Length is <= 0 or > MaximumBytes) return null;
                using var json = JsonDocument.Parse(StrictPendingUtf8.GetString(plain), new JsonDocumentOptions { MaxDepth = 8 }); var root = json.RootElement;
                RejectDuplicateFields(root); RequireExactFields(root, RecoveryPromotionFields); RequireExactFields(root.GetProperty("identity"), IdentityFields);
                RequireExactFields(root.GetProperty("offer"), V3OfferFields); RequireExactFields(root.GetProperty("invitation"), InvitationFields);
                record = root.Deserialize<IsolatedPromotionRecord>(PendingJson) ?? throw new InvalidDataException("Invalid promotion record.");
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (record.Version != 1 || record.Identity is null || record.Offer is null || record.Invitation is null) return null;
            ValidateDigest(record.InvitationDigest); ValidateDigest(record.V3CipherDigest); if (record.BaseCipherDigest is not null) ValidateDigest(record.BaseCipherDigest);
            var stagePath = Path.Combine(directory, PendingInvitationFile); RejectLinks(stagePath);
            var stageCipher = await ReadOfflinePeerFileAsync(stagePath, ct);
            if (!DigestMatches(record.V3CipherDigest, Convert.ToHexString(SHA256.HashData(stageCipher)))) return null;
            var stagePlain = protector.Unprotect(stageCipher); V3Document stage;
            try
            {
                if (stagePlain.Length is <= 0 or > MaximumBytes) return null;
                using var json = JsonDocument.Parse(StrictPendingUtf8.GetString(stagePlain), new JsonDocumentOptions { MaxDepth = 8 }); var root = json.RootElement;
                RejectDuplicateFields(root); RequireExactFields(root, V3Fields); RequireExactFields(root.GetProperty("identity"), IdentityFields);
                RequireExactFields(root.GetProperty("offer"), V3OfferFields); RequireExactFields(root.GetProperty("invitation"), InvitationFields);
                stage = root.Deserialize<V3Document>(PendingJson) ?? throw new InvalidDataException("Invalid v3 stage."); ValidateV3History(stage);
            }
            finally { CryptographicOperations.ZeroMemory(stagePlain); }
            if (record.StageId != stage.StageId || record.Identity != stage.Identity || record.Offer != stage.Offer || record.Slot != stage.Slot ||
                record.DataPortBase != stage.DataPortBase || record.Invitation != stage.Invitation || !DigestMatches(record.InvitationDigest, stage.InvitationDigest)) return null;
            RejectLinks(file); if (Directory.Exists(file)) return null;
            byte[]? activeHash = null; MultiPeerInvitation? diskActive = null;
            if (File.Exists(file))
            {
                var active = await ReadOfflinePeerFileAsync(file, ct); activeHash = SHA256.HashData(active); var activePlain = protector.Unprotect(active);
                try
                {
                    var activeJson = StrictPendingUtf8.GetString(activePlain); using var doc = JsonDocument.Parse(activeJson, new JsonDocumentOptions { MaxDepth = 8 });
                    RejectDuplicateFields(doc.RootElement); RequireExactFields(doc.RootElement, InvitationFields); diskActive = MultiPeerInvitation.Parse(activeJson);
                }
                finally { CryptographicOperations.ZeroMemory(activePlain); }
            }
            if (record.BaseCipherDigest is null ? activeHash is not null : activeHash is null || !DigestMatches(record.BaseCipherDigest, Convert.ToHexString(activeHash))) return null;
            if (diskActive != invitation) return null;
            var currentDigest = diskActive is null ? null : ComputePendingDigest(diskActive);
            if (stage.BaseInvitationDigest is null ? currentDigest is not null : currentDigest is null || !DigestMatches(stage.BaseInvitationDigest, currentDigest)) return null;
            var binding = new OfflineRecoveryBinding(stage.Identity, stage.Offer, stage.StageId, stage.Slot, stage.DataPortBase, stage.Invitation.Host, stage.Invitation.Port,
                stage.Invitation.KeyboardPort, stage.Invitation.DataPort, stage.InvitationDigest);
            return new(recoveryOwner, recoveryGenerations.GetValue((object?)invitation ?? recoveryOwner, _ => new object()), binding,
                SHA256.HashData(encrypted), SHA256.HashData(stageCipher), activeHash, now >= stage.Identity.ExpiresAtUtc || now >= stage.Offer.ExpiresAtUtc);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or FormatException or InvalidOperationException or System.Text.DecoderFallbackException)
        { return null; }
    }

    internal async Task<bool> IsPromotedRecoveryCurrentTestAsync(OfflineRecoverySnapshot snapshot, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!ReferenceEquals(snapshot.Owner, recoveryOwner)) return false;
        var fresh = await CapturePromotedRecoveryTestAsync(now, ct);
        return fresh is not null && SamePeerRecoverySnapshot(snapshot, fresh) && CryptographicOperations.FixedTimeEquals(snapshot.SecondHash, fresh.SecondHash);
    }
}
