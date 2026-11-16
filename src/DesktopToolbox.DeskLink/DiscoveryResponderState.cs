using System.Net;

namespace DesktopToolbox.DeskLink;

/// <summary>One ephemeral public identity, rate-limited and scoped to one approved interface.</summary>
internal sealed class DiscoveryResponderState
{
    internal const int MaximumPacketsPerSecond = 8;
    private const int MaximumSeen = 128;
    private readonly object gate = new();
    private readonly PeerIdentityOffer offer;
    private readonly IPAddress group;
    private readonly DiscoverySubnet subnet;
    private readonly int interfaceIndex;
    private readonly Func<DateTimeOffset> clock;
    private readonly HashSet<Guid> seen = [];
    private readonly Queue<DateTimeOffset> received = [];
    private DateTimeOffset lastTime;
    private bool stopped;

    internal DiscoveryResponderState(PeerIdentityOffer offer, IPAddress group, int interfaceIndex, DiscoverySubnet subnet, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(offer); ArgumentNullException.ThrowIfNull(group); ArgumentNullException.ThrowIfNull(subnet);
        if (interfaceIndex <= 0) throw new ArgumentException("A valid interface is required.");
        this.offer = offer; this.group = group; this.subnet = subnet; this.interfaceIndex = interfaceIndex; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        lastTime = this.clock();
        offer.Validate(lastTime);
    }

    internal byte[]? Reply(ReadOnlyMemory<byte> packet, IPEndPoint source, IPAddress destination, int incomingInterface)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(destination);
        source = new IPEndPoint(new IPAddress(source.Address.GetAddressBytes()), source.Port);
        destination = new IPAddress(destination.GetAddressBytes());
        lock (gate)
        {
            var now = clock();
            if (stopped) return null;
            if (now < lastTime) { stopped = true; seen.Clear(); return null; }
            lastTime = now;
            if (offer.ExpiresAtUtc <= now) { stopped = true; seen.Clear(); return null; }
            while (received.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromSeconds(1)) received.Dequeue();
            if (received.Count >= MaximumPacketsPerSecond) return null;
            received.Enqueue(now);
            if (!DiscoveryPacketPolicy.IsAllowed(source.Address, source.Port, destination, incomingInterface, group, interfaceIndex, subnet) ||
                !DiscoveryQuery.TryParse(packet, now, out var query)) return null;
            if (seen.Count >= MaximumSeen || seen.Contains(query!.ScanNonce)) return null;
            var expires = new[] { now.AddSeconds(10), query.ExpiresAtUtc, offer.ExpiresAtUtc }.Min();
            var result = DiscoveryAnnouncement.Encode(offer, query.ScanNonce, now, expires);
            seen.Add(query.ScanNonce);
            return result;
        }
    }
    internal TimeSpan Remaining()
    {
        lock (gate)
        {
            var now = clock();
            if (stopped || now < lastTime || now >= offer.ExpiresAtUtc) { stopped = true; seen.Clear(); return TimeSpan.Zero; }
            lastTime = now;
            return offer.ExpiresAtUtc - now;
        }
    }
    internal void Stop() { lock (gate) { stopped = true; seen.Clear(); received.Clear(); } }
}
