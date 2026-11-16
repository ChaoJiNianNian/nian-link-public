using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace DesktopToolbox.DeskLink;

/// <summary>IPv4 subnet on the user-approved interface; not proof of peer identity.</summary>
internal sealed class DiscoverySubnet
{
    private readonly uint local, mask, hostMask, network;
    private readonly bool loopbackTest;
    internal IPAddress LocalAddress { get; }

    internal DiscoverySubnet(IPAddress local, IPAddress mask) : this(local, mask, false) { }
    private DiscoverySubnet(IPAddress address, IPAddress netmask, bool loopbackTest)
    {
        ArgumentNullException.ThrowIfNull(address); ArgumentNullException.ThrowIfNull(netmask);
        if (address.AddressFamily != AddressFamily.InterNetwork || netmask.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Discovery requires an IPv4 subnet.");
        var a = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        var m = BinaryPrimitives.ReadUInt32BigEndian(netmask.GetAddressBytes());
        var inverse = ~m;
        if (m == 0 || inverse < 3 || (inverse & (inverse + 1)) != 0 ||
            !(loopbackTest ? IPAddress.IsLoopback(address) : IsPrivate(address)))
            throw new ArgumentException("Discovery subnet is invalid.");
        var host = a & inverse;
        if (host == 0 || host == inverse) throw new ArgumentException("Local address is not a host address.");
        LocalAddress = address; local = a; mask = m; hostMask = inverse; network = a & m; this.loopbackTest = loopbackTest;
    }
    internal static DiscoverySubnet LoopbackForTest(IPAddress address) => new(address, IPAddress.Parse("255.0.0.0"), true);
    internal bool Allows(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork ||
            !(loopbackTest ? IPAddress.IsLoopback(address) : IsPrivate(address))) return false;
        var value = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        var host = value & hostMask;
        return value != local && (value & mask) == network && host != 0 && host != hostMask;
    }
    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
    }
}
