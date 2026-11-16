using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    // Device credentials and their transaction markers must be published together.
    // Neither v2 reading nor these existing mutations create new transaction records.
    private sealed record VaultSnapshot(int Version, Entry[] Devices, VaultTransaction[] Transactions, RetiredTransaction[]? Retired = null);
    private sealed record VaultV2Document(int Version, Entry[] Devices, VaultTransaction[] Transactions);
    private sealed record VaultV3Document(int Version, Entry[] Devices, VaultTransaction[] Transactions, string Retired);
    private sealed record RetiredTransaction(Guid TransactionId, Guid StageId, Guid SessionId, Guid OfferNonce, string CredentialDigest);
    private sealed record VaultTransaction(Guid TransactionId, Guid StageId, Guid SessionId, Guid OfferNonce,
        string HostName, string HostFingerprint, string PeerName, string PeerFingerprint, string Platform,
        int Slot, string InvitationDigest, string CredentialDigest, string State, DateTimeOffset ExpiresAtUtc);
    private sealed record VersionHeader(int Version);
    private const int MaximumVaultTransactions = 64;
    private const int MaximumRetiredTransactions = 1100;
    private const int RetiredRecordBytes = 96;
    private const int MaxVaultFileBytes = 262144;
    private static readonly byte[] VaultCredentialDomain = Encoding.UTF8.GetBytes("nian-link-vault-token-v2\0");
    private static readonly JsonSerializerOptions StrictVaultJson = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly string[] V2Fields = ["version", "devices", "transactions"];
    private static readonly string[] V3Fields = ["version", "devices", "transactions", "retired"];
    private static readonly string[] V2DeviceFields = ["name", "fingerprint", "token", "platform", "slot"];
    private static readonly string[] V2TransactionFields = ["transactionId", "stageId", "sessionId", "offerNonce", "hostName", "hostFingerprint", "peerName", "peerFingerprint", "platform", "slot", "invitationDigest", "credentialDigest", "state", "expiresAtUtc"];

    private static VaultSnapshot ParseVaultSnapshot(byte[] plain)
    {
        if (plain.Length is <= 0 or > MaxVaultFileBytes) throw new InvalidDataException("Device trust is invalid or oversized.");
        try
        {
            using var json = JsonDocument.Parse(plain, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count(x => string.Equals(x.Name, "version", StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidDataException("Device trust version is ambiguous.");
            // Preserve the existing v1 deserializer and compatibility semantics.
            var header = JsonSerializer.Deserialize<VersionHeader>(plain, Json) ?? throw new InvalidDataException("Device trust is empty.");
            if (header.Version == 1)
            {
                if (plain.Length > MaxFileBytes) throw new InvalidDataException("Device trust is oversized.");
                // A transaction-bearing v2 shape cannot be accepted as v1 and silently stripped.
                if (root.EnumerateObject().Any(x => string.Equals(x.Name, "transactions", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Name, "retired", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Transaction data cannot be read as version 1.");
                var v1 = JsonSerializer.Deserialize<Document>(plain, Json) ?? throw new InvalidDataException("Device trust is empty.");
                Validate(v1);
                return new(1, v1.Devices.ToArray(), []);
            }
            if (header.Version is not (2 or 3)) throw new InvalidDataException("Device trust version is unsupported.");
            if (header.Version == 2 && plain.Length > MaxFileBytes) throw new InvalidDataException("Device trust is oversized.");
            RequireVaultFields(root, header.Version == 2 ? V2Fields : V3Fields);
            var devices = root.GetProperty("devices"); var transactions = root.GetProperty("transactions");
            if (devices.ValueKind != JsonValueKind.Array || devices.GetArrayLength() > MultiDeviceCredentials.MaximumPeers ||
                transactions.ValueKind != JsonValueKind.Array || transactions.GetArrayLength() > MaximumVaultTransactions)
                throw new InvalidDataException("Device trust count is invalid.");
            foreach (var item in devices.EnumerateArray()) RequireVaultFields(item, V2DeviceFields);
            foreach (var item in transactions.EnumerateArray()) RequireVaultFields(item, V2TransactionFields);
            VaultSnapshot result;
            if (header.Version == 2)
            {
                var v2 = root.Deserialize<VaultV2Document>(StrictVaultJson) ?? throw new InvalidDataException("Device trust is empty.");
                result = new(v2.Version, v2.Devices, v2.Transactions);
            }
            else
            {
                var v3 = root.Deserialize<VaultV3Document>(StrictVaultJson) ?? throw new InvalidDataException("Device trust is empty.");
                result = new(v3.Version, v3.Devices, v3.Transactions, DecodeRetired(v3.Retired));
            }
            ValidateVaultSnapshot(result);
            return result with { Devices = result.Devices.ToArray(), Transactions = result.Transactions.ToArray(), Retired = result.Retired?.ToArray() };
        }
        catch (JsonException error) { throw new InvalidDataException("Device trust is invalid.", error); }
    }

    private static void ValidateVaultSnapshot(VaultSnapshot snapshot)
    {
        if (snapshot.Version is not (1 or 2 or 3) || snapshot.Devices is null || snapshot.Transactions is null ||
            snapshot.Transactions.Length > MaximumVaultTransactions || snapshot.Version == 1 && snapshot.Transactions.Length != 0 ||
            snapshot.Version < 3 && snapshot.Retired is { Length: > 0 } || snapshot.Version == 3 && snapshot.Retired is null ||
            snapshot.Retired is { Length: > MaximumRetiredTransactions })
            throw new InvalidDataException("Device trust snapshot is invalid.");
        Validate(new Document(1, snapshot.Devices));
        if (snapshot.Version == 1) return;
        var activeHashes = new List<(string Digest, Entry Device)>();
        foreach (var entry in snapshot.Devices)
        {
            var decoded = Convert.FromBase64String(entry.Token);
            try
            {
                if (decoded.Length != 32 || Convert.ToBase64String(decoded) != entry.Token)
                    throw new InvalidDataException("Device credential is not canonical.");
                var digest = VaultTokenDigest(decoded);
                if (activeHashes.Any(active => FixedDigestEquals(active.Digest, digest)))
                    throw new InvalidDataException("Device credentials are duplicated.");
                activeHashes.Add((digest, entry));
            }
            finally { CryptographicOperations.ZeroMemory(decoded); }
        }
        var transactions = new HashSet<Guid>(); var stages = new HashSet<Guid>();
        var credentialHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Transactions)
        {
            if (item is null || item.TransactionId == Guid.Empty || item.StageId == Guid.Empty || item.SessionId == Guid.Empty || item.OfferNonce == Guid.Empty ||
                !transactions.Add(item.TransactionId) || !stages.Add(item.StageId) || item.ExpiresAtUtc == default ||
                item.Slot is < 1 or > MultiDeviceCredentials.MaximumPeers || item.Platform is not ("windows" or "mac" or "unknown") ||
                item.State is not ("committed" or "applied" or "finished" or "retired" or "revoked" or "aborted") || !IsVaultDigest(item.InvitationDigest) || !IsVaultDigest(item.CredentialDigest) ||
                !credentialHashes.Add(item.CredentialDigest))
                throw new InvalidDataException("Device trust transaction is invalid.");
            try
            {
                DeskflowProfile.ValidateName(item.HostName); DeskflowProfile.ValidateName(item.PeerName);
                if (string.Equals(item.HostName, item.PeerName, StringComparison.OrdinalIgnoreCase) ||
                    DeskLinkFingerprint.Normalize(item.HostFingerprint) != item.HostFingerprint ||
                    DeskLinkFingerprint.Normalize(item.PeerFingerprint) != item.PeerFingerprint)
                    throw new InvalidDataException("Device trust transaction identity is invalid.");
            }
            catch (ArgumentException error) { throw new InvalidDataException("Device trust transaction identity is invalid.", error); }
            if (item.State is "committed" or "applied" or "finished" or "retired")
            {
                var active = activeHashes.FirstOrDefault(candidate => FixedDigestEquals(candidate.Digest, item.CredentialDigest)).Device;
                if (active is null || !string.Equals(active.Name, item.PeerName, StringComparison.Ordinal) || active.Fingerprint != item.PeerFingerprint ||
                    active.Platform != item.Platform || active.Slot != item.Slot)
                    throw new InvalidDataException("Committed transaction does not match its active device.");
            }
            else if (activeHashes.Any(active => FixedDigestEquals(active.Digest, item.CredentialDigest)))
                throw new InvalidDataException("A cancelled or revoked credential cannot be active.");
        }
        if (snapshot.Retired is { } retired)
        {
            var sessions = new HashSet<Guid>(snapshot.Transactions.Select(x => x.SessionId));
            var nonces = new HashSet<Guid>(snapshot.Transactions.Select(x => x.OfferNonce));
            if (sessions.Count != snapshot.Transactions.Length || nonces.Count != snapshot.Transactions.Length)
                throw new InvalidDataException("Active transaction identity is duplicated.");
            foreach (var item in retired)
            {
                if (item is null || item.TransactionId == Guid.Empty || item.StageId == Guid.Empty || item.SessionId == Guid.Empty || item.OfferNonce == Guid.Empty ||
                    !transactions.Add(item.TransactionId) || !stages.Add(item.StageId) || !sessions.Add(item.SessionId) || !nonces.Add(item.OfferNonce) ||
                    !IsVaultDigest(item.CredentialDigest) || !credentialHashes.Add(item.CredentialDigest) ||
                    activeHashes.Any(active => FixedDigestEquals(active.Digest, item.CredentialDigest)))
                    throw new InvalidDataException("Retired transaction identity is invalid.");
            }
        }
    }

    private static byte[] SerializeVaultSnapshot(VaultSnapshot snapshot) => snapshot.Version switch
    {
        1 => JsonSerializer.SerializeToUtf8Bytes(new Document(1, snapshot.Devices), Json),
        2 => JsonSerializer.SerializeToUtf8Bytes(new VaultV2Document(2, snapshot.Devices, snapshot.Transactions), StrictVaultJson),
        3 => JsonSerializer.SerializeToUtf8Bytes(new VaultV3Document(3, snapshot.Devices, snapshot.Transactions,
            EncodeRetired(snapshot.Retired ?? throw new InvalidDataException("Retired history is missing."))), StrictVaultJson),
        _ => throw new InvalidDataException("Device trust version is unsupported.")
    };

    private static string EncodeRetired(RetiredTransaction[] entries)
    {
        if (entries.Length > MaximumRetiredTransactions) throw new InvalidDataException("Retired history is full.");
        var bytes = new byte[checked(entries.Length * RetiredRecordBytes)];
        for (var i = 0; i < entries.Length; i++)
        {
            var span = bytes.AsSpan(i * RetiredRecordBytes, RetiredRecordBytes); var item = entries[i];
            item.TransactionId.TryWriteBytes(span); item.StageId.TryWriteBytes(span[16..]);
            item.SessionId.TryWriteBytes(span[32..]); item.OfferNonce.TryWriteBytes(span[48..]);
            Convert.FromHexString(item.CredentialDigest).CopyTo(span[64..]);
        }
        return Convert.ToBase64String(bytes);
    }

    private static RetiredTransaction[] DecodeRetired(string? encoded)
    {
        if (encoded is null || encoded.Length > ((MaximumRetiredTransactions * RetiredRecordBytes + 2) / 3) * 4)
            throw new InvalidDataException("Retired history is invalid.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new InvalidDataException("Retired history is invalid.", error); }
        if (bytes.Length % RetiredRecordBytes != 0 || bytes.Length / RetiredRecordBytes > MaximumRetiredTransactions ||
            Convert.ToBase64String(bytes) != encoded) throw new InvalidDataException("Retired history is invalid.");
        var result = new RetiredTransaction[bytes.Length / RetiredRecordBytes];
        for (var i = 0; i < result.Length; i++)
        {
            var span = bytes.AsSpan(i * RetiredRecordBytes, RetiredRecordBytes);
            result[i] = new(new Guid(span[..16]), new Guid(span.Slice(16, 16)), new Guid(span.Slice(32, 16)),
                new Guid(span.Slice(48, 16)), Convert.ToHexString(span[64..]));
        }
        return result;
    }

    private static bool IsVaultDigest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitUpper);
    private static string VaultTokenDigest(byte[] token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(VaultCredentialDomain); hash.AppendData(token);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string VaultTokenDigest(string token)
    {
        var bytes = Convert.FromBase64String(token);
        try { return VaultTokenDigest(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static bool FixedDigestEquals(string first, string second) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second));

    private static async Task<byte[]> ReadBoundedVaultFileAsync(string path, CancellationToken ct, int maximum = MaxFileBytes)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var buffer = new byte[maximum + 1]; var count = 0;
        while (count < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        if (count <= 0 || count > maximum) throw new InvalidDataException("Protected device trust is invalid or oversized.");
        return buffer.AsSpan(0, count).ToArray();
    }

    private static void RequireVaultFields(JsonElement value, IReadOnlyCollection<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Device trust object is invalid.");
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !fields.Add(property.Name))
                throw new InvalidDataException("Device trust fields are invalid.");
        if (fields.Count != expected.Count) throw new InvalidDataException("Device trust fields are incomplete.");
    }
}
