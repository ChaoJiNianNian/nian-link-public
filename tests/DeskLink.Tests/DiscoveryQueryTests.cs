using System.Net;
using System.Text;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class DiscoveryQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    [Fact]
    public void QueryRequiresFreshRandomNonceExactFieldsAndStrictUtf8()
    {
        var nonce = Guid.NewGuid();
        var valid = DiscoveryQuery.Encode(nonce, Now, Now.AddSeconds(15));
        Assert.True(DiscoveryQuery.TryParse(valid, Now, out var query));
        Assert.Equal(nonce, query!.ScanNonce);
        Assert.False(DiscoveryQuery.TryParse(valid, Now.AddSeconds(15), out _));
        Assert.Throws<ArgumentException>(() => DiscoveryQuery.Encode(Guid.Empty, Now, Now.AddSeconds(15)));
        Assert.Throws<ArgumentException>(() => DiscoveryQuery.Encode(nonce, Now, Now.AddMinutes(1)));
        var json = Encoding.UTF8.GetString(valid);
        Assert.False(DiscoveryQuery.TryParse(Encoding.UTF8.GetBytes(json.Insert(1, "\"token\":\"secret\",")), Now, out _));
        Assert.False(DiscoveryQuery.TryParse(Encoding.UTF8.GetBytes(json.Insert(1, "\"version\":1,")), Now, out _));
        Assert.False(DiscoveryQuery.TryParse(Encoding.UTF8.GetBytes(json.Replace("\"version\":1", "\"version\":2")), Now, out _));
        Assert.False(DiscoveryQuery.TryParse(new byte[] { 0xff }, Now, out _));
        Assert.False(DiscoveryQuery.TryParse(new byte[DiscoveryQuery.MaximumBytes + 1], Now, out _));
    }

    [Fact]
    public void PacketMustArriveOnApprovedInterfaceAndExpectedDestinationFromPrivateSource()
    {
        var local = IPAddress.Parse("192.168.1.4");
        var remote = IPAddress.Parse("192.168.1.5");
        var group = IPAddress.Parse("239.255.248.82");
        var subnet = new DiscoverySubnet(local, IPAddress.Parse("255.255.255.0"));
        Assert.True(DiscoveryPacketPolicy.IsAllowed(remote, 1234, group, 7, group, 7, subnet));
        Assert.True(DiscoveryPacketPolicy.IsAllowed(remote, 1234, local, 7, local, 7, subnet));
        Assert.False(DiscoveryPacketPolicy.IsAllowed(remote, 0, group, 7, group, 7, subnet));
        Assert.False(DiscoveryPacketPolicy.IsAllowed(remote, 1234, local, 7, group, 7, subnet));
        Assert.False(DiscoveryPacketPolicy.IsAllowed(remote, 1234, group, 8, group, 7, subnet));
        Assert.False(DiscoveryPacketPolicy.IsAllowed(remote, 1234, group, 0, group, 7, subnet));
        Assert.False(DiscoveryPacketPolicy.IsAllowed(IPAddress.Parse("8.8.8.8"), 1234, group, 7, group, 7, subnet));
        Assert.False(DiscoveryPacketPolicy.IsAllowed(IPAddress.Loopback, 1234, group, 7, group, 7, subnet));
    }
}

public sealed class DiscoveryScanSessionStateTests
{
    [Fact]
    public void ExpiredStoppedOrWrongInterfaceNeverAddsOrReturnsCandidate()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var local = IPAddress.Parse("192.168.1.4"); var peer = IPAddress.Parse("192.168.1.5");
        var session = new DiscoveryScanState(new DiscoverySubnet(local, IPAddress.Parse("255.255.255.0")), 7, TimeSpan.FromSeconds(5), () => now);
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddSeconds(15) };
        var data = DiscoveryAnnouncement.Encode(offer, session.Nonce, now, now.AddSeconds(10));
        Assert.False(session.Observe(data, new IPEndPoint(peer, 49473), local, 8));
        Assert.False(session.Observe(data, new IPEndPoint(peer, 49474), local, 7));
        Assert.False(session.Observe(data, new IPEndPoint(peer, 49473), IPAddress.Parse("192.168.1.8"), 7));
        Assert.True(session.Observe(data, new IPEndPoint(peer, 49473), local, 7));
        Assert.Single(session.GetCandidates());
        now = now.AddSeconds(5);
        Assert.Empty(session.GetCandidates());
        Assert.False(session.Observe(data, new IPEndPoint(peer, 49473), local, 7));
        session.Stop(); Assert.Empty(session.GetCandidates());
    }

    [Fact]
    public void EachScanHasNewNonceAndLatePreviousPacketsAreRejected()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var local = IPAddress.Parse("10.0.0.1"); var peer = IPAddress.Parse("10.0.0.2");
        var first = new DiscoveryScanState(new DiscoverySubnet(local, IPAddress.Parse("255.255.255.0")), 7, TimeSpan.FromSeconds(5), () => now);
        var second = new DiscoveryScanState(new DiscoverySubnet(local, IPAddress.Parse("255.255.255.0")), 7, TimeSpan.FromSeconds(5), () => now);
        Assert.NotEqual(first.Nonce, second.Nonce);
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddSeconds(15) };
        var old = DiscoveryAnnouncement.Encode(offer, first.Nonce, now, now.AddSeconds(5));
        first.Stop();
        Assert.False(first.Observe(old, new IPEndPoint(peer, 49473), local, 7));
        Assert.False(second.Observe(old, new IPEndPoint(peer, 49473), local, 7));
    }
}

public sealed class DiscoveryClockSkewTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly IPAddress Host = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress Peer = IPAddress.Parse("10.0.0.2");
    private static readonly IPAddress Mask = IPAddress.Parse("255.255.255.0");

    [Theory]
    [InlineData(500)]
    [InlineData(4000)]
    [InlineData(5000)]
    [InlineData(-4000)]
    public void IndependentClocksCanExchangeFreshQueryAndReplyWithinBoundedSkew(int hostAheadMilliseconds)
    {
        // Independent clocks model the same instant, not changes to the operating system clock.
        var hostNow = BaseTime.AddMilliseconds(hostAheadMilliseconds);
        var peerNow = BaseTime;
        var scan = new DiscoveryScanState(new DiscoverySubnet(Host, Mask), 7, TimeSpan.FromSeconds(30), () => hostNow);
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = peerNow.AddMinutes(10) };
        var responder = new DiscoveryResponderState(offer, DiscoveryUdpSocket.Group, 9, new DiscoverySubnet(Peer, Mask), () => peerNow);

        var queryBytes = scan.CreateQuery();
        Assert.True(DiscoveryQuery.TryParse(queryBytes, peerNow, out var query));
        Assert.Equal(hostNow.AddSeconds(25), query!.ExpiresAtUtc);
        var reply = responder.Reply(queryBytes, new IPEndPoint(Host, 52000), DiscoveryUdpSocket.Group, 9);
        Assert.NotNull(reply);
        Assert.True(DiscoveryAnnouncement.TryParse(reply, hostNow, out var announcement));
        Assert.Equal(new[] { peerNow.AddSeconds(10), query.ExpiresAtUtc, offer.ExpiresAtUtc }.Min(), announcement!.ExpiresAtUtc);
        Assert.True(scan.Observe(reply, new IPEndPoint(Peer, DiscoveryUdpSocket.DiscoveryPort), Host, 7));
        Assert.Single(scan.GetCandidates());
        Assert.Null(responder.Reply(queryBytes, new IPEndPoint(Host, 52000), DiscoveryUdpSocket.Group, 9));
        Assert.False(scan.Observe(reply, new IPEndPoint(Peer, DiscoveryUdpSocket.DiscoveryPort), Host, 7));
        hostNow = announcement.ExpiresAtUtc;
        Assert.Empty(scan.GetCandidates());
        Assert.False(scan.Observe(reply, new IPEndPoint(Peer, DiscoveryUdpSocket.DiscoveryPort), Host, 7));
    }

    [Fact]
    public void ShortScanDeadlineIsNotExtendedAndNormalQueryRulesStayStrict()
    {
        var now = BaseTime;
        var shortScan = new DiscoveryScanState(new DiscoverySubnet(Host, Mask), 7, TimeSpan.FromSeconds(3), () => now);
        var bytes = shortScan.CreateQuery();
        Assert.True(DiscoveryQuery.TryParse(bytes, now, out var query));
        Assert.Equal(now.AddSeconds(3), query!.ExpiresAtUtc);
        Assert.False(DiscoveryQuery.TryParse(bytes, now.AddSeconds(3), out _));
        Assert.Throws<ArgumentException>(() => DiscoveryQuery.Encode(Guid.NewGuid(), now, now.AddSeconds(30).AddTicks(1)));
        now = now.AddSeconds(2).AddMilliseconds(999);
        Assert.True(DiscoveryQuery.TryParse(shortScan.CreateQuery(), now, out var nearDeadline));
        Assert.Equal(BaseTime.AddSeconds(3), nearDeadline!.ExpiresAtUtc);
        now = now.AddSeconds(-1);
        Assert.Throws<InvalidOperationException>(() => shortScan.CreateQuery());
    }

    [Fact]
    public void ClockSkewOutsideTheFiveSecondBudgetStillFailsClosed()
    {
        var peerNow = BaseTime;
        var hostNow = peerNow.AddSeconds(5).AddTicks(1);
        var scan = new DiscoveryScanState(new DiscoverySubnet(Host, Mask), 7, TimeSpan.FromSeconds(30), () => hostNow);
        Assert.False(DiscoveryQuery.TryParse(scan.CreateQuery(), peerNow, out _));
    }

    [Fact]
    public void DelayedQueryNeverExtendsOriginalScanDeadline()
    {
        var now = BaseTime;
        var scan = new DiscoveryScanState(new DiscoverySubnet(Host, Mask), 7, TimeSpan.FromSeconds(30), () => now);
        now = now.AddSeconds(6);
        Assert.True(DiscoveryQuery.TryParse(scan.CreateQuery(), now, out var query));
        Assert.Equal(BaseTime.AddSeconds(30), query!.ExpiresAtUtc);
        Assert.False(DiscoveryQuery.TryParse(scan.CreateQuery(), BaseTime.AddSeconds(30), out _));
    }

    [Fact]
    public void QueryAlreadyExpiredOnPeerIsNotAnswered()
    {
        var peerNow = BaseTime;
        var hostNow = peerNow.AddSeconds(-26);
        var scan = new DiscoveryScanState(new DiscoverySubnet(Host, Mask), 7, TimeSpan.FromSeconds(30), () => hostNow);
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = peerNow.AddMinutes(10) };
        var responder = new DiscoveryResponderState(offer, DiscoveryUdpSocket.Group, 9, new DiscoverySubnet(Peer, Mask), () => peerNow);
        var bytes = scan.CreateQuery();
        Assert.False(DiscoveryQuery.TryParse(bytes, peerNow, out _));
        Assert.Null(responder.Reply(bytes, new IPEndPoint(Host, 52000), DiscoveryUdpSocket.Group, 9));
    }
}
