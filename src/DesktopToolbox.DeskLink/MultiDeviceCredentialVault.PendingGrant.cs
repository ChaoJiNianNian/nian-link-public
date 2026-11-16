using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopToolbox.DeskLink;

/// <summary>
/// Public description of a pending operation; it contains no credential.
/// IsExpired is a clock snapshot, not a monotonic or durable authorization decision.
/// Future commits must independently verify expiry and protect against clock rollback across restarts.
/// </summary>
internal sealed record PendingGrantMetadata(Guid StageId, Guid TransactionId, Guid SessionId, Guid OfferNonce,
    string PeerName, string PeerFingerprint, string Platform, int Slot, DateTimeOffset ExpiresAtUtc, bool HasConflict, bool IsExpired)
{
    internal PendingFrozenInvitationMetadata? Frozen { get; init; }
}

public sealed partial class MultiDeviceCredentialVault
{
    // This experimental record is separate from the active v1 vault. It is never used by
    // authentication and does not itself establish TLS binding or user confirmation.
    private sealed record PendingGrantDocument(int Version, Guid StageId, PairingSessionIdentity Identity,
        PeerIdentityOffer Offer, int Slot, string Token, DateTimeOffset CreatedAtUtc,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FrozenInvitation? Frozen = null)
    {
        public override string ToString() => $"PendingGrantDocument {{ StageId = {StageId}, Token = [redacted] }}";
    }

    private static readonly string[] PendingFields = ["version", "stageId", "identity", "offer", "slot", "token", "createdAtUtc"];
    private static readonly string[] PendingFrozenFields = ["version", "stageId", "identity", "offer", "slot", "token", "createdAtUtc", "frozen"];
    private static readonly string[] PendingIdentityFields = ["sessionId", "offerNonce", "transactionId", "hostName", "peerName", "hostFingerprint", "peerFingerprint", "expiresAtUtc"];
    private static readonly string[] PendingOfferFields = ["version", "name", "platform", "keyboardFingerprint", "nonce", "expiresAtUtc"];
    private string PendingGrantPath => Path.Combine(directory, "pending-grant.bin");

    internal async Task<PendingGrantMetadata> StagePendingGrantAsync(PairingSessionIdentity identity, PeerIdentityOffer offer,
        DateTimeOffset now, CancellationToken ct = default, Func<DateTimeOffset>? clock = null)
    {
        ValidatePendingBinding(identity, offer, now);
        clock ??= () => DateTimeOffset.UtcNow;
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            RejectExistingPrepareMarker();
            if (await ReadPendingGrantAsync(ct) is not null) throw new InvalidOperationException("A pending grant already exists.");
            await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
            // Retire only terminal history before reserving space for either a
            // committed or aborted transaction; no evidence may be pending here.
            await RetireTerminalTransactionsIfNeededLockedAsync(ct).ConfigureAwait(false);
            if (vaultSnapshot.Transactions.Length >= MaximumVaultTransactions || ConflictsWithRetired(vaultSnapshot, identity) ||
                vaultSnapshot.Transactions.Any(x => x.TransactionId == identity.TransactionId || x.SessionId == identity.SessionId || x.OfferNonce == identity.OfferNonce))
                throw new InvalidOperationException("Pairing history conflicts or is full.");
            var checkedNow = clock();
            if (checkedNow < now) throw new ArgumentException("Clock moved backwards during pending grant preparation.");
            ValidatePendingBinding(identity, offer, checkedNow);
            ct.ThrowIfCancellationRequested();
            var old = vaultSnapshot.Devices;
            if (old.Any(x => string.Equals(x.Name, identity.PeerName, StringComparison.OrdinalIgnoreCase) || x.Fingerprint == identity.PeerFingerprint))
                throw new ArgumentException("Device name or fingerprint is already registered.");
            if (old.Length >= MultiDeviceCredentials.MaximumPeers) throw new InvalidOperationException("The device limit has been reached.");
            var slot = Enumerable.Range(1, MultiDeviceCredentials.MaximumPeers).First(x => old.All(p => p.Slot != x));
            var random = RandomNumberGenerator.GetBytes(32);
            string token;
            try { token = Convert.ToBase64String(random); }
            finally { CryptographicOperations.ZeroMemory(random); }
            if (ConflictsWithRetired(vaultSnapshot, identity, credentialDigest: VaultTokenDigest(token)) ||
                vaultSnapshot.Transactions.Any(x => FixedDigestEquals(x.CredentialDigest, VaultTokenDigest(token))))
                throw new InvalidOperationException("The pending credential conflicts with previous history.");
            var pending = new PendingGrantDocument(1, Guid.NewGuid(), identity, offer, slot, token, checkedNow.ToUniversalTime());
            if (ConflictsWithRetired(vaultSnapshot, identity, pending.StageId) || vaultSnapshot.Transactions.Any(x => x.StageId == pending.StageId))
                throw new InvalidOperationException("The pending stage conflicts with previous history.");
            ValidatePendingGrant(pending);
            await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
            await WriteNewPendingGrantAsync(pending, clock, ct);
            return ToMetadata(pending, checkedNow);
        }
        finally { gate.Release(); }
    }

    internal async Task<PendingGrantMetadata?> GetPendingGrantMetadataAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { ThrowIfDisposed(); var pending = await ReadPendingGrantAsync(ct); return pending is null ? null : ToMetadata(pending, now); }
        finally { gate.Release(); }
    }

    internal async Task<bool> AbortPendingGrantAsync(Guid stageId, PairingSessionIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            RejectExistingPrepareMarker();
            var pending = await ReadPendingGrantAsync(ct);
            if (pending is null || pending.StageId != stageId || pending.Identity != identity) return false;
            RejectLinks(directory); RejectLinks(PendingGrantPath);
            ct.ThrowIfCancellationRequested();
            File.Delete(PendingGrantPath);
            return true;
        }
        finally { gate.Release(); }
    }

    private PendingGrantMetadata ToMetadata(PendingGrantDocument value, DateTimeOffset now)
    {
        var conflict = vaultSnapshot.Devices.Any(x => string.Equals(x.Name, value.Identity.PeerName, StringComparison.OrdinalIgnoreCase) ||
            x.Fingerprint == value.Identity.PeerFingerprint || x.Slot == value.Slot);
        return new(value.StageId, value.Identity.TransactionId, value.Identity.SessionId, value.Identity.OfferNonce,
            value.Identity.PeerName, value.Identity.PeerFingerprint, value.Offer.Platform, value.Slot,
            value.Identity.ExpiresAtUtc, conflict, now < value.CreatedAtUtc || now >= value.Identity.ExpiresAtUtc)
        { Frozen = value.Frozen is null ? null : FrozenMetadata(value) };
    }

    private async Task<PendingGrantDocument?> ReadPendingGrantAsync(CancellationToken ct)
        => (await ReadPendingGrantSnapshotAsync(ct)).Document;

    private async Task<(PendingGrantDocument? Document, byte[]? Ciphertext)> ReadPendingGrantSnapshotAsync(CancellationToken ct)
    {
        RejectLinks(directory); RejectLinks(PendingGrantPath);
        if (Directory.Exists(PendingGrantPath)) throw new InvalidDataException("Pending grant path is a directory.");
        if (!File.Exists(PendingGrantPath)) return (null, null);
        byte[] ciphertext;
        await using (var input = new FileStream(PendingGrantPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            // Bound allocation even if a file changes after checking its path or length.
            var buffer = new byte[MaxFileBytes + 1]; var count = 0;
            while (count < buffer.Length)
            {
                var read = await input.ReadAsync(buffer.AsMemory(count), ct);
                if (read == 0) break;
                count += read;
            }
            if (count is <= 0 or > MaxFileBytes) throw new InvalidDataException("Protected pending grant is invalid or oversized.");
            ciphertext = buffer.AsSpan(0, count).ToArray();
        }
        var plaintext = protector.Unprotect(ciphertext);
        try
        {
            if (plaintext.Length is <= 0 or > MaxFileBytes) throw new InvalidDataException("Pending grant is invalid or oversized.");
            using var json = JsonDocument.Parse(plaintext, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var versionValue) ||
                versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt32(out var version) ||
                version is not (1 or 2) || !HasExactPendingFields(root, version == 1 ? PendingFields : PendingFrozenFields) ||
                !HasExactPendingFields(root.GetProperty("identity"), PendingIdentityFields) ||
                !HasExactPendingFields(root.GetProperty("offer"), PendingOfferFields)) throw new InvalidDataException("Pending grant fields are invalid.");
            if (version == 2 && !HasExactPendingFields(root.GetProperty("frozen"), FrozenFields)) throw new InvalidDataException("Frozen invitation fields are invalid.");
            var pending = root.Deserialize<PendingGrantDocument>(Json) ?? throw new InvalidDataException("Pending grant is empty.");
            ValidatePendingGrant(pending);
            return (pending, ciphertext);
        }
        catch (JsonException error) { throw new InvalidDataException("Pending grant is invalid.", error); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task WriteNewPendingGrantAsync(PendingGrantDocument pending, Func<DateTimeOffset> clock, CancellationToken ct)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(pending, Json);
        byte[] ciphertext;
        try { ciphertext = protector.Protect(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        if (ciphertext.Length is <= 0 or > MaxFileBytes) throw new InvalidDataException("Protected pending grant is invalid or oversized.");
        var path = PendingGrantPath; var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            RejectLinks(directory); RejectLinks(path); RejectLinks(temp);
            await File.WriteAllBytesAsync(temp, ciphertext, ct);
            RejectLinks(path);
            var publishNow = clock();
            if (publishNow < pending.CreatedAtUtc) throw new ArgumentException("Clock moved backwards during pending grant preparation.");
            ValidatePendingBinding(pending.Identity, pending.Offer, publishNow);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void ValidatePendingGrant(PendingGrantDocument pending)
    {
        if (pending.Version is not (1 or 2) || (pending.Version == 1) != (pending.Frozen is null) || pending.StageId == Guid.Empty || pending.Slot is < 1 or > MultiDeviceCredentials.MaximumPeers || pending.Token is not { Length: 44 })
            throw new InvalidDataException("Pending grant identity is invalid.");
        try
        {
            ValidatePendingBinding(pending.Identity, pending.Offer, pending.CreatedAtUtc);
            var token = Convert.FromBase64String(pending.Token);
            try { if (token.Length != 32 || Convert.ToBase64String(token) != pending.Token) throw new InvalidDataException("Pending credential is invalid."); }
            finally { CryptographicOperations.ZeroMemory(token); }
            if (pending.Frozen is not null) ValidateFrozenInvitation(pending);
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        { throw new InvalidDataException("Pending grant is invalid.", error); }
    }

    private static void ValidatePendingBinding(PairingSessionIdentity identity, PeerIdentityOffer offer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(offer);
        offer.Validate(now);
        _ = new PairingConsentState(identity, () => now);
        if (!Guid.TryParseExact(offer.Nonce, "N", out var nonce) || nonce != identity.OfferNonce ||
            offer.Name != identity.PeerName || !DeskLinkFingerprint.Matches(identity.PeerFingerprint, offer.KeyboardFingerprint) ||
            identity.ExpiresAtUtc > offer.ExpiresAtUtc)
            throw new ArgumentException("Pending grant does not match the identity offer.");
    }

    private static bool HasExactPendingFields(JsonElement element, IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!expected.Contains(property.Name) || !found.Add(property.Name)) return false;
        return found.Count == expected.Count;
    }
}
