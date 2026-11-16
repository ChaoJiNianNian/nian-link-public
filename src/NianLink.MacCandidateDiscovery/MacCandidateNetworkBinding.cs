using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace NianLink.MacCandidateDiscovery;

// Evidence is a system classification, not proof of hardware or peer identity.
internal sealed record MacNetworkRecord(string Id, string Name, int Index, NetworkInterfaceType Type,
    OperationalStatus Status, IPAddress Address, IPAddress Mask);
internal sealed record MacWifiEvidence(string BsdName, int Index);

/// <summary>Candidate-only binding. Synthetic instances must never authorize live network activity.</summary>
public sealed class MacCandidateNetworkBinding : IDisposable
{
    private const int Limit = 1024;
    private readonly object gate = new();
    private readonly Snapshot baseline;
    private readonly Func<IReadOnlyList<MacNetworkRecord>>? networks;
    private readonly Func<IReadOnlyList<MacWifiEvidence>>? wifi;
    private readonly MacBindingRevalidator? liveState;
    private readonly DiscoveryNetworkBinding? sharedBinding;
    private int disposed;
    private bool revoked;

    internal sealed record Snapshot(string Id, string Name, int Index, NetworkInterfaceType Type,
        OperationalStatus Status, uint Address, uint Mask, string BsdName);

    private MacCandidateNetworkBinding(Snapshot baseline, Func<IReadOnlyList<MacNetworkRecord>> networks,
        Func<IReadOnlyList<MacWifiEvidence>> wifi)
    { this.baseline = baseline; this.networks = networks; this.wifi = wifi; }

    private MacCandidateNetworkBinding(Snapshot baseline, VerifiedLanBinding approved, DiscoveryNetworkBinding shared)
    {
        this.baseline = baseline;
        sharedBinding = shared;
        liveState = MacBindingRevalidator.ForSystem(baseline, approved, shared);
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        try { NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged; }
        catch { NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged; liveState.Revoke(); throw; }
    }

    /// <summary>Indicates origin only. Always revalidate and observe RevocationToken before use.</summary>
    public bool HasLiveNetworkOrigin => liveState is not null;
    public IPAddress Address => ToAddress(baseline.Address);
    public IPAddress SubnetMask => ToAddress(baseline.Mask);
    public string InterfaceId => baseline.Id;
    public int InterfaceIndex => baseline.Index;
    public string BsdName => baseline.BsdName;
    public CancellationToken RevocationToken => liveState?.RevocationToken ?? new CancellationToken(true);
    internal DiscoveryNetworkBinding SharedBinding => sharedBinding is not null && !RevocationToken.IsCancellationRequested
        ? sharedBinding : throw new InvalidOperationException("Real network binding is unavailable.");

    public static MacCandidateNetworkBinding CreateForCurrentNetwork(VerifiedLanBinding approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        throw new PlatformNotSupportedException("Use the asynchronous macOS binding factory.");
    }

    public static async Task<MacCandidateNetworkBinding> CreateForCurrentNetworkAsync(VerifiedLanBinding approved,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(approved);
        cancellation.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS is required.");
        var observed = await new MacReadOnlyMappingProbe().ReadCurrentPinnedAsync(approved, TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
        var shared = observed.SharedBinding;
        if (shared is null || !shared.HasLiveNetworkOrigin) throw new InvalidOperationException("Real network binding unavailable.");
        var result = new MacCandidateNetworkBinding(observed.Selected, approved, shared);
        try
        {
            if (!await result.IsStillAvailableAsync(cancellation).ConfigureAwait(false))
                throw new InvalidOperationException("Approved network changed while binding.");
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public Task<bool> IsStillAvailableAsync(CancellationToken cancellation = default) =>
        liveState is null ? Task.FromResult(false) : liveState.CheckAsync(cancellation);

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => liveState?.Revoke();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => liveState?.Revoke();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (liveState is not null)
        {
            liveState.Revoke();
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }
        else { lock (gate) revoked = true; }
    }

    internal static MacCandidateNetworkBinding FromSyntheticForTest(MacNetworkRecord expected,
        Func<IReadOnlyList<MacNetworkRecord>> networks, Func<IReadOnlyList<MacWifiEvidence>> wifi)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(networks);
        ArgumentNullException.ThrowIfNull(wifi);
        var expectedAddress = ToUInt(expected.Address);
        var selected = Select(expectedAddress, networks(), wifi());
        if (Freeze(expected, selected.BsdName) != selected)
            throw new InvalidOperationException("Selected interface does not match the approved snapshot.");
        return new(selected, networks, wifi);
    }

    public bool IsStillAvailable()
    {
        if (liveState is not null) return false; // Live checks must await fresh system evidence.
        lock (gate)
        {
            if (revoked) return false;
            try { if (Select(baseline.Address, networks!(), wifi!()) != baseline) revoked = true; }
            catch (Exception) { revoked = true; }
            return !revoked;
        }
    }

    internal static Snapshot Select(uint address, IReadOnlyList<MacNetworkRecord> networks,
        IReadOnlyList<MacWifiEvidence> wifi)
    {
        if (networks is null || wifi is null || networks.Count is < 1 or > Limit || wifi.Count is < 1 or > Limit)
            throw new InvalidOperationException("Missing or excessive network evidence.");
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var indices = new Dictionary<int, string>();
        var interfaceMetadata = new Dictionary<string, (string Name, NetworkInterfaceType Type, OperationalStatus Status)>(StringComparer.Ordinal);
        var addresses = new HashSet<(string Id, uint Address)>();
        foreach (var n in networks)
        {
            if (n is null || string.IsNullOrWhiteSpace(n.Id) || n.Id.Length > 256 ||
                string.IsNullOrWhiteSpace(n.Name) || n.Name.Length > 256 || n.Index <= 0)
                throw new InvalidOperationException("Invalid network identity.");
            var metadata = (n.Name, n.Type, n.Status);
            if ((ids.TryGetValue(n.Id, out var index) && index != n.Index) ||
                (indices.TryGetValue(n.Index, out var id) && id != n.Id) ||
                (interfaceMetadata.TryGetValue(n.Id, out var previous) && previous != metadata) ||
                !addresses.Add((n.Id, ToUInt(n.Address))))
                throw new InvalidOperationException("Network snapshot is ambiguous.");
            ids[n.Id] = n.Index;
            indices[n.Index] = n.Id;
            interfaceMetadata[n.Id] = metadata;
        }
        var bsdNames = new HashSet<string>(StringComparer.Ordinal);
        var wifiIndices = new HashSet<int>();
        foreach (var w in wifi)
            if (w is null || !ValidBsdName(w.BsdName) || w.Index <= 0 ||
                !bsdNames.Add(w.BsdName) || !wifiIndices.Add(w.Index))
                throw new InvalidOperationException("Wi-Fi evidence is invalid or duplicated.");
        var matching = networks.Where(n => n is not null && ToUInt(n.Address) == address).ToArray();
        if (matching.Length != 1) throw new InvalidOperationException("Address is not uniquely mapped.");
        var selected = matching[0];
        var relevant = wifi.Where(w => w is not null &&
            (w.Index == selected.Index || w.BsdName == selected.Id || w.BsdName == selected.Name)).ToArray();
        if (relevant.Length != 1 ||
            relevant[0].Index != selected.Index ||
            (relevant[0].BsdName != selected.Id && relevant[0].BsdName != selected.Name))
            throw new InvalidOperationException("Wi-Fi classification is absent or ambiguous.");
        return Freeze(selected, relevant[0].BsdName);
    }

    private static Snapshot Freeze(MacNetworkRecord n, string bsd)
    {
        if (string.IsNullOrWhiteSpace(n.Id) || n.Id.Length > 256 || string.IsNullOrWhiteSpace(n.Name) ||
            n.Name.Length > 256 || n.Index <= 0 || n.Type != NetworkInterfaceType.Wireless80211 ||
            n.Status != OperationalStatus.Up || !ValidBsdName(bsd))
            throw new InvalidOperationException("Invalid interface snapshot.");
        var address = ToUInt(n.Address); var mask = ToUInt(n.Mask);
        try { _ = new DiscoverySubnet(ToAddress(address), ToAddress(mask)); }
        catch (ArgumentException e) { throw new InvalidOperationException("Invalid interface subnet.", e); }
        return new(n.Id, n.Name, n.Index, n.Type, n.Status, address, mask, bsd);
    }
    private static bool ValidBsdName(string? name) => name is { Length: > 0 and <= 63 } &&
        name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');
    internal static uint ToUInt(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("IPv4 address required.");
        return BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }
    private static IPAddress ToAddress(uint value)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return new IPAddress(bytes);
    }
}
