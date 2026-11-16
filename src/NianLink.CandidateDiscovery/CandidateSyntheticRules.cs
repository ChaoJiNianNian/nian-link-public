#if ISOLATED_CANDIDATE_SYNTHETIC_TEST
using System.Net;
using System.Security.Cryptography;

namespace NianLink.CandidateDiscovery;

/// <summary>Fixed non-identity frames for a same-host test; absent from candidate builds.</summary>
internal static class CandidateSyntheticRules
{
    private static readonly byte[] Magic = [(byte)'N', (byte)'L', (byte)'P', (byte)'T'];
    internal const int Length = 22;
    internal static byte[] Packet(bool reply, byte[] nonce)
    {
        if (nonce.Length != 16 || nonce.All(value => value == 0)) throw new ArgumentException("Invalid synthetic nonce.");
        var packet = new byte[Length]; Magic.CopyTo(packet, 0); packet[4] = 1; packet[5] = reply ? (byte)2 : (byte)1;
        nonce.CopyTo(packet, 6); return packet;
    }
    internal static bool Accept(byte[] packet, bool reply, byte[] nonce, IPAddress source, int sourcePort,
        int expectedPort, IPAddress local, int incomingIndex, int expectedIndex, bool truncated)
        => !truncated && packet.Length == Length && nonce.Length == 16 && nonce.Any(value => value != 0) &&
           packet.AsSpan(0, 4).SequenceEqual(Magic) && packet[4] == 1 && packet[5] == (reply ? 2 : 1) &&
           CryptographicOperations.FixedTimeEquals(packet.AsSpan(6, 16), nonce) && source.Equals(local) &&
           expectedPort is >= 1 and <= 65535 && sourcePort == expectedPort && incomingIndex == expectedIndex && expectedIndex > 0;
}
#endif
