using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace NianLink.CandidateDiscovery;

/// <summary>Private socket; it only emits discovery protocol packets selected by the owning session.</summary>
internal sealed class WindowsDiscoveryDatagramSocket : ICandidateDiscoveryTransport
{
    private readonly Socket socket;
    private readonly DiscoveryNetworkBinding binding;
    private readonly bool responder;
    private readonly CandidateQueryLease pending = new();
    private int receiving;
    private int disposed;
#if ISOLATED_CANDIDATE_SYNTHETIC_TEST
    private bool syntheticTest;
    private int syntheticSend, syntheticReply, syntheticReceive;
    internal int SyntheticPort => ((IPEndPoint)socket.LocalEndPoint!).Port;
#endif
    private WindowsDiscoveryDatagramSocket(Socket socket, DiscoveryNetworkBinding binding, bool responder)
    { this.socket = socket; this.binding = binding; this.responder = responder; }

    internal static WindowsDiscoveryDatagramSocket Open(DiscoveryNetworkBinding binding, bool responder)
    {
        if (!binding.HasLiveNetworkOrigin || !binding.IsHardwareAvailable())
            throw new InvalidOperationException("Approved hardware interface is unavailable.");
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ExclusiveAddressUse = true;
            DiscoveryInterfacePin.Configure(socket, binding.InterfaceIndex);
            socket.Bind(new IPEndPoint(binding.Address, responder ? DiscoveryProtocol.Port : 0));
            if (socket.LocalEndPoint is not IPEndPoint bound || !bound.Address.Equals(binding.Address) ||
                bound.Port is < 1 or > 65535 || responder && bound.Port != DiscoveryProtocol.Port)
                throw new InvalidOperationException("Discovery socket binding could not be verified.");
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            if (responder)
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(DiscoveryProtocol.MulticastGroup, binding.Address));
            else
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, binding.Address.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                var selected = new byte[4]; socket.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, selected);
                if (!selected.AsSpan().SequenceEqual(binding.Address.GetAddressBytes()) ||
                    Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive)) != 1)
                    throw new InvalidOperationException("Multicast options could not be verified.");
            }
            if (!binding.IsHardwareAvailable()) throw new InvalidOperationException("Approved hardware interface is unavailable.");
            return new(socket, binding, responder);
        }
        catch (Exception error)
        {
            var cleanup = DiscoveryStartupCleanup.TryDispose(socket);
            if (cleanup is not null) throw new DiscoveryStartupCleanupException(error, cleanup);
            throw;
        }
    }

    public Task SendQueryAsync(DiscoveryScanSession scan, CancellationToken ct) =>
        SendQueryGuardedAsync(scan, ct, send => send());

    public async Task SendQueryGuardedAsync(DiscoveryScanSession scan, CancellationToken ct, Func<Func<Task>, Task> start)
    {
#if ISOLATED_CANDIDATE_SYNTHETIC_TEST
        if (syntheticTest) throw new InvalidOperationException("Synthetic socket cannot send product packets.");
#endif
        RequireBoundScan(scan, binding);
        if (responder || !binding.IsHardwareAvailable()) throw new InvalidOperationException("Discovery query rejected.");
        var data = scan.CreateQuery();
        if (data.Length is 0 or > 512) throw new InvalidOperationException("Discovery query rejected.");
        // No network validation or payload generation is performed inside the
        // caller's close gate: only the OS send is initiated atomically with stop.
        await start(async () =>
        {
            if (await socket.SendToAsync(data, SocketFlags.None, new IPEndPoint(DiscoveryProtocol.MulticastGroup, DiscoveryProtocol.Port), ct) != data.Length)
                throw new IOException("Incomplete discovery query.");
        }).ConfigureAwait(false);
    }
    public async Task ReplyToReceivedQueryAsync(DiscoveryResponderSession session, CandidateDiscoveryDatagram packet, CancellationToken ct)
    {
#if ISOLATED_CANDIDATE_SYNTHETIC_TEST
        if (syntheticTest) throw new InvalidOperationException("Synthetic socket cannot send product packets.");
#endif
        RequireBoundResponder(session, binding); ArgumentNullException.ThrowIfNull(packet);
        if (!responder || !pending.TryTake(packet, out var original) || original is null)
            throw new InvalidOperationException("Discovery reply rejected.");
        var data = session.Reply(original.Data, original.Source, original.Destination, original.InterfaceIndex);
        if (data is null) return;
        if (data.Length is 0 or > DiscoveryAnnouncement.MaximumBytes || !binding.IsHardwareAvailable())
            throw new InvalidOperationException("Discovery reply rejected.");
        if (await socket.SendToAsync(data, SocketFlags.None, original.Source, ct) != data.Length) throw new IOException("Incomplete discovery reply.");
    }
#if ISOLATED_CANDIDATE_SYNTHETIC_TEST
    internal static WindowsDiscoveryDatagramSocket OpenForSyntheticTest(DiscoveryNetworkBinding binding, bool responder)
    {
        var probe = Open(binding, responder);
        try
        {
            probe.syntheticTest = true;
            if (!responder)
            {
                probe.socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 0);
                if (Convert.ToInt32(probe.socket.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive)) != 0)
                    throw new InvalidOperationException("Synthetic multicast scope was not restricted to this host.");
            }
            return probe;
        }
        catch { probe.Dispose(); throw; }
    }
    internal async Task SendSyntheticQueryAsync(byte[] nonce, CancellationToken ct)
    {
        if (!syntheticTest || responder || Interlocked.Exchange(ref syntheticSend, 1) != 0 ||
            Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive)) != 0 ||
            !binding.IsHardwareAvailable()) throw new InvalidOperationException("Synthetic query rejected.");
        var data = CandidateSyntheticRules.Packet(false, nonce);
        if (await socket.SendToAsync(data, SocketFlags.None, new IPEndPoint(DiscoveryProtocol.MulticastGroup, DiscoveryProtocol.Port), ct) != data.Length)
            throw new IOException("Incomplete synthetic query.");
    }
    internal async Task ReplySyntheticAsync(CandidateDiscoveryDatagram packet, byte[] nonce, int expectedPort, CancellationToken ct)
    {
        if (!syntheticTest || !responder || Interlocked.Exchange(ref syntheticReply, 1) != 0 ||
            !pending.TryTake(packet, out var original) || original is null ||
            !CandidateSyntheticRules.Accept(original.Data, false, nonce, original.Source.Address, original.Source.Port,
                expectedPort, binding.Address, original.InterfaceIndex, binding.InterfaceIndex, false) ||
            !original.Destination.Equals(DiscoveryProtocol.MulticastGroup) || !binding.IsHardwareAvailable())
            throw new InvalidOperationException("Synthetic reply rejected.");
        var data = CandidateSyntheticRules.Packet(true, nonce);
        if (await socket.SendToAsync(data, SocketFlags.None, new IPEndPoint(binding.Address, expectedPort), ct) != data.Length)
            throw new IOException("Incomplete synthetic response.");
    }
    internal async Task ReceiveSyntheticReplyAsync(byte[] nonce, CancellationToken ct)
    {
        if (!syntheticTest || responder || Interlocked.Exchange(ref syntheticReceive, 1) != 0)
            throw new InvalidOperationException("Synthetic receive rejected.");
        var packet = await ReceiveAsync(ct);
        if (!CandidateSyntheticRules.Accept(packet.Data, true, nonce, packet.Source.Address, packet.Source.Port,
                DiscoveryProtocol.Port, binding.Address, packet.InterfaceIndex, binding.InterfaceIndex, false) ||
            !packet.Destination.Equals(binding.Address) || !binding.IsHardwareAvailable())
            throw new InvalidOperationException("Synthetic response rejected.");
    }
#endif
    internal static void RequireBoundScan(DiscoveryScanSession scan, DiscoveryNetworkBinding binding)
    {
        ArgumentNullException.ThrowIfNull(scan); ArgumentNullException.ThrowIfNull(binding);
        if (!scan.IsBoundTo(binding)) throw new InvalidOperationException("Discovery scan belongs to another network binding.");
    }
    internal static void RequireBoundResponder(DiscoveryResponderSession session, DiscoveryNetworkBinding binding)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(binding);
        if (!session.IsBoundTo(binding)) throw new InvalidOperationException("Discovery responder belongs to another network binding.");
    }
    public async Task<CandidateDiscoveryDatagram> ReceiveAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref receiving, 1, 0) != 0) throw new InvalidOperationException("Concurrent discovery receive is not allowed.");
        try
        {
            var buffer = new byte[DiscoveryAnnouncement.MaximumBytes + 1];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                SocketReceiveMessageFromResult result;
                try { result = await socket.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.MessageSize) { continue; }
                if (result.RemoteEndPoint is not IPEndPoint source || result.ReceivedBytes is 0 or > DiscoveryAnnouncement.MaximumBytes ||
                    (result.SocketFlags & (SocketFlags.Truncated | SocketFlags.ControlDataTruncated)) != 0 || result.PacketInformation.Interface != binding.InterfaceIndex ||
                    !(binding.Address.Equals(result.PacketInformation.Address) || responder && DiscoveryProtocol.MulticastGroup.Equals(result.PacketInformation.Address))) continue;
                var packet = new CandidateDiscoveryDatagram(buffer.AsSpan(0, result.ReceivedBytes).ToArray(), source, result.PacketInformation.Address, result.PacketInformation.Interface);
                if (responder && DiscoveryProtocol.MulticastGroup.Equals(packet.Destination)) pending.Publish(packet);
                return packet;
            }
        }
        finally { Volatile.Write(ref receiving, 0); }
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) { pending.Close(); socket.Dispose(); } }
}

/// <summary>Atomically associates a socket-received packet with one private copy.</summary>
internal sealed class CandidateQueryLease
{
    private readonly object gate = new();
    private CandidateDiscoveryDatagram? token, copy;
    private bool closed;
    internal void Publish(CandidateDiscoveryDatagram packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var snapshot = new CandidateDiscoveryDatagram(packet.Data.ToArray(),
            new IPEndPoint(new IPAddress(packet.Source.Address.GetAddressBytes()), packet.Source.Port),
            new IPAddress(packet.Destination.GetAddressBytes()), packet.InterfaceIndex);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            token = packet; copy = snapshot;
        }
    }
    internal bool TryTake(CandidateDiscoveryDatagram packet, out CandidateDiscoveryDatagram? snapshot)
    {
        lock (gate)
        {
            if (!ReferenceEquals(token, packet) || copy is null) { snapshot = null; return false; }
            snapshot = copy; token = null; copy = null; return true;
        }
    }
    internal void Close() { lock (gate) { closed = true; token = null; copy = null; } }
}
