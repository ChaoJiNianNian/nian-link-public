using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NianLink.MacResponderCandidate;

internal sealed record InterfaceIPv4Snapshot(string Id, int Index, OperationalStatus Status, IReadOnlyList<IPAddress> Addresses);

internal static class SingleIPv4Policy
{
    internal static bool Matches(IReadOnlyList<InterfaceIPv4Snapshot> snapshots, string id, int index, IPAddress address)
    {
        var byId = snapshots.Where(i => i.Id == id).ToArray();
        if (byId.Length != 1 || snapshots.Count(i => i.Index == index) != 1) return false;
        var selected = byId[0];
        return selected.Index == index && selected.Status == OperationalStatus.Up &&
               selected.Addresses.Count == 1 && selected.Addresses[0].Equals(address);
    }

    internal static bool ReadAndMatch(string id, int index, IPAddress address)
    {
        try
        {
            var snapshots = new List<InterfaceIPv4Snapshot>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var properties = nic.GetIPProperties();
                var ipv4 = properties.UnicastAddresses.Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
                if (ipv4.Length == 0) continue;
                var ipv4Properties = properties.GetIPv4Properties();
                if (ipv4Properties is null) return false;
                snapshots.Add(new(nic.Id, ipv4Properties.Index, nic.OperationalStatus, ipv4));
            }
            return Matches(snapshots, id, index, address);
        }
        catch (Exception e) when (e is NetworkInformationException or SocketException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { return false; }
    }
}
