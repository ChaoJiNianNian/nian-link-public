using System.Net;

namespace DesktopToolbox.DeskLink;

/// <summary>Discovery protocol constants. Their values are not an identity or authorization.</summary>
public static class DiscoveryProtocol
{
    public const int Port = DiscoveryUdpSocket.DiscoveryPort;
    public static IPAddress MulticastGroup => new(DiscoveryUdpSocket.Group.GetAddressBytes());
}

/// <summary>In-memory scan state; no socket, device authentication or authorization is performed.</summary>
public sealed class DiscoveryScanSession : IDisposable
{
    private readonly DiscoveryNetworkBinding binding;
    private readonly DiscoveryScanState state;
    private DiscoveryScanSession(DiscoveryNetworkBinding binding, TimeSpan duration, Func<DateTimeOffset>? clock)
    {
        this.binding = binding;
        var selected = binding.Interface;
        state = new(new DiscoverySubnet(selected.Address, selected.Mask), selected.Index, duration, clock);
    }
    public static DiscoveryScanSession Create(DiscoveryNetworkBinding binding, TimeSpan duration)
        => CreateCore(binding, duration, false, null);
    public bool IsBoundTo(DiscoveryNetworkBinding candidate) => ReferenceEquals(binding, candidate);
    internal static DiscoveryScanSession ForTest(DiscoveryNetworkBinding binding, TimeSpan duration, Func<DateTimeOffset> clock)
        => CreateCore(binding, duration, true, clock);
    private static DiscoveryScanSession CreateCore(DiscoveryNetworkBinding binding, TimeSpan duration, bool test, Func<DateTimeOffset>? clock)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if ((!test && !binding.IsLive) || !binding.IsStillAvailable()) throw new InvalidOperationException("Approved network is unavailable.");
        return new(binding, duration, clock);
    }
    public byte[] CreateQuery()
    {
        if (!Current()) throw new InvalidOperationException("Discovery network is unavailable.");
        return state.CreateQuery();
    }
    /// <summary>Checks caller-supplied packet metadata; it does not prove its origin or authenticate the candidate.</summary>
    public bool Observe(ReadOnlyMemory<byte> data, IPEndPoint source, IPAddress destination, int interfaceIndex)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(destination);
        var capturedSource = new IPEndPoint(new IPAddress(source.Address.GetAddressBytes()), source.Port);
        var capturedDestination = new IPAddress(destination.GetAddressBytes());
        return Current() && state.Observe(data, capturedSource, capturedDestination, interfaceIndex);
    }
    public IReadOnlyList<DiscoveryCandidate> GetCandidates() => Current() ? state.GetCandidates() : [];
    public TimeSpan Remaining() => Current() ? state.Remaining() : TimeSpan.Zero;
    private bool Current()
    {
        if (binding.IsStillAvailable()) return true;
        state.Stop(); return false;
    }
    public void Dispose() => state.Stop();
}

/// <summary>In-memory response policy; replies are public, unverified identity data.</summary>
public sealed class DiscoveryResponderSession : IDisposable
{
    private readonly DiscoveryNetworkBinding binding;
    private readonly DiscoveryResponderState state;
    private DiscoveryResponderSession(DiscoveryNetworkBinding binding, PeerIdentityOffer offer, Func<DateTimeOffset>? clock)
    {
        this.binding = binding;
        var selected = binding.Interface;
        state = new(offer, DiscoveryUdpSocket.Group, selected.Index, new DiscoverySubnet(selected.Address, selected.Mask), clock);
    }
    public static DiscoveryResponderSession Create(DiscoveryNetworkBinding binding, PeerIdentityOffer offer)
        => CreateCore(binding, offer, false, null);
    public bool IsBoundTo(DiscoveryNetworkBinding candidate) => ReferenceEquals(binding, candidate);
    internal static DiscoveryResponderSession ForTest(DiscoveryNetworkBinding binding, PeerIdentityOffer offer, Func<DateTimeOffset> clock)
        => CreateCore(binding, offer, true, clock);
    private static DiscoveryResponderSession CreateCore(DiscoveryNetworkBinding binding, PeerIdentityOffer offer, bool test, Func<DateTimeOffset>? clock)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(offer);
        if ((!test && !binding.IsLive) || !binding.IsStillAvailable()) throw new InvalidOperationException("Approved network is unavailable.");
        return new(binding, offer, clock);
    }
    /// <summary>Checks caller-supplied metadata; it does not establish that the packet arrived on an OS interface.</summary>
    public byte[]? Reply(ReadOnlyMemory<byte> data, IPEndPoint source, IPAddress destination, int interfaceIndex)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(destination);
        var capturedSource = new IPEndPoint(new IPAddress(source.Address.GetAddressBytes()), source.Port);
        var capturedDestination = new IPAddress(destination.GetAddressBytes());
        if (!binding.IsStillAvailable()) { state.Stop(); return null; }
        return state.Reply(data, capturedSource, capturedDestination, interfaceIndex);
    }
    public TimeSpan Remaining()
    {
        if (!binding.IsStillAvailable()) { state.Stop(); return TimeSpan.Zero; }
        return state.Remaining();
    }
    public void Dispose() => state.Stop();
}
