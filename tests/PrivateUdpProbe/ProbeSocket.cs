using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace NianLink.PrivateUdpProbe;

internal sealed class ProbeSocket : IDisposable
{
    private readonly Socket socket;
    private readonly ProbeInterface selected;
    private readonly IPAddress group;
    private readonly bool responder;
    internal int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;

    private ProbeSocket(Socket socket, ProbeInterface selected, IPAddress group, bool responder)
    { this.socket = socket; this.selected = selected; this.group = group; this.responder = responder; }

    internal static ProbeSocket Open(ProbeInterface selected, IPAddress group, bool responder)
    {
        if (!PrivateUdpProbeRules.IsAllowedGroup(group)) throw new InvalidOperationException("Invalid isolated multicast group.");
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ExclusiveAddressUse = true;
            DiscoveryInterfacePin.Configure(socket, selected.Index);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, selected.Address.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            var outgoing = new byte[4]; socket.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, outgoing);
            if (!outgoing.AsSpan().SequenceEqual(selected.Address.GetAddressBytes()) ||
                Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive)) != 1)
                throw new InvalidOperationException("Multicast interface settings could not be verified.");
            socket.Bind(new IPEndPoint(selected.Address, 0));
            var endpoint = socket.LocalEndPoint as IPEndPoint;
            if (endpoint is null || !endpoint.Address.Equals(selected.Address) || endpoint.Port is < 49152 or > 65535)
                throw new InvalidOperationException("Private test socket binding is invalid.");
            if (responder) socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, selected.Address));
            return new(socket, selected, group, responder);
        }
        catch { socket.Dispose(); throw; }
    }

    internal async Task SendQueryAsync(byte[] nonce, int port, CancellationToken ct)
    {
        if (responder || port is < 49152 or > 65535) throw new InvalidOperationException("Invalid isolated query target.");
        await EnsureCurrentAsync(ct);
        var data = PrivateUdpProbeRules.BuildPacket(false, nonce);
        if (await socket.SendToAsync(data, SocketFlags.None, new IPEndPoint(group, port), ct) != data.Length)
            throw new IOException("Incomplete private test query.");
    }

    internal async Task ReceiveAndReplyAsync(int expectedPort, CancellationToken ct)
    {
        if (!responder) throw new InvalidOperationException("Probe socket is not a responder.");
        var buffer = new byte[PrivateUdpProbeRules.PacketLength + 1];
        var received = await socket.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
        var data = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
        if (received.RemoteEndPoint is not IPEndPoint source || source.Port != expectedPort || !PrivateUdpProbeRules.TryQueryNonce(data, out var nonce) ||
            !PrivateUdpProbeRules.Accept(data, false, nonce, selected, group, received.PacketInformation.Address, received.PacketInformation.Interface,
                source.Address, source.Port, 0, (received.SocketFlags & SocketFlags.Truncated) != 0))
            throw new InvalidDataException("Private test query was rejected.");
        var reply = PrivateUdpProbeRules.BuildPacket(true, nonce);
        await EnsureCurrentAsync(ct);
        if (await socket.SendToAsync(reply, SocketFlags.None, source, ct) != reply.Length)
            throw new IOException("Incomplete private test response.");
    }

    internal async Task ReceiveReplyAsync(byte[] nonce, int responderPort, CancellationToken ct)
    {
        if (responder) throw new InvalidOperationException("Probe socket is not a requester.");
        var buffer = new byte[PrivateUdpProbeRules.PacketLength + 1];
        var received = await socket.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
        var data = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
        if (received.RemoteEndPoint is not IPEndPoint source || !PrivateUdpProbeRules.Accept(data, true, nonce, selected, group,
            received.PacketInformation.Address, received.PacketInformation.Interface, source.Address, source.Port, responderPort,
            (received.SocketFlags & SocketFlags.Truncated) != 0)) throw new InvalidDataException("Private test response was rejected.");
        await EnsureCurrentAsync(ct);
    }
    private async Task EnsureCurrentAsync(CancellationToken ct)
    {
        if (!PrivateUdpProbeRules.IsCurrent(selected, await ProbeEnvironment.ReadAsync(ct)))
            throw new InvalidOperationException("Private test interface changed.");
    }
    public void Dispose() => socket.Dispose();
}
