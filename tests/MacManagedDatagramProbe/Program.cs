using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MacManagedDatagramProbe;

internal static class Program
{
    // These are Darwin's IPPROTO_IP (0) and IP_BOUND_IF (25), not portable socket option numbers.
    private const int DarwinIpProtoIp = 0;
    private const int DarwinIpBoundIf = 25;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { Print("not_started"); return 2; }
        if (args.Length != 7 || args[0] != "--run" ||
            !IPAddress.TryParse(args[1], out var local) || !IPAddress.TryParse(args[2], out var peer) ||
            !int.TryParse(args[3], out var index) || !int.TryParse(args[4], out var port) ||
            !int.TryParse(args[5], out var peerPort) || args[6].Length != 32 ||
            !args[6].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        { Print("invalid"); return 2; }
        var nonce = Convert.FromHexString(args[6]);
        if (!DatagramRules.Valid(local, peer, index, port, peerPort, nonce) || !OperatingSystem.IsMacOS())
        { Print("unavailable"); return 1; }
        try
        {
            await RunAsync(local, peer, index, port, peerPort, nonce);
            Print("ok"); return 0;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or InvalidOperationException or IOException)
        { Print("failed"); return 1; }
    }

    private static async Task RunAsync(IPAddress local, IPAddress peer, int index, int port, int peerPort, byte[] nonce)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetRawSocketOption(DarwinIpProtoIp, DarwinIpBoundIf, BitConverter.GetBytes(index));
        var actual = new byte[sizeof(int)];
        if (socket.GetRawSocketOption(DarwinIpProtoIp, DarwinIpBoundIf, actual) != actual.Length || BitConverter.ToInt32(actual) != index)
            throw new InvalidOperationException("Interface binding not verified.");
        // Enable before bind so every queued packet has packet information (see Microsoft docs).
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(DatagramRules.Group, local));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Print("ready");
        var expected = new IPEndPoint(peer, peerPort);
        var buffer = new byte[128];
        while (true)
        {
            var received = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            if (received.RemoteEndPoint is not IPEndPoint source ||
                !DatagramRules.Accept(buffer.AsSpan(0, received.ReceivedBytes), source, received.PacketInformation.Address,
                    received.PacketInformation.Interface, received.SocketFlags, expected, index, nonce)) continue;
            var reply = DatagramRules.Reply(nonce);
            if (await socket.SendToAsync(reply, SocketFlags.None, expected, timeout.Token) != reply.Length)
                throw new IOException("Incomplete datagram send.");
            return;
        }
    }
    private static void Print(string status) { Console.WriteLine(JsonSerializer.Serialize(new { status })); Console.Out.Flush(); }
}
