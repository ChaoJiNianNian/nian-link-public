using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public interface IMultiDeviceSecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}
public sealed record TrustedDeviceIdentity(string Name, string Fingerprint, string Platform = "unknown", int Slot = 0);
public sealed record MultiDeviceGrant(string Token, int Slot)
{
    public override string ToString() => $"MultiDeviceGrant {{ Token = [redacted], Slot = {Slot} }}";
}

/// <summary>
/// Stores only explicitly confirmed devices. All mutations are committed to
/// the protected file before becoming visible; a single process holds the lease.
/// </summary>
public sealed partial class MultiDeviceCredentialVault : IAsyncDisposable, IMultiDeviceAuthorizer
{
    internal Action? AfterReviewClaimForTest { get; set; }
    internal Func<byte[]>? NextCredentialForTest { get; set; }
    private sealed record Entry(string Name, string Fingerprint, string Token, string Platform, int Slot)
    {
        public override string ToString() => $"Entry {{ Name = {Name}, Token = [redacted] }}";
    }
    private sealed record Document(int Version, Entry[] Devices);
    private const int MaxFileBytes = 65536;
    private readonly string directory;
    private readonly string file;
    private readonly IMultiDeviceSecretProtector protector;
    private readonly FileStream lease;
    private readonly SemaphoreSlim gate = new(1, 1);
    internal SemaphoreSlim SessionChangeGate { get; } = new(1, 1);
    private VaultSnapshot vaultSnapshot = new(1, [], []);
    private int disposed;
    private int writeUncertain;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    private MultiDeviceCredentialVault(string directory, IMultiDeviceSecretProtector protector, FileStream lease)
    {
        this.directory = directory;
        file = Path.Combine(directory, "multi-device-trust.bin");
        this.protector = protector;
        this.lease = lease;
    }

    public static async Task<MultiDeviceCredentialVault> OpenAsync(string directory, IMultiDeviceSecretProtector protector, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("A private absolute directory is required.", nameof(directory));
        directory = Path.GetFullPath(directory);
        RejectLinks(directory);
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        var leasePath = Path.Combine(directory, ".multi-device-trust.lock");
        RejectLinks(leasePath);
        var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var vault = new MultiDeviceCredentialVault(directory, protector, lease);
        try
        {
            RejectLinks(vault.file);
            if (Directory.Exists(vault.file)) throw new InvalidDataException("Protected device trust path is a directory.");
            if (!File.Exists(vault.file)) return vault;
            var encrypted = await ReadBoundedVaultFileAsync(vault.file, ct, MaxVaultFileBytes);
            var plain = protector.Unprotect(encrypted);
            try
            {
                vault.vaultSnapshot = ParseVaultSnapshot(plain);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            return vault;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public bool IsAuthorized(string name, string token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref writeUncertain) != 0) return false;
        if (string.IsNullOrEmpty(name) || token is not { Length: 44 }) return false;
        byte[] supplied;
        try { supplied = Convert.FromBase64String(token); }
        catch (FormatException) { return false; }
        try
        {
            var entry = Volatile.Read(ref vaultSnapshot).Devices.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry is null || supplied.Length != 32) return false;
            var expected = Convert.FromBase64String(entry.Token);
            try { return CryptographicOperations.FixedTimeEquals(supplied, expected); }
            finally { CryptographicOperations.ZeroMemory(expected); }
        }
        finally { CryptographicOperations.ZeroMemory(supplied); }
    }

    public IReadOnlyList<TrustedDeviceIdentity> GetTrustedDevices()
    {
        ThrowIfDisposed();
        return Volatile.Read(ref vaultSnapshot).Devices.Select(x => new TrustedDeviceIdentity(x.Name, x.Fingerprint, x.Platform, x.Slot)).ToArray();
    }

    internal byte[] ReadTokenForEndpoint(string name)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref writeUncertain) != 0) throw new UnauthorizedAccessException("Device trust is unavailable; reopen the store.");
        var entry = Volatile.Read(ref vaultSnapshot).Devices.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) throw new UnauthorizedAccessException("Device trust was revoked.");
        return Convert.FromBase64String(entry.Token);
    }

    /// <summary>
    /// Linearizes a bounded in-memory mutation with this vault's grant changes.
    /// The internal caller must use a short synchronous action: no I/O, blocking,
    /// reentry into the vault, session-change lock, or untrusted callbacks.
    /// This is not a general-purpose authentication or network API.
    /// </summary>
    internal async Task<bool> TryCommitAuthorizedAsync(string name, byte[] expectedToken, Action commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedToken);
        ArgumentNullException.ThrowIfNull(commit);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Volatile.Read(ref writeUncertain) != 0) return false;
            if (expectedToken.Length != 32 || string.IsNullOrEmpty(name)) return false;
            var entry = vaultSnapshot.Devices.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return false;
            var current = Convert.FromBase64String(entry.Token);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(current, expectedToken)) return false;
                commit();
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(current); }
        }
        finally { gate.Release(); }
    }

    public async Task<string> AddConfirmedAsync(string name, string fingerprint, CancellationToken ct = default) =>
        (await AddConfirmedWithSlotAsync(name, fingerprint, "unknown", ct)).Token;

    public Task<MultiDeviceGrant> AddConfirmedWithSlotAsync(string name, string fingerprint, string platform, CancellationToken ct = default) =>
        AddConfirmedWithSlotCoreAsync(name, fingerprint, platform, null, null, ct);

    internal Task<MultiDeviceGrant> AddReviewedWithSlotAsync(string name, string fingerprint, string platform, Action validateReview, Action claimPublish, CancellationToken ct) =>
        AddConfirmedWithSlotCoreAsync(name, fingerprint, platform, validateReview ?? throw new ArgumentNullException(nameof(validateReview)), claimPublish ?? throw new ArgumentNullException(nameof(claimPublish)), ct);

    private async Task<MultiDeviceGrant> AddConfirmedWithSlotCoreAsync(string name, string fingerprint, string platform, Action? validateReview, Action? claimPublish, CancellationToken ct)
    {
        DeskflowProfile.ValidateName(name);
        var normalized = DeskLinkFingerprint.Normalize(fingerprint);
        if (platform is not ("windows" or "mac" or "unknown")) throw new ArgumentException("Unsupported device platform.", nameof(platform));
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            validateReview?.Invoke();
            var current = vaultSnapshot;
            var old = current.Devices;
            if (old.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) || x.Fingerprint == normalized))
                throw new ArgumentException("Device name or fingerprint is already registered.");
            if (old.Length >= MultiDeviceCredentials.MaximumPeers) throw new InvalidOperationException("The device limit has been reached.");
            var random = NextCredentialForTest?.Invoke() ?? RandomNumberGenerator.GetBytes(32);
            string token;
            try
            {
                var digest = VaultTokenDigest(random);
                if (current.Version >= 2 && (current.Transactions.Any(x => FixedDigestEquals(x.CredentialDigest, digest)) ||
                    (current.Retired?.Any(x => FixedDigestEquals(x.CredentialDigest, digest)) ?? false)))
                    throw new InvalidOperationException("A previous credential cannot be reused.");
                token = Convert.ToBase64String(random);
            }
            finally { CryptographicOperations.ZeroMemory(random); }
            var slot = Enumerable.Range(1, MultiDeviceCredentials.MaximumPeers).First(x => old.All(p => p.Slot != x));
            var next = current with { Devices = old.Append(new Entry(name, normalized, token, platform, slot)).ToArray() };
            await CommitAsync(next, ct, claimPublish);
            Volatile.Write(ref vaultSnapshot, next);
            return new(token, slot);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> RevokeAsync(string name, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            var current = vaultSnapshot;
            var old = current.Devices;
            var next = old.Where(x => !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (next.Length == old.Length) return false;
            var removed = old.Single(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            var digest = current.Version >= 2 ? VaultTokenDigest(removed.Token) : null;
            var transactions = current.Version >= 2
                ? current.Transactions.Select(x => x.State is ("committed" or "applied" or "finished" or "retired") && FixedDigestEquals(x.CredentialDigest, digest!) ? x with { State = "revoked" } : x).ToArray()
                : current.Transactions;
            var updated = current with { Devices = next, Transactions = transactions };
            await CommitAsync(updated, ct);
            Volatile.Write(ref vaultSnapshot, updated);
            return true;
        }
        finally { gate.Release(); }
    }

    private async Task CommitAsync(VaultSnapshot next, CancellationToken ct, Action? validateReview = null)
    {
        ValidateVaultSnapshot(next);
        var plain = SerializeVaultSnapshot(next);
        byte[] encrypted;
        try { encrypted = protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length is <= 0 or > MaxVaultFileBytes) throw new InvalidDataException("Protected device trust is invalid or oversized.");
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            RejectLinks(directory); RejectLinks(file); RejectLinks(temporary);
            await File.WriteAllBytesAsync(temporary, encrypted, ct);
            RejectLinks(file);
            ct.ThrowIfCancellationRequested();
            validateReview?.Invoke();
            if (validateReview is not null) AfterReviewClaimForTest?.Invoke();
            try { File.Move(temporary, file, true); }
            catch { Volatile.Write(ref writeUncertain, 1); throw; }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(Document document)
    {
        if (document.Version != 1 || document.Devices is null || document.Devices.Length > MultiDeviceCredentials.MaximumPeers)
            throw new InvalidDataException("Device trust version or count is invalid.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var slots = new HashSet<int>();
        foreach (var entry in document.Devices)
        {
            if (entry is null) throw new InvalidDataException("Device trust contains an empty entry.");
            try
            {
                DeskflowProfile.ValidateName(entry.Name);
                if (entry.Platform is not ("windows" or "mac" or "unknown"))
                    throw new InvalidDataException("Device platform is invalid.");
                if (entry.Slot is < 1 or > MultiDeviceCredentials.MaximumPeers || !slots.Add(entry.Slot))
                    throw new InvalidDataException("Device slot is invalid or duplicated.");
                var normalized = DeskLinkFingerprint.Normalize(entry.Fingerprint);
                if (normalized != entry.Fingerprint || !names.Add(entry.Name) || !fingerprints.Add(normalized))
                    throw new InvalidDataException("Device trust contains duplicate or invalid identities.");
                var token = Convert.FromBase64String(entry.Token);
                try { if (token.Length != 32 || entry.Token.Length != 44) throw new InvalidDataException("Device token is invalid."); }
                finally { CryptographicOperations.ZeroMemory(token); }
            }
            catch (Exception error) when (error is ArgumentException or FormatException)
            { throw new InvalidDataException("Device trust contains invalid data.", error); }
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Device trust cannot use a linked path."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref writeUncertain) != 0) throw new InvalidOperationException("Device trust write outcome is uncertain; close and reopen the store.");
    }
    public async ValueTask DisposeAsync()
    {
        PairingTlsConsentSession? stopping = null; Task? draining = null; RecoveryActivationSession? recoveryStopping = null; Task? recoveryDraining = null;
        TaskCompletionSource? completion = null; Task? existing;
        await gate.WaitAsync();
        try
        {
            existing = isolatedDisposeTask;
            if (existing is null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously); isolatedDisposeTask = completion.Task;
                Interlocked.Exchange(ref disposed, 1);
                Volatile.Write(ref vaultSnapshot, new VaultSnapshot(1, [], []));
                stopping = activePrepareSession; draining = activePrepareDrained?.Task;
                recoveryStopping = activeRecoverySession; recoveryDraining = activeRecoveryDrained?.Task;
            }
        }
        finally { gate.Release(); }
        if (existing is not null) { await existing; return; }
        try
        {
            Exception? stopError = null;
            try { stopping?.StopLivePrepareTest(); }
            catch (Exception error) { stopError = error; }
            try { recoveryStopping?.Stop(); }
            catch (Exception error) { stopError ??= error; }
            if (draining is not null) await draining;
            if (recoveryDraining is not null) await recoveryDraining;
            await lease.DisposeAsync();
            if (stopError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopError).Throw();
            completion!.TrySetResult();
        }
        catch (Exception error) { completion!.TrySetException(error); throw; }
    }
}
