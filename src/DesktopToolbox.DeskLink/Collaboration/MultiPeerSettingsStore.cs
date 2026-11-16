using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink.Coordination;

namespace DesktopToolbox.DeskLink.Collaboration;

public sealed record MultiPeerMonitorSettings(string MonitorId, uint HostInput, uint PeerInput);

public sealed record MultiPeerFileSettings(string? ReceiveDirectory = null,
    IReadOnlyList<SharedDirectory>? Shares = null, bool ClipboardEnabled = false,
    int PreviewRefreshSeconds = PreviewCaptureCadence.DefaultRefreshSeconds, MultiPeerMonitorSettings? Monitor = null);

/// <summary>Protected per-grant settings; corrupted data is retained and never silently reset.</summary>
public sealed class MultiPeerSettingsStore : IAsyncDisposable
{
    private sealed record Document(int Version, Dictionary<string, MultiPeerFileSettings> Devices, MultiScreenConfiguration? Screens = null);
    private const int MaximumBytes = 262144;
    private readonly string directory, file;
    private readonly IMultiDeviceSecretProtector protector;
    private readonly FileStream lease;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, MultiPeerFileSettings> entries = new(StringComparer.Ordinal);
    private MultiScreenConfiguration? screens;
    private bool disposed;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 12 };

    private MultiPeerSettingsStore(string directory, IMultiDeviceSecretProtector protector, FileStream lease)
    { this.directory = directory; this.protector = protector; this.lease = lease; file = Path.Combine(directory, "device-settings.bin"); }

    public static async Task<MultiPeerSettingsStore> OpenAsync(string directory, IMultiDeviceSecretProtector protector, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("A private absolute directory is required.");
        directory = Path.GetFullPath(directory); RejectLinks(directory); PrivateFiles.EnsureDirectory(directory); RejectLinks(directory);
        var lockPath = Path.Combine(directory, ".settings.lock"); RejectLinks(lockPath);
        var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var result = new MultiPeerSettingsStore(directory, protector, lease);
        try
        {
            RejectLinks(result.file);
            if (!File.Exists(result.file)) return result;
            var info = new FileInfo(result.file);
            if (info.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected device settings are invalid.");
            var encrypted = await File.ReadAllBytesAsync(result.file, ct);
            var plain = protector.Unprotect(encrypted);
            try
            {
                if (plain.Length > MaximumBytes) throw new InvalidDataException("Device settings are oversized.");
                var doc = JsonSerializer.Deserialize<Document>(plain, Json) ?? throw new InvalidDataException("Device settings are empty.");
                if (doc.Version != 1 || doc.Devices is null || doc.Devices.Count > 128) throw new InvalidDataException("Device settings are invalid.");
                foreach (var item in doc.Devices) { ValidateKey(item.Key); Validate(item.Value); }
                result.entries = new(doc.Devices, StringComparer.Ordinal);
                try { result.screens = doc.Screens?.ValidateAndCopy(); }
                catch (ArgumentException error) { throw new InvalidDataException("Protected screen configuration is invalid.", error); }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            return result;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public MultiPeerFileSettings Get(string key)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ValidateKey(key);
        return entries.TryGetValue(key, out var value) ? Copy(value) : new();
    }

    public async Task SetAsync(string key, MultiPeerFileSettings value, CancellationToken ct = default)
    {
        ValidateKey(key); Validate(value);
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var next = new Dictionary<string, MultiPeerFileSettings>(entries, StringComparer.Ordinal) { [key] = Copy(value) };
            if (next.Count > 128) throw new InvalidOperationException("Too many saved device settings.");
            await CommitAsync(next, ct);
        }
        finally { gate.Release(); }
    }

    public async Task SetMonitorsAsync(IReadOnlyDictionary<string, MultiPeerMonitorSettings?> monitors, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var requested = monitors.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (requested.Count > 4) throw new ArgumentException("Too many display mappings.", nameof(monitors));
        foreach (var key in requested.Keys) ValidateKey(key);
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var next = new Dictionary<string, MultiPeerFileSettings>(entries, StringComparer.Ordinal);
            foreach (var (key, monitor) in requested)
            {
                var value = (next.TryGetValue(key, out var existing) ? existing : new MultiPeerFileSettings()) with { Monitor = monitor };
                Validate(value); next[key] = Copy(value);
            }
            if (next.Count > 128) throw new InvalidOperationException("Too many saved device settings.");
            await CommitAsync(next, ct);
        }
        finally { gate.Release(); }
    }

    internal MultiScreenConfiguration? GetScreenConfiguration()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return screens?.ValidateAndCopy();
    }

    internal async Task SetScreenConfigurationAsync(MultiScreenConfiguration? value, CancellationToken ct = default)
    {
        var requested = value?.ValidateAndCopy();
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await CommitAsync(new Dictionary<string, MultiPeerFileSettings>(entries, StringComparer.Ordinal), ct, requested, replaceScreens: true);
        }
        finally { gate.Release(); }
    }

    public async Task RetainAsync(IReadOnlySet<string> activeKeys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activeKeys);
        foreach (var key in activeKeys) ValidateKey(key);
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var next = entries.Where(item => activeKeys.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            var removeScreens = screens is not null && screens.Peers.Any(peer => !activeKeys.Contains(peer.GrantKey));
            if (next.Count != entries.Count || removeScreens) await CommitAsync(next, ct, null, replaceScreens: removeScreens);
        }
        finally { gate.Release(); }
    }

    private async Task CommitAsync(Dictionary<string, MultiPeerFileSettings> next, CancellationToken ct,
        MultiScreenConfiguration? nextScreens = null, bool replaceScreens = false)
    {
        var savedScreens = replaceScreens ? nextScreens : screens;
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Document(1, next, savedScreens), Json);
        byte[] encrypted;
        try { encrypted = protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected device settings are oversized.");
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            RejectLinks(directory); RejectLinks(file); RejectLinks(temp);
            await File.WriteAllBytesAsync(temp, encrypted, ct);
            RejectLinks(file); File.Move(temp, file, true);
            entries = next;
            screens = savedScreens;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static MultiPeerFileSettings Copy(MultiPeerFileSettings value) => value with { Shares = value.Shares?.ToArray() };
    private static void ValidateKey(string key)
    { if (key is not { Length: 64 } || key.Any(c => !char.IsAsciiHexDigitUpper(c))) throw new InvalidDataException("Invalid device settings identity."); }
    internal static void Validate(MultiPeerFileSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        PreviewCaptureCadence.ValidateRefreshSeconds(value.PreviewRefreshSeconds);
        if (value.Monitor is { } display && (string.IsNullOrWhiteSpace(display.MonitorId) || display.MonitorId.Length > 256 ||
            display.MonitorId.Any(char.IsControl) || display.HostInput is 0 or > 255 || display.PeerInput is 0 or > 255 || display.HostInput == display.PeerInput))
            throw new InvalidDataException("Invalid display input preference.");
        if (value.ReceiveDirectory is { } receive) _ = CollaborationPaths.Resolve(receive);
        if (value.Shares is not { } shares) return;
        if (shares.Count > 64 || shares.Any(x => x is null) || shares.Select(x => x.RootId).Distinct(StringComparer.Ordinal).Count() != shares.Count)
            throw new InvalidDataException("Invalid shared directories.");
        foreach (var share in shares)
        {
            if (share is null || share.RootId.Length is < 1 or > 64 || share.RootId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') || share.Name.Length is < 1 or > 128)
                throw new InvalidDataException("Invalid shared directory.");
            _ = CollaborationPaths.Resolve(share.Path);
        }
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Settings path cannot contain links."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (!disposed) { disposed = true; entries.Clear(); screens = null; await lease.DisposeAsync(); } }
        finally { gate.Release(); }
    }
}
