using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DesktopToolbox.DeskLink;

internal sealed record LanInterfaceSnapshot(string Name, string Description, NetworkInterfaceType Type,
    OperationalStatus Status, IReadOnlyList<IPAddress> Addresses, string? Id = null);

/// <summary>Explicit consent and an active private physical interface are required.</summary>
public sealed class VerifiedLanBinding
{
    private static readonly string[] VirtualMarkers = ["vpn", "virtual", "wintun", "tap", "meta", "hyper-v", "vmware", "vbox", "wireguard", "tailscale"];
    private readonly string interfaceName;
    private readonly string? interfaceId;
    private readonly bool addressUniqueAtApproval;
    private readonly IPAddress address;
    private readonly bool liveProbe;
    private readonly object probeGate = new();
    private long checkedAt;
    private int revoked;
    public IPAddress Address => new(address.GetAddressBytes());
    // A missing captured ID cannot be upgraded into a discovery network binding.
    internal bool MatchesInterface(string name, string id) => addressUniqueAtApproval && !string.IsNullOrWhiteSpace(interfaceId) &&
        string.Equals(interfaceName, name, StringComparison.Ordinal) && string.Equals(interfaceId, id, StringComparison.Ordinal);
    private VerifiedLanBinding(IPAddress address, string interfaceName, string? interfaceId, bool addressUniqueAtApproval, bool liveProbe)
    { this.address = new(address.GetAddressBytes()); this.interfaceName = interfaceName; this.interfaceId = interfaceId; this.addressUniqueAtApproval = addressUniqueAtApproval; this.liveProbe = liveProbe; }

    public static VerifiedLanBinding VerifyCurrentNetwork(IPAddress address, bool userApproved)
    {
        ArgumentNullException.ThrowIfNull(address);
        var captured = new IPAddress(address.GetAddressBytes());
        return FromSnapshots(captured, userApproved, ReadInterfaces(), liveProbe: true);
    }

    public static IReadOnlyList<IPAddress> GetEligibleAddresses() => EligibleAddresses(ReadInterfaces());

    internal static IReadOnlyList<IPAddress> EligibleAddresses(IEnumerable<LanInterfaceSnapshot> interfaces)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        var snapshot = interfaces.ToArray();
        return snapshot.SelectMany(x => x.Addresses).Distinct()
            .Where(address => IsEligible(address, snapshot, null)).ToArray();
    }

    public bool IsStillAvailable()
    {
        if (!liveProbe) return false; // Synthetic snapshots never grant real network access.
        if (Volatile.Read(ref revoked) != 0) return false;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref checkedAt) < System.Diagnostics.Stopwatch.Frequency) return true;
        lock (probeGate)
        {
            if (revoked != 0) return false;
            now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - checkedAt < System.Diagnostics.Stopwatch.Frequency) return true;
            bool available;
            try { available = IsEligible(Address, ReadInterfaces(), interfaceName); }
            catch (Exception error) when (error is NetworkInformationException or SocketException or UnauthorizedAccessException or IOException)
            { available = false; }
            if (!available) Volatile.Write(ref revoked, 1);
            Interlocked.Exchange(ref checkedAt, now);
            return available;
        }
    }

    internal static VerifiedLanBinding FromSnapshots(IPAddress address, bool userApproved, IEnumerable<LanInterfaceSnapshot> interfaces, bool liveProbe = false)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(interfaces);
        address = new IPAddress(address.GetAddressBytes());
        var snapshot = interfaces.ToArray();
        if (!userApproved || !IsEligible(address, snapshot, null))
            throw new InvalidOperationException("Choose and explicitly approve an active private LAN interface.");
        var adapter = snapshot.First(x => IsEligible(address, [x], null));
        var unique = snapshot.Sum(item => item.Addresses.Count(itemAddress => itemAddress.Equals(address))) == 1;
        return new(address, adapter.Name, adapter.Id, unique, liveProbe);
    }

    private static bool IsEligible(IPAddress address, IEnumerable<LanInterfaceSnapshot> interfaces, string? name)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        if (!(bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31)) return false;
        return interfaces.Any(adapter => (name is null || adapter.Name == name) &&
            adapter.Status == OperationalStatus.Up &&
            adapter.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
            !VirtualMarkers.Any(word => (adapter.Name + " " + adapter.Description).Contains(word, StringComparison.OrdinalIgnoreCase)) &&
            adapter.Addresses.Contains(address));
    }

    private static LanInterfaceSnapshot[] ReadInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces().Select(adapter => new LanInterfaceSnapshot(
            adapter.Name, adapter.Description, adapter.NetworkInterfaceType, adapter.OperationalStatus,
            adapter.GetIPProperties().UnicastAddresses.Select(item => item.Address).ToArray(), adapter.Id)).ToArray();
}
