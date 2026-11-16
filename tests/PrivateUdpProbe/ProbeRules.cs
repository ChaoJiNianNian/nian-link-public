using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DesktopToolbox.DeskLink;

namespace NianLink.PrivateUdpProbe;

internal sealed record ProbeInterface(Guid InterfaceId, int Index, IPAddress Address, IPAddress Mask, bool Physical, bool Up)
{
    public override string ToString() => "[isolated private interface]";
}

internal static class PrivateUdpProbeRules
{
    private static readonly byte[] Magic = [(byte)'N', (byte)'L', (byte)'P', (byte)'T'];
    internal const int PacketLength = 22;
    internal static ProbeInterface SelectUnique(IReadOnlyCollection<ProbeInterface> interfaces)
    {
        var eligible = new List<ProbeInterface>();
        foreach (var candidate in interfaces.Where(x => x.Physical && x.Up))
        {
            try { eligible.Add(Select(true, candidate.Address, interfaces)); }
            catch (InvalidOperationException) { }
        }
        return eligible.Count == 1 ? eligible[0] : throw new InvalidOperationException("Private interface selection is not unique.");
    }
    internal static ProbeInterface Select(bool enabled, IPAddress requested, IReadOnlyCollection<ProbeInterface> interfaces)
    {
        if (!enabled || requested.AddressFamily != AddressFamily.InterNetwork) throw new InvalidOperationException("Private probe requires explicit opt-in and IPv4.");
        var matches = interfaces.Where(value => value.Address.Equals(requested)).ToArray();
        if (matches.Length != 1 || !matches[0].Physical || !matches[0].Up || matches[0].Index <= 0 || matches[0].InterfaceId == Guid.Empty)
            throw new InvalidOperationException("Private interface mapping is unavailable or ambiguous.");
        try { _ = new DiscoverySubnet(matches[0].Address, matches[0].Mask); }
        catch (ArgumentException error) { throw new InvalidOperationException("Private interface subnet is invalid.", error); }
        return matches[0];
    }
    internal static bool IsCurrent(ProbeInterface expected, IReadOnlyCollection<ProbeInterface> interfaces)
    {
        try { return Select(true, expected.Address, interfaces) == expected; }
        catch (InvalidOperationException) { return false; }
    }
    internal static bool IsAllowedGroup(IPAddress group)
    {
        if (group.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = group.GetAddressBytes();
        return b[0] == 239 && b[1] == 255 && b[2] is > 0 and < 255 && b[3] is > 0 and < 255 && !(b[2] == 248 && b[3] == 82);
    }
    internal static byte[] BuildPacket(bool reply, byte[] nonce)
    {
        if (nonce.Length != 16 || nonce.All(x => x == 0)) throw new ArgumentException("Invalid synthetic nonce.");
        var data = new byte[PacketLength]; Magic.CopyTo(data, 0); data[4] = 1; data[5] = reply ? (byte)2 : (byte)1;
        nonce.CopyTo(data, 6); return data;
    }
    internal static bool TryQueryNonce(byte[] data, out byte[] nonce)
    {
        nonce = [];
        if (data.Length != PacketLength || !data.AsSpan(0, 4).SequenceEqual(Magic) || data[4] != 1 || data[5] != 1 || data.AsSpan(6, 16).IndexOfAnyExcept((byte)0) < 0) return false;
        nonce = data.AsSpan(6, 16).ToArray(); return true;
    }
    internal static bool Accept(byte[] data, bool reply, byte[] nonce, ProbeInterface selected, IPAddress group,
        IPAddress destination, int interfaceIndex, IPAddress source, int sourcePort, int expectedSourcePort, bool truncated)
    {
        if (truncated || !IsAllowedGroup(group) || data.Length != PacketLength || nonce.Length != 16 || nonce.All(x => x == 0) ||
            !data.AsSpan(0, 4).SequenceEqual(Magic) || data[4] != 1 || data[5] != (reply ? 2 : 1) ||
            !CryptographicOperations.FixedTimeEquals(data.AsSpan(6, 16), nonce) || interfaceIndex != selected.Index ||
            !source.Equals(selected.Address) || sourcePort is < 1 or > 65535 || reply && sourcePort != expectedSourcePort ||
            !destination.Equals(reply ? selected.Address : group)) return false;
        return true;
    }
}
