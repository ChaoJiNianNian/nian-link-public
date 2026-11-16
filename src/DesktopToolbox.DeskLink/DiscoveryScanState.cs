using System.Net;

namespace DesktopToolbox.DeskLink;

/// <summary>One short explicit scan, never an authorization or connection identity.</summary>
internal sealed class DiscoveryScanState
{
    // Leave a bounded margin for another device whose wall clock is behind ours.
    // This only shortens the query; scan and identity deadlines are unchanged.
    private static readonly TimeSpan QueryWireLifetime = TimeSpan.FromSeconds(25);
    private readonly object gate = new();
    private readonly DiscoverySubnet subnet;
    private readonly int expectedPort;
    private readonly int interfaceIndex;
    private readonly Func<DateTimeOffset> clock;
    private readonly DiscoveryCandidateCache cache;
    private readonly DateTimeOffset expires;
    private DateTimeOffset lastTime;
    private bool stopped;
    internal Guid Nonce { get; } = Guid.NewGuid();
    internal DateTimeOffset ExpiresAtUtc => expires;
    internal int ExpectedPort => expectedPort;

    internal DiscoveryScanState(DiscoverySubnet subnet, int interfaceIndex, TimeSpan duration, Func<DateTimeOffset>? clock = null)
        : this(subnet, interfaceIndex, duration, clock, DiscoveryUdpSocket.DiscoveryPort, false) { }

    internal static DiscoveryScanState ForLoopbackTest(IPAddress local, int interfaceIndex, TimeSpan duration, int expectedPort, Func<DateTimeOffset>? clock = null) =>
        new(DiscoverySubnet.LoopbackForTest(local), interfaceIndex, duration, clock, expectedPort, true);

    private DiscoveryScanState(DiscoverySubnet subnet, int interfaceIndex, TimeSpan duration, Func<DateTimeOffset>? clock, int expectedPort, bool loopbackTest)
    {
        ArgumentNullException.ThrowIfNull(subnet);
        if (interfaceIndex <= 0 || expectedPort is < 1 or > 65535 || duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30)) throw new ArgumentException("Invalid discovery scan.");
        this.subnet = subnet; this.expectedPort = expectedPort; this.interfaceIndex = interfaceIndex; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        lastTime = this.clock(); expires = lastTime.Add(duration); cache = loopbackTest ? DiscoveryCandidateCache.ForLoopbackTest(Nonce, this.clock) : new(Nonce, this.clock);
    }
    internal bool Observe(ReadOnlyMemory<byte> packet, IPEndPoint source, IPAddress destination, int incomingInterface)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(destination);
        source = new IPEndPoint(new IPAddress(source.Address.GetAddressBytes()), source.Port);
        destination = new IPAddress(destination.GetAddressBytes());
        lock (gate)
        {
            if (!IsCurrent()) return false;
            if (source.Port != expectedPort || !DiscoveryPacketPolicy.IsAllowed(source.Address, source.Port, destination, incomingInterface, subnet.LocalAddress, interfaceIndex, subnet)) return false;
            return cache.Observe(packet, source.Address);
        }
    }
    internal IReadOnlyList<DiscoveryCandidate> GetCandidates()
    {
        lock (gate) return IsCurrent() ? cache.GetCandidates() : [];
    }
    internal byte[] CreateQuery()
    {
        lock (gate)
        {
            if (!IsCurrent()) throw new InvalidOperationException("Discovery scan expired.");
            var queryExpires = expires - lastTime > QueryWireLifetime ? lastTime.Add(QueryWireLifetime) : expires;
            return DiscoveryQuery.Encode(Nonce, lastTime, queryExpires);
        }
    }
    internal TimeSpan Remaining()
    {
        lock (gate) return IsCurrent() ? expires - lastTime : TimeSpan.Zero;
    }
    internal void Stop() { lock (gate) StopLocked(); }
    private bool IsCurrent()
    {
        if (stopped) return false;
        var now = clock();
        if (now < lastTime || now >= expires) { StopLocked(); return false; }
        lastTime = now; return true;
    }
    private void StopLocked() { stopped = true; cache.Stop(); }
}
