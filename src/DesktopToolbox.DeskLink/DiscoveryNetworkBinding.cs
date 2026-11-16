using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DesktopToolbox.DeskLink;

// A system-reported mapping, not proof that an adapter is physical or that it may send packets.
internal sealed record DiscoveryNetworkInterface(string Id, string Name, NetworkInterfaceType Type,
    OperationalStatus Status, int Index, IPAddress Address, IPAddress Mask);

/// <summary>Tracks one approved address and permanently invalidates on a mapping change.</summary>
public sealed class DiscoveryNetworkBinding
{
    private readonly object gate = new();
    private readonly Func<IReadOnlyList<DiscoveryNetworkInterface>> read;
    private readonly Func<bool> approvedNetworkAvailable;
    private bool revoked;
    internal DiscoveryNetworkInterface Interface { get; }
    internal bool IsLive { get; }
    /// <summary>Whether this handle originated from a real approved binding; it does not assert current availability.</summary>
    public bool HasLiveNetworkOrigin => IsLive;
    public IPAddress Address => new(Interface.Address.GetAddressBytes());
    public IPAddress SubnetMask => new(Interface.Mask.GetAddressBytes());
    public int InterfaceIndex => Interface.Index;
    public string InterfaceId => Interface.Id;
    /// <summary>Reports whether Windows currently describes the approved interface as hardware.</summary>
    public bool IsHardwareAvailable() => WindowsHardwareInterface.IsHardware(this);

    private DiscoveryNetworkBinding(DiscoveryNetworkInterface selected,
        Func<IReadOnlyList<DiscoveryNetworkInterface>> read, Func<bool> approvedNetworkAvailable, bool isLive)
    { Interface = selected; this.read = read; this.approvedNetworkAvailable = approvedNetworkAvailable; IsLive = isLive; }

    public static DiscoveryNetworkBinding CreateForCurrentNetwork(VerifiedLanBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.IsStillAvailable()) throw new InvalidOperationException("Approved network is unavailable.");
        var selected = SelectForApprovedNetwork(binding, ReadInterfaces());
        if (!binding.IsStillAvailable()) throw new InvalidOperationException("Approved network is unavailable.");
        return new(selected, ReadInterfaces, binding.IsStillAvailable, true);
    }

    // Synthetic bindings are visibly marked and cannot themselves approve a live network.
    internal static DiscoveryNetworkBinding FromSnapshotsForTest(IPAddress address,
        Func<IReadOnlyList<DiscoveryNetworkInterface>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return new(Select(address, read()), read, () => true, false);
    }

    public bool IsStillAvailable()
    {
        lock (gate)
        {
            if (revoked) return false;
            try
            {
                if (!approvedNetworkAvailable() || Select(Interface.Address, read()) != Interface || !approvedNetworkAvailable())
                    revoked = true;
            }
            catch (Exception e) when (e is NetworkInformationException or SocketException or IOException or
                UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            { revoked = true; }
            return !revoked;
        }
    }

    internal static DiscoveryNetworkInterface Select(IPAddress address, IReadOnlyList<DiscoveryNetworkInterface> interfaces)
    {
        ArgumentNullException.ThrowIfNull(address); ArgumentNullException.ThrowIfNull(interfaces);
        var matches = interfaces.Where(item => item.Address.Equals(address)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("The approved address has no unique interface mapping.");
        var selected = matches[0];
        if (string.IsNullOrWhiteSpace(selected.Id) || string.IsNullOrWhiteSpace(selected.Name) || selected.Index <= 0 ||
            selected.Status != OperationalStatus.Up || selected.Type is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211) ||
            interfaces.Any(item => item.Id == selected.Id && item.Index != selected.Index || item.Index == selected.Index && item.Id != selected.Id))
            throw new InvalidOperationException("The approved interface metadata is invalid or ambiguous.");
        try { _ = new DiscoverySubnet(selected.Address, selected.Mask); }
        catch (ArgumentException error) { throw new InvalidOperationException("The approved interface subnet is invalid.", error); }
        return selected;
    }

    internal static DiscoveryNetworkInterface SelectForApprovedNetwork(VerifiedLanBinding binding,
        IReadOnlyList<DiscoveryNetworkInterface> interfaces)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var selected = Select(binding.Address, interfaces);
        if (!binding.MatchesInterface(selected.Name, selected.Id))
            throw new InvalidOperationException("The approved address moved to another interface.");
        return selected;
    }

    internal static DiscoveryNetworkInterface[] ProjectAdapter(string id, string name, NetworkInterfaceType type,
        OperationalStatus status, IEnumerable<(IPAddress Address, IPAddress Mask)> addresses, Func<int> getIndex)
    {
        var ipv4 = addresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork).ToArray();
        if (ipv4.Length == 0) return [];
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("IPv4 adapter identity is unavailable.");
        var index = getIndex();
        if (index <= 0) throw new InvalidOperationException("IPv4 interface index is unavailable.");
        return ipv4.Select(item => new DiscoveryNetworkInterface(id, name, type, status, index, item.Address, item.Mask)).ToArray();
    }

    private static IReadOnlyList<DiscoveryNetworkInterface> ReadInterfaces()
    {
        var result = new List<DiscoveryNetworkInterface>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = adapter.GetIPProperties();
            var addresses = properties.UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(item => (item.Address, item.IPv4Mask ?? IPAddress.Any));
            result.AddRange(ProjectAdapter(adapter.Id, adapter.Name, adapter.NetworkInterfaceType, adapter.OperationalStatus,
                addresses, () => properties.GetIPv4Properties()?.Index ?? throw new InvalidOperationException("IPv4 metadata unavailable.")));
        }
        return result;
    }
}
