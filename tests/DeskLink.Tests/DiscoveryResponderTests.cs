using System.Net;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class DiscoveryResponderTests
{
    [Fact]
    public void RepliesOnlyToFreshQueriesOnApprovedInterfaceAndStopsOnExpiration()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddSeconds(20) };
        var group = IPAddress.Parse("239.255.248.82");
        var source = new IPEndPoint(IPAddress.Parse("192.168.2.3"), 52345);
        var state = new DiscoveryResponderState(offer, group, 7, new DiscoverySubnet(IPAddress.Parse("192.168.2.4"), IPAddress.Parse("255.255.255.0")), () => now);
        var scan = Guid.NewGuid();
        var query = DiscoveryQuery.Encode(scan, now, now.AddSeconds(10));
        Assert.Null(state.Reply(query, source, group, 8));
        Assert.Null(state.Reply(query, source, IPAddress.Parse("192.168.2.4"), 7));
        var response = state.Reply(query, source, group, 7);
        Assert.NotNull(response);
        Assert.True(DiscoveryAnnouncement.TryParse(response!, now, out var parsed));
        Assert.Equal(scan, parsed!.ScanNonce);
        Assert.Null(state.Reply(query, source, group, 7));
        now = now.AddSeconds(21);
        Assert.Null(state.Reply(query, source, group, 7));
    }

    [Fact]
    public void BoundedResponderRateLimitsAndRejectsAfterStopAndClockRollback()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(1) };
        var group = IPAddress.Parse("239.255.248.82");
        var source = new IPEndPoint(IPAddress.Parse("10.0.0.3"), 11111);
        var state = new DiscoveryResponderState(offer, group, 7, new DiscoverySubnet(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("255.255.255.0")), () => now);
        for (var i = 0; i < DiscoveryResponderState.MaximumPacketsPerSecond; i++)
            Assert.NotNull(state.Reply(DiscoveryQuery.Encode(Guid.NewGuid(), now, now.AddSeconds(10)), source, group, 7));
        var next = DiscoveryQuery.Encode(Guid.NewGuid(), now, now.AddSeconds(10));
        Assert.Null(state.Reply(next, source, group, 7));
        now = now.AddSeconds(1);
        Assert.NotNull(state.Reply(next, source, group, 7));
        now = now.AddSeconds(-2);
        Assert.Null(state.Reply(next, source, group, 7));
        now = now.AddSeconds(3);
        Assert.Null(state.Reply(DiscoveryQuery.Encode(Guid.NewGuid(), now, now.AddSeconds(10)), source, group, 7));
        state.Stop();
    }
}

public sealed class DiscoveryResponderReplayAndRateTests
{
    [Fact]
    public void ReusingNonceWithDifferentExpiryNeverReplaysWithinIdentitySession()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(1) };
        var group = IPAddress.Parse("239.255.248.82"); var source = new IPEndPoint(IPAddress.Parse("10.0.0.3"), 11111);
        var state = new DiscoveryResponderState(offer, group, 7, new DiscoverySubnet(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("255.255.255.0")), () => now); var nonce = Guid.NewGuid();
        Assert.NotNull(state.Reply(DiscoveryQuery.Encode(nonce, now, now.AddSeconds(1)), source, group, 7));
        now = now.AddSeconds(2);
        Assert.Null(state.Reply(DiscoveryQuery.Encode(nonce, now, now.AddSeconds(10)), source, group, 7));
    }

    [Fact]
    public void RateLimitRemainsBoundedAcrossWindowBoundary()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(1) };
        var group = IPAddress.Parse("239.255.248.82"); var source = new IPEndPoint(IPAddress.Parse("10.0.0.3"), 11111);
        var state = new DiscoveryResponderState(offer, group, 7, new DiscoverySubnet(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("255.255.255.0")), () => now);
        now = now.AddMilliseconds(900);
        for (var i = 0; i < DiscoveryResponderState.MaximumPacketsPerSecond; i++)
            Assert.NotNull(state.Reply(DiscoveryQuery.Encode(Guid.NewGuid(), now, now.AddSeconds(5)), source, group, 7));
        now = now.AddMilliseconds(200);
        Assert.Null(state.Reply(DiscoveryQuery.Encode(Guid.NewGuid(), now, now.AddSeconds(5)), source, group, 7));
        now = now.AddMilliseconds(800);
        Assert.NotNull(state.Reply(DiscoveryQuery.Encode(Guid.NewGuid(), now, now.AddSeconds(5)), source, group, 7));
    }
}
