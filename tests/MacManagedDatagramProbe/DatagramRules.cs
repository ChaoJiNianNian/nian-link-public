using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MacManagedDatagramProbe;

internal static class DatagramRules
{
    internal static readonly IPAddress Group = IPAddress.Parse("239.255.82.197");
    private static readonly byte[] QueryTag = Encoding.ASCII.GetBytes("NIAN-SOCKET-PROBE-QUERY-v1:");
    private static readonly byte[] ReplyTag = Encoding.ASCII.GetBytes("NIAN-SOCKET-PROBE-REPLY-v1:");
    internal static byte[] Query(byte[] nonce) => Frame(QueryTag, nonce);
    internal static byte[] Reply(byte[] nonce) => Frame(ReplyTag, nonce);
    private static byte[] Frame(byte[] tag, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        if (nonce.Length != 16) throw new ArgumentException("16-byte nonce required.");
        return tag.Concat(nonce).ToArray();
    }

    internal static bool Valid(IPAddress local, IPAddress peer, int index, int port, int peerPort, byte[] nonce)
        => IsPrivate(local) && IsPrivate(peer) && !local.Equals(peer) && index > 0 &&
           port is >= 1024 and <= 65535 && peerPort is >= 1024 and <= 65535 && port != peerPort &&
           port != 49473 && peerPort != 49473 && nonce is { Length: 16 };

    private static bool IsPrivate(IPAddress? address)
    {
        if (address?.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    internal static bool Accept(ReadOnlySpan<byte> data, IPEndPoint source, IPAddress? destination,
        int receivedIndex, SocketFlags flags, IPEndPoint expectedSource, int expectedIndex, byte[] nonce)
        => (flags & (SocketFlags.Truncated | SocketFlags.ControlDataTruncated)) == 0 &&
           source.Equals(expectedSource) && Group.Equals(destination) && receivedIndex == expectedIndex &&
           data.SequenceEqual(Query(nonce));
}
