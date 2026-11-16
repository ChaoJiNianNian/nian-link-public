using System.Net;
using System.Net.NetworkInformation;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class DiscoveryProtocolSessionTests
{
    private static readonly IPAddress Host = IPAddress.Parse("192.168.20.10"), Peer = IPAddress.Parse("192.168.20.11");
    private static DiscoveryNetworkBinding Binding(IPAddress address, Func<DiscoveryNetworkInterface[]> snapshot)
        => DiscoveryNetworkBinding.FromSnapshotsForTest(address, snapshot);
    private static DiscoveryNetworkInterface Interface(IPAddress address) =>
        new("synthetic-id", "test", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 12, address, IPAddress.Parse("255.255.255.0"));
    private static PeerIdentityOffer Offer(DateTimeOffset now) => new()
    {
        Name = "synthetic", Platform = "windows", KeyboardFingerprint = new string('A', 64),
        Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(1)
    };

    [Fact]
    public void CallerCannotChangeApprovedAddressThroughMutableIpAddressReferences()
    {
        var original = IPAddress.Parse("192.168.20.10");
        var approved = VerifiedLanBinding.FromSnapshots(original, true,
            [new LanInterfaceSnapshot("test", "test", NetworkInterfaceType.Ethernet, OperationalStatus.Up, [original], "synthetic-id")]);
#pragma warning disable CS0618
        original.Address = 0;
        var returned = approved.Address;
        returned.Address = 0;
#pragma warning restore CS0618
        Assert.Equal(Host, approved.Address);
    }

    [Fact]
    public void PublicSessionsRejectSyntheticBindings()
    {
        var binding = Binding(Host, () => [Interface(Host)]);
        Assert.Throws<InvalidOperationException>(() => DiscoveryScanSession.Create(binding, TimeSpan.FromSeconds(10)));
        Assert.Throws<InvalidOperationException>(() => DiscoveryResponderSession.Create(binding, Offer(DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void SyntheticProtocolRoundTripProducesOnlyUntrustedCandidateAndStops()
    {
        var now = DateTimeOffset.UtcNow;
        var host = Binding(Host, () => [Interface(Host)]);
        var peer = Binding(Peer, () => [Interface(Peer)]);
        using var scanner = DiscoveryScanSession.ForTest(host, TimeSpan.FromSeconds(10), () => now);
        using var responder = DiscoveryResponderSession.ForTest(peer, Offer(now), () => now);
        Assert.Equal(TimeSpan.FromSeconds(10), scanner.Remaining());
        Assert.Equal(TimeSpan.FromMinutes(1), responder.Remaining());
        var query = scanner.CreateQuery();
        var reply = responder.Reply(query, new IPEndPoint(Host, 53000), DiscoveryProtocol.MulticastGroup, 12);
        Assert.NotNull(reply);
        Assert.False(scanner.Observe(reply, new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 13));
        Assert.False(scanner.Observe(reply, new IPEndPoint(Peer, DiscoveryProtocol.Port), Peer, 12));
        var source = new IPEndPoint(IPAddress.Parse("192.168.20.11"), DiscoveryProtocol.Port);
        Assert.True(scanner.Observe(reply, source, Host, 12));
#pragma warning disable CS0618
        source.Address.Address = 0;
#pragma warning restore CS0618
        var candidate = Assert.Single(scanner.GetCandidates());
        Assert.Equal(Peer, candidate.Source);
#pragma warning disable CS0618
        candidate.Source.Address = 0;
#pragma warning restore CS0618
        Assert.Equal(Peer, Assert.Single(scanner.GetCandidates()).Source);
        scanner.Dispose();
        Assert.Empty(scanner.GetCandidates());
        Assert.Equal(TimeSpan.Zero, scanner.Remaining());
        Assert.False(scanner.Observe(reply, new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
    }

    [Fact]
    public void MappingChangeInvalidatesSessionsAndClearsCandidates()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new[] { Interface(Host) };
        var binding = Binding(Host, () => current);
        using var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => now);
        current = [Interface(Host) with { Index = 13 }];
        Assert.Throws<InvalidOperationException>(() => scan.CreateQuery());
        Assert.Empty(scan.GetCandidates());
        Assert.Equal(TimeSpan.Zero, scan.Remaining());
        current = [Interface(Host)];
        Assert.Empty(scan.GetCandidates());
    }

    [Fact]
    public void ExpiredSessionRejectsQueryAndClearsCandidates()
    {
        var now = DateTimeOffset.UtcNow;
        var binding = Binding(Host, () => [Interface(Host)]);
        using var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(1), () => now);
        now = now.AddSeconds(1);
        Assert.Throws<InvalidOperationException>(() => scan.CreateQuery());
        Assert.Empty(scan.GetCandidates());
    }

    [Fact]
    public void ScanSnapshotsSourceBeforeCallingClockOrValidatingPacket()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new IPEndPoint(IPAddress.Parse("192.168.20.11"), DiscoveryProtocol.Port);
        bool mutate = false;
        DateTimeOffset Clock()
        {
            if (mutate)
            {
#pragma warning disable CS0618
                source.Address.Address = 0;
#pragma warning restore CS0618
                mutate = false;
            }
            return now;
        }
        var host = Binding(Host, () => [Interface(Host)]);
        var peer = Binding(Peer, () => [Interface(Peer)]);
        using var scanner = DiscoveryScanSession.ForTest(host, TimeSpan.FromSeconds(10), Clock);
        using var responder = DiscoveryResponderSession.ForTest(peer, Offer(now), () => now);
        var reply = responder.Reply(scanner.CreateQuery(), new IPEndPoint(Host, 53000), DiscoveryProtocol.MulticastGroup, 12);
        Assert.NotNull(reply);
        mutate = true;
        Assert.True(scanner.Observe(reply, source, Host, 12));
        Assert.Equal(Peer, Assert.Single(scanner.GetCandidates()).Source);
    }
}
