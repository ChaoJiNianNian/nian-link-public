using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

// Internal descriptions of current local files only; never a user approval or authorization.
internal sealed record OfflineRecoveryBinding(PairingSessionIdentity Identity, PeerIdentityOffer Offer, Guid Stage, int Slot,
    int DataPortBase, string Host, int ControlPort, int KeyboardPort, int DataPort, string InvitationDigest)
{
    public override string ToString() => "[isolated recovery binding]";
}

internal sealed class OfflineRecoverySnapshot
{
    internal object Owner { get; }
    internal object Generation { get; }
    internal OfflineRecoveryBinding Binding { get; }
    private readonly byte[] firstHash, secondHash;
    private readonly byte[]? activeHash;
    internal byte[] FirstHash => firstHash.ToArray();
    internal byte[] SecondHash => secondHash.ToArray();
    internal byte[]? ActiveHash => activeHash?.ToArray();
    internal bool IsExpired { get; }
    internal int MarkerVersion { get; }
    internal OfflineRecoverySnapshot(object owner, object generation, OfflineRecoveryBinding binding, byte[] firstHash, byte[] secondHash, byte[]? activeHash, bool expired, int markerVersion = 0)
    { Owner = owner; Generation = generation; Binding = binding; this.firstHash = firstHash.ToArray(); this.secondHash = secondHash.ToArray(); this.activeHash = activeHash?.ToArray(); IsExpired = expired; MarkerVersion = markerVersion; }
    internal bool MatchesCurrentPeer(OfflineRecoverySnapshot other) => HasSameBinding(other);
    internal bool HasSameBinding(OfflineRecoverySnapshot other) =>
        Binding with { InvitationDigest = "" } == other.Binding with { InvitationDigest = "" } &&
        Binding.InvitationDigest is { Length: 64 } && other.Binding.InvitationDigest is { Length: 64 } &&
        Binding.InvitationDigest.All(char.IsAsciiHexDigitUpper) && other.Binding.InvitationDigest.All(char.IsAsciiHexDigitUpper) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Binding.InvitationDigest), Convert.FromHexString(other.Binding.InvitationDigest));
    public override string ToString() => "[isolated recovery snapshot]";
}

public sealed partial class MultiDeviceCredentialVault
{
    private readonly object recoveryOwner = new();
    private readonly ConditionalWeakTable<object, object> recoveryGenerations = new();
    private static readonly string[] RecoveryMarkerFields = ["version", "stageId", "identity", "invitationDigest", "tokenDigest"];
    private static readonly string[] RecoveryMarkerV2Fields = [.. RecoveryMarkerFields, "offer", "pendingCiphertextDigest"];
    private static readonly string[] RecoveryV1VaultFields = ["version", "devices"];

    internal async Task<OfflineRecoverySnapshot?> CaptureOfflineRecoveryTestAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (activePrepareSession is not null) return null; // Old sending must already be drained.
            RejectLinks(directory); RejectLinks(PrepareMarkerPath);
            if (Directory.Exists(PrepareMarkerPath) || !File.Exists(PrepareMarkerPath)) return null;
            var markerBytes = await ReadBoundedVaultFileAsync(PrepareMarkerPath, ct);
            var markerPlain = protector.Unprotect(markerBytes);
            PrepareUnknownMarker marker;
            try
            {
                if (markerPlain.Length is <= 0 or > MaxFileBytes) return null;
                using var json = JsonDocument.Parse(markerPlain, new JsonDocumentOptions { MaxDepth = 8 });
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number is not (1 or 2)) return null;
                RequireVaultFields(root, number == 1 ? RecoveryMarkerFields : RecoveryMarkerV2Fields);
                RequireVaultFields(root.GetProperty("identity"), PendingIdentityFields);
                if (number == 2) RequireVaultFields(root.GetProperty("offer"), PendingOfferFields);
                marker = json.RootElement.Deserialize<PrepareUnknownMarker>(StrictVaultJson) ?? throw new InvalidDataException("Invalid recovery marker.");
            }
            finally { CryptographicOperations.ZeroMemory(markerPlain); }
            var captured = await ReadPendingGrantSnapshotAsync(ct); var pending = captured.Document;
            if (marker.Version is not (1 or 2) || marker.Identity is null || pending is null || captured.Ciphertext is null || pending.Version != 2 || pending.Frozen is null ||
                marker.StageId != pending.StageId || marker.Identity != pending.Identity || !IsVaultDigest(marker.InvitationDigest) || !IsVaultDigest(marker.TokenDigest)) return null;
            ValidatePendingGrant(pending); ValidateFrozenInvitation(pending);
            if (marker.Version == 2 && (marker.Offer is null || marker.Offer != pending.Offer ||
                !IsVaultDigest(marker.PendingCiphertextDigest) ||
                !FixedDigestEquals(marker.PendingCiphertextDigest!, Convert.ToHexString(SHA256.HashData(captured.Ciphertext))))) return null;
            if (!FixedDigestEquals(marker.InvitationDigest, pending.Frozen.InvitationDigest) || !FixedDigestEquals(marker.TokenDigest, VaultTokenDigest(pending.Token)) || now < pending.CreatedAtUtc) return null;
            var current = vaultSnapshot;
            if (current.Transactions.Any(x => x.TransactionId == pending.Identity.TransactionId || x.StageId == pending.StageId || x.SessionId == pending.Identity.SessionId || x.OfferNonce == pending.Identity.OfferNonce) ||
                current.Devices.Any(x => string.Equals(x.Name, pending.Identity.PeerName, StringComparison.OrdinalIgnoreCase) || x.Fingerprint == pending.Identity.PeerFingerprint || x.Slot == pending.Slot)) return null;
            var activeHash = await VerifyOfflineActiveVaultAsync(current, ct);
            if (activeHash is null && (current.Version != 1 || current.Devices.Length != 0)) return null;
            var canonicalOffer = pending.Offer with { KeyboardFingerprint = DeskLinkFingerprint.Normalize(pending.Offer.KeyboardFingerprint), Nonce = Guid.ParseExact(pending.Offer.Nonce, "N").ToString("N"), ExpiresAtUtc = pending.Offer.ExpiresAtUtc.ToUniversalTime() };
            var frozen = pending.Frozen;
            var binding = new OfflineRecoveryBinding(pending.Identity, canonicalOffer, pending.StageId, pending.Slot, frozen.DataPortBase, frozen.Host, frozen.ControlPort, frozen.KeyboardPort, frozen.DataPort, frozen.InvitationDigest);
            return new(recoveryOwner, recoveryGenerations.GetValue(current, _ => new object()), binding, SHA256.HashData(markerBytes), SHA256.HashData(captured.Ciphertext), activeHash, now >= pending.Identity.ExpiresAtUtc || now >= pending.Offer.ExpiresAtUtc, marker.Version);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or FormatException or InvalidOperationException)
        { return null; }
        finally { gate.Release(); }
    }

    private async Task<byte[]?> VerifyOfflineActiveVaultAsync(VaultSnapshot current, CancellationToken ct)
    {
        RejectLinks(file); if (Directory.Exists(file)) throw new InvalidDataException("Invalid active vault path.");
        if (!File.Exists(file)) return null;
        var encrypted = await ReadBoundedVaultFileAsync(file, ct); var plain = protector.Unprotect(encrypted);
        try
        {
            using var json = JsonDocument.Parse(plain, new JsonDocumentOptions { MaxDepth = 8 }); var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number))
                throw new InvalidDataException("Invalid active vault version.");
            if (number == 1)
            {
                RequireVaultFields(root, RecoveryV1VaultFields);
                var devices = root.GetProperty("devices"); if (devices.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
                foreach (var item in devices.EnumerateArray()) RequireVaultFields(item, V2DeviceFields);
            }
            var disk = ParseVaultSnapshot(plain);
            var first = JsonSerializer.SerializeToUtf8Bytes(disk, StrictVaultJson); var second = JsonSerializer.SerializeToUtf8Bytes(current, StrictVaultJson);
            try { if (!CryptographicOperations.FixedTimeEquals(first, second)) throw new InvalidDataException("Active vault changed."); }
            finally { CryptographicOperations.ZeroMemory(first); CryptographicOperations.ZeroMemory(second); }
            return SHA256.HashData(encrypted);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal async Task<bool> IsOfflineRecoverySnapshotCurrentAsync(OfflineRecoverySnapshot snapshot, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!ReferenceEquals(snapshot.Owner, recoveryOwner)) return false;
        var fresh = await CaptureOfflineRecoveryTestAsync(now, ct);
        return fresh is not null && ReferenceEquals(snapshot.Generation, fresh.Generation) && snapshot.HasSameBinding(fresh) &&
            CryptographicOperations.FixedTimeEquals(snapshot.FirstHash, fresh.FirstHash) && CryptographicOperations.FixedTimeEquals(snapshot.SecondHash, fresh.SecondHash) &&
            EqualOptionalHash(snapshot.ActiveHash, fresh.ActiveHash) && snapshot.IsExpired == fresh.IsExpired && snapshot.MarkerVersion == fresh.MarkerVersion;
    }
    private static bool EqualOptionalHash(byte[]? first, byte[]? second) => first is null ? second is null : second is not null && CryptographicOperations.FixedTimeEquals(first, second);
}

public sealed partial class PeerInvitationStore
{
    private readonly object recoveryOwner = new();
    private readonly ConditionalWeakTable<object, object> recoveryGenerations = new();
    internal async Task<OfflineRecoverySnapshot?> CaptureOfflineRecoveryTestAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await CaptureOfflinePeerCoreAsync(now, ct); }
        finally { gate.Release(); }
    }

    private async Task<OfflineRecoverySnapshot?> CaptureOfflinePeerCoreAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RejectLinks(PromotionPath);
            if (Volatile.Read(ref liveStageUncertain) != 0 || Volatile.Read(ref promotionUnknown) != 0 || promotedInvitation is not null || File.Exists(PromotionPath) || Directory.Exists(PromotionPath)) return null;
            var path = Path.Combine(directory, PendingInvitationFile); RejectLinks(directory); RejectLinks(path);
            if (Directory.Exists(path) || !File.Exists(path)) return null;
            var encrypted = await ReadOfflinePeerFileAsync(path, ct); var plain = protector.Unprotect(encrypted); V3Document doc;
            try
            {
                if (plain.Length is <= 0 or > MaximumBytes) return null;
                using var json = JsonDocument.Parse(StrictPendingUtf8.GetString(plain), new JsonDocumentOptions { MaxDepth = 8 }); var root = json.RootElement;
                RejectDuplicateFields(root); RequireExactFields(root, V3Fields); RequireExactFields(root.GetProperty("identity"), IdentityFields);
                RequireExactFields(root.GetProperty("offer"), V3OfferFields); RequireExactFields(root.GetProperty("invitation"), InvitationFields);
                doc = root.Deserialize<V3Document>(PendingJson) ?? throw new InvalidDataException("Invalid peer history."); ValidateV3History(doc);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            RejectLinks(file); if (Directory.Exists(file)) return null;
            byte[]? activeHash = null; MultiPeerInvitation? diskActive = null;
            if (File.Exists(file))
            {
                var active = await ReadOfflinePeerFileAsync(file, ct); var activePlain = protector.Unprotect(active);
                try
                {
                    var activeJson = StrictPendingUtf8.GetString(activePlain);
                    using var activeDoc = JsonDocument.Parse(activeJson, new JsonDocumentOptions { MaxDepth = 8 });
                    RejectDuplicateFields(activeDoc.RootElement); RequireExactFields(activeDoc.RootElement, InvitationFields);
                    diskActive = MultiPeerInvitation.Parse(activeJson);
                }
                finally { CryptographicOperations.ZeroMemory(activePlain); }
                activeHash = SHA256.HashData(active);
            }
            if (diskActive != invitation) return null;
            var activeDigest = diskActive is null ? null : ComputePendingDigest(diskActive);
            if (doc.BaseInvitationDigest is null ? activeDigest is not null : activeDigest is null || !DigestMatches(doc.BaseInvitationDigest, activeDigest)) return null;
            var binding = new OfflineRecoveryBinding(doc.Identity, doc.Offer, doc.StageId, doc.Slot, doc.DataPortBase, doc.Invitation.Host, doc.Invitation.Port, doc.Invitation.KeyboardPort, doc.Invitation.DataPort, doc.InvitationDigest);
            return new(recoveryOwner, recoveryGenerations.GetValue((object?)invitation ?? recoveryOwner, _ => new object()), binding, SHA256.HashData(encrypted), new byte[32], activeHash, now >= doc.Identity.ExpiresAtUtc || now >= doc.Offer.ExpiresAtUtc);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or FormatException or InvalidOperationException or System.Text.DecoderFallbackException)
        { return null; }
    }

    private static async Task<byte[]> ReadOfflinePeerFileAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var buffer = new byte[MaximumBytes + 1]; var count = 0;
        while (count < buffer.Length) { var n = await input.ReadAsync(buffer.AsMemory(count), ct); if (n == 0) break; count += n; }
        if (count is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid protected peer file length.");
        return buffer.AsSpan(0, count).ToArray();
    }

    internal async Task<bool> IsOfflineRecoverySnapshotCurrentAsync(OfflineRecoverySnapshot snapshot, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!ReferenceEquals(snapshot.Owner, recoveryOwner)) return false;
        var fresh = await CaptureOfflineRecoveryTestAsync(now, ct);
        return fresh is not null && SamePeerRecoverySnapshot(snapshot, fresh);
    }

    private static bool SamePeerRecoverySnapshot(OfflineRecoverySnapshot snapshot, OfflineRecoverySnapshot fresh) =>
        ReferenceEquals(snapshot.Generation, fresh.Generation) && snapshot.HasSameBinding(fresh) &&
        CryptographicOperations.FixedTimeEquals(snapshot.FirstHash, fresh.FirstHash) &&
        (snapshot.ActiveHash is null ? fresh.ActiveHash is null : fresh.ActiveHash is not null && CryptographicOperations.FixedTimeEquals(snapshot.ActiveHash, fresh.ActiveHash)) && snapshot.IsExpired == fresh.IsExpired;
}
