using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DesktopToolbox.DeskLink;

internal sealed record DiscoveryDatagram(byte[] Data, IPEndPoint Source, IPAddress Destination, int InterfaceIndex);

/// <summary>Private, short-lived socket; caller enforces packet content and candidate policy.</summary>
internal sealed class DiscoveryUdpSocket : IDiscoveryDatagramTransport
{
    internal static readonly IPAddress Group = IPAddress.Parse("239.255.248.82");
    internal const int DiscoveryPort = 49473;
    private readonly Socket socket;
    private readonly IPAddress local;
    private readonly int interfaceIndex;
    private readonly bool responder;
    private readonly Func<bool> active;
    private int disposed;
#if ISOLATED_SOCKET_CONFIGURATION_TEST
    private readonly bool configurationOnly;
#endif
    internal int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;

    private DiscoveryUdpSocket(IPAddress local, int interfaceIndex, int port, bool responder, Func<bool> active
#if ISOLATED_SOCKET_CONFIGURATION_TEST
        , bool configurationOnly = false
#endif
        )
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || interfaceIndex <= 0 || port is < 0 or > 65535)
            throw new ArgumentException("Invalid discovery socket binding.");
        this.local = local; this.interfaceIndex = interfaceIndex; this.responder = responder; this.active = active;
#if ISOLATED_SOCKET_CONFIGURATION_TEST
        this.configurationOnly = configurationOnly;
#endif
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (!active()) throw new InvalidOperationException("Approved network is unavailable.");
            socket.ExclusiveAddressUse = true;
#if ISOLATED_SOCKET_CONFIGURATION_TEST
            if (configurationOnly) DiscoveryInterfacePin.Configure(socket, interfaceIndex);
#endif
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            socket.Bind(new IPEndPoint(local, port));
            if (responder)
            {
#if ISOLATED_SOCKET_CONFIGURATION_TEST
                if (!configurationOnly)
#endif
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, local));
            }
            else
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            }
            if (!active()) throw new InvalidOperationException("Approved network is unavailable.");
        }
        catch { socket.Dispose(); throw; }
    }

    // Real interfaces remain disabled until selected-interface unicast routing is proven on both platforms.
    internal static DiscoveryUdpSocket Open(VerifiedLanBinding binding, bool responder)
    {
        ArgumentNullException.ThrowIfNull(binding);
        throw new NotSupportedException("Discovery network interface binding has not been verified on this platform.");
    }

#if ISOLATED_SOCKET_CONFIGURATION_TEST
    // This entry point only exists in the isolated test assembly, cannot join a group or send/receive,
    // and its socket is never exposed. It validates configuration, not a working discovery transport.
    internal static DiscoveryUdpSocket OpenForWindowsConfigurationTest(DiscoveryNetworkBinding binding, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!OperatingSystem.IsWindows() || !enabled || !binding.IsLive || !binding.IsStillAvailable())
            throw new InvalidOperationException("A live Windows binding and explicit test opt-in are required.");
        return new(binding.Interface.Address, binding.Interface.Index, DiscoveryPort, true, binding.IsStillAvailable, configurationOnly: true);
    }
#endif

    internal static DiscoveryUdpSocket OpenForLoopbackTest(int interfaceIndex, int port, bool responder, Func<bool>? active = null, IPAddress? local = null)
    {
        local ??= IPAddress.Loopback;
        if (!IPAddress.IsLoopback(local)) throw new ArgumentException("Test socket must use a loopback address.");
        return new(local, interfaceIndex, port, responder, active ?? (() => true));
    }

    internal async Task SendQueryAsync(byte[] packet, int port, CancellationToken ct)
    {
#if ISOLATED_SOCKET_CONFIGURATION_TEST
        if (configurationOnly) throw new InvalidOperationException("Configuration-only socket cannot send or receive.");
#endif
        if (responder || packet is null || packet.Length is 0 or > DiscoveryQuery.MaximumBytes || port is < 1 or > 65535)
            throw new ArgumentException("Invalid discovery query.");
        EnsureActive();
        await socket.SendToAsync(packet.AsMemory(), SocketFlags.None, new IPEndPoint(Group, port), ct);
    }

    internal async Task SendReplyAsync(byte[] packet, IPEndPoint destination, CancellationToken ct)
    {
#if ISOLATED_SOCKET_CONFIGURATION_TEST
        if (configurationOnly) throw new InvalidOperationException("Configuration-only socket cannot send or receive.");
#endif
        if (!responder || packet is null || packet.Length is 0 or > DiscoveryAnnouncement.MaximumBytes || destination is null ||
            destination.AddressFamily != AddressFamily.InterNetwork || destination.Port is < 1 or > 65535 ||
            IPAddress.IsLoopback(local) && !IPAddress.IsLoopback(destination.Address))
            throw new ArgumentException("Invalid discovery response.");
        EnsureActive();
        await socket.SendToAsync(packet.AsMemory(), SocketFlags.None, destination, ct);
    }

    internal async Task<DiscoveryDatagram> ReceiveAsync(CancellationToken ct)
    {
#if ISOLATED_SOCKET_CONFIGURATION_TEST
        if (configurationOnly) throw new InvalidOperationException("Configuration-only socket cannot send or receive.");
#endif
        var buffer = new byte[DiscoveryAnnouncement.MaximumBytes + 1];
        while (true)
        {
            EnsureActive();
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tick.CancelAfter(TimeSpan.FromSeconds(1));
            SocketReceiveMessageFromResult received;
            try { received = await socket.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), tick.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && Volatile.Read(ref disposed) == 0) { continue; }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize) { continue; }
            EnsureActive();
            var expected = responder ? Group : local;
            if (received.ReceivedBytes is 0 or > DiscoveryAnnouncement.MaximumBytes || (received.SocketFlags & SocketFlags.Truncated) != 0 ||
                received.PacketInformation.Interface != interfaceIndex || !received.PacketInformation.Address.Equals(expected) ||
                received.RemoteEndPoint is not IPEndPoint source || source.AddressFamily != AddressFamily.InterNetwork) continue;
            return new(buffer.AsSpan(0, received.ReceivedBytes).ToArray(), source, received.PacketInformation.Address, received.PacketInformation.Interface);
        }
    }

    Task IDiscoveryDatagramTransport.SendQueryAsync(byte[] packet, int port, CancellationToken ct) => SendQueryAsync(packet, port, ct);
    Task IDiscoveryDatagramTransport.SendReplyAsync(byte[] packet, IPEndPoint destination, CancellationToken ct) => SendReplyAsync(packet, destination, ct);
    Task<DiscoveryDatagram> IDiscoveryDatagramTransport.ReceiveAsync(CancellationToken ct) => ReceiveAsync(ct);

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!active()) { Dispose(); throw new InvalidOperationException("Approved network is unavailable."); }
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) socket.Dispose(); }
}

