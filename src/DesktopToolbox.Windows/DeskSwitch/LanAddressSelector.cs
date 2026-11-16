using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>One selection policy for the paired control and data endpoints.</summary>
public sealed record LanAdapterSnapshot(string Name, string Description, NetworkInterfaceType Type,
    OperationalStatus Status, IReadOnlyList<IPAddress> Addresses);

public static class LanAddressSelector
{
    private static readonly string[] VirtualMarkers = ["vpn", "virtual", "wintun", "tap", "meta", "hyper-v", "vmware", "vbox", "wireguard", "tailscale"];

    public static IPAddress? Select(IEnumerable<LanAdapterSnapshot> adapters, string? controlAddress = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var eligible = adapters.Where(IsEligibleAdapter).SelectMany(adapter => adapter.Addresses)
            .Where(IsPrivateLan).ToArray();
        // If a live control endpoint has already chosen an address, using a
        // different network would split the authenticated control and data
        // channels. Wait for that same address to become available again.
        if (!string.IsNullOrWhiteSpace(controlAddress))
            return IPAddress.TryParse(controlAddress, out var preferred) && eligible.Any(address => address.Equals(preferred))
                ? preferred : null;
        return eligible.FirstOrDefault();
    }

    public static IPAddress? SelectSystemAddress(string? controlAddress = null) => SelectAvailable(() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => IsEligibleAdapter(new(adapter.Name, adapter.Description, adapter.NetworkInterfaceType,
                adapter.OperationalStatus, [])))
            .Select(adapter => new LanAdapterSnapshot(adapter.Name, adapter.Description, adapter.NetworkInterfaceType,
                adapter.OperationalStatus, adapter.GetIPProperties().UnicastAddresses.Select(value => value.Address).ToArray())), controlAddress);

    // A transient interface-enumeration failure is not permission to retain a
    // previous external listener: callers treat null as unavailable and close it.
    internal static IPAddress? SelectAvailable(Func<IEnumerable<LanAdapterSnapshot>> source, string? controlAddress)
    {
        try { return Select(source(), controlAddress); }
        catch (NetworkInformationException) { return null; }
        catch (SocketException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
    }

    private static bool IsEligibleAdapter(LanAdapterSnapshot adapter) => adapter.Status == OperationalStatus.Up &&
        adapter.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
        !VirtualMarkers.Any(word => (adapter.Name + " " + adapter.Description).Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool IsPrivateLan(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
    }
}
