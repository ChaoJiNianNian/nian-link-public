using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace NianLink.MacResponderCandidate;

/// <summary>Private Mac candidate socket. Only its own received packets can be replied to.</summary>
internal sealed class MacResponderSocket : IResponderTransport
{
    private const int DarwinIpProtoIp = 0, DarwinIpBoundIf = 25;
    private readonly Socket socket;
    private readonly DiscoveryNetworkBinding binding;
    private readonly MacReplyLease pending = new();
    private int receiving, closed;
    private MacResponderSocket(Socket socket, DiscoveryNetworkBinding binding) { this.socket = socket; this.binding = binding; }

    internal static MacResponderSocket Open(DiscoveryNetworkBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!OperatingSystem.IsMacOS() || !binding.HasLiveNetworkOrigin || !binding.IsStillAvailable())
            throw new InvalidOperationException("Approved macOS discovery network is unavailable.");
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetRawSocketOption(DarwinIpProtoIp, DarwinIpBoundIf, BitConverter.GetBytes(binding.InterfaceIndex));
            var actual = new byte[sizeof(int)];
            if (socket.GetRawSocketOption(DarwinIpProtoIp, DarwinIpBoundIf, actual) != actual.Length ||
                BitConverter.ToInt32(actual) != binding.InterfaceIndex)
                throw new InvalidOperationException("Discovery interface binding could not be verified.");
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.Port));
            if (socket.LocalEndPoint is not IPEndPoint endpoint || !endpoint.Address.Equals(IPAddress.Any) || endpoint.Port != DiscoveryProtocol.Port)
                throw new InvalidOperationException("Discovery port binding could not be verified.");
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(DiscoveryProtocol.MulticastGroup, binding.Address));
            if (!binding.IsStillAvailable()) throw new InvalidOperationException("Approved discovery network changed.");
            return new(socket, binding);
        }
        catch { socket.Dispose(); throw; }
    }

    internal static bool Accept(int bytes, SocketFlags flags, IPEndPoint? source, IPAddress? destination, int incomingIndex, int expectedIndex)
        => Accept(bytes, flags, source, destination, incomingIndex, expectedIndex, null);

    internal static bool Accept(int bytes, SocketFlags flags, IPEndPoint? source, IPAddress? destination, int incomingIndex, int expectedIndex, IPAddress? local)
        => bytes is > 0 and <= DiscoveryAnnouncement.MaximumBytes &&
           (flags & (SocketFlags.Truncated | SocketFlags.ControlDataTruncated)) == 0 &&
           source?.Address.AddressFamily == AddressFamily.InterNetwork && source.Port > 0 &&
           incomingIndex == expectedIndex && (DiscoveryProtocol.MulticastGroup.Equals(destination) || local is not null && local.Equals(destination));

    public async Task<ResponderPacket> ReceiveAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref receiving, 1, 0) != 0) throw new InvalidOperationException("Concurrent receive is not supported.");
        try
        {
            var buffer = new byte[DiscoveryAnnouncement.MaximumBytes + 1];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                SocketReceiveMessageFromResult received;
                try { received = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct); }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize) { continue; }
                var source = received.RemoteEndPoint as IPEndPoint;
                if (!Accept(received.ReceivedBytes, received.SocketFlags, source, received.PacketInformation.Address,
                    received.PacketInformation.Interface, binding.InterfaceIndex, binding.Address)) continue;
                var sourceSnapshot = new IPEndPoint(new IPAddress(source!.Address.GetAddressBytes()), source.Port);
                var packet = new ResponderPacket(buffer.AsSpan(0, received.ReceivedBytes).ToArray(),
                    new IPEndPoint(new IPAddress(sourceSnapshot.Address.GetAddressBytes()), sourceSnapshot.Port),
                    new IPAddress(received.PacketInformation.Address.GetAddressBytes()), received.PacketInformation.Interface);
                if (DiscoveryProtocol.MulticastGroup.Equals(packet.Destination)) pending.Publish(packet, sourceSnapshot);
                return packet;
            }
        }
        finally { Volatile.Write(ref receiving, 0); }
    }

    public async Task SendReplyAsync(ResponderPacket packet, byte[] data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (closed != 0 || data.Length is 0 or > DiscoveryAnnouncement.MaximumBytes || !pending.TryTake(packet, out var destination) || destination is null)
            throw new InvalidOperationException("Discovery reply has no matching received datagram.");
        if (await socket.SendToAsync(data, SocketFlags.None, destination, ct) != data.Length)
            throw new IOException("Incomplete discovery reply.");
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0) { pending.Close(); socket.Dispose(); }
        return ValueTask.CompletedTask;
    }
}

internal sealed class MacReplyLease
{
    private readonly object gate = new();
    private ResponderPacket? token;
    private IPEndPoint? source;
    private bool closed;
    internal void Publish(ResponderPacket packet, IPEndPoint endpoint)
    {
        var frozen = new IPEndPoint(new IPAddress(endpoint.Address.GetAddressBytes()), endpoint.Port);
        lock (gate) { ObjectDisposedException.ThrowIf(closed, this); token = packet; source = frozen; }
    }
    internal bool TryTake(ResponderPacket packet, out IPEndPoint? endpoint)
    {
        lock (gate)
        {
            if (!ReferenceEquals(token, packet) || source is null) { endpoint = null; return false; }
            endpoint = source; token = null; source = null; return true;
        }
    }
    internal void Close() { lock (gate) { closed = true; token = null; source = null; } }
}
