using System.Net;
using System.Text;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class DiscoveryAnnouncementTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Scan = Guid.Parse("c291643c-40f4-4ebd-aa11-8cf2fa268d08");
    private static PeerIdentityOffer Offer(string nonce, string name = "peer") => new()
    {
        Name = name, Platform = "windows", KeyboardFingerprint = new string('A', 64),
        Nonce = nonce, ExpiresAtUtc = Start.AddMinutes(10)
    };
    private static byte[] Announcement(string nonce, string name = "peer") =>
        DiscoveryAnnouncement.Encode(Offer(nonce, name), Scan, Start, Start.AddSeconds(20));

    [Fact]
    public void StrictPublicAnnouncementRoundTripsWithoutSecretAndExpires()
    {
        var bytes = Announcement(Guid.NewGuid().ToString("N"));
        Assert.DoesNotContain("token", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
        Assert.True(DiscoveryAnnouncement.TryParse(bytes, Start.AddSeconds(1), out var parsed));
        Assert.Equal("peer", parsed!.Offer.Name);
        Assert.False(DiscoveryAnnouncement.TryParse(bytes, Start.AddSeconds(20), out _));
        Assert.Equal(Scan, parsed.ScanNonce);
        Assert.Throws<ArgumentException>(() => DiscoveryAnnouncement.Encode(Offer(Guid.NewGuid().ToString("N")), Scan, Start, Start.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() => DiscoveryAnnouncement.Encode(Offer(Guid.NewGuid().ToString("N")), Guid.Empty, Start, Start.AddSeconds(20)));
    }

    [Fact]
    public void RejectsMalformedOversizedUnknownDuplicateAndSecretFields()
    {
        var json = Encoding.UTF8.GetString(Announcement(Guid.NewGuid().ToString("N")));
        var extra = json.Insert(1, "\"token\":\"secret\",");
        var duplicate = json.Insert(1, "\"version\":1,");
        foreach (var invalid in new[] { Encoding.UTF8.GetBytes(extra), Encoding.UTF8.GetBytes(duplicate),
                     Encoding.UTF8.GetBytes(json.Replace("\"version\":1", "\"version\":2")),
                     Encoding.UTF8.GetBytes(json.Replace("\"type\":\"offer\"", "\"type\":\"invitation\"")),
                     [0xff, 0xfe], new byte[DiscoveryAnnouncement.MaximumBytes + 1] })
            Assert.False(DiscoveryAnnouncement.TryParse(invalid, Start, out _));
    }

    [Fact]
    public void DistinctSessionsWithSameNameStaySeparateAndDuplicateCannotReplaceSourceOrIdentity()
    {
        var now = Start;
        var cache = new DiscoveryCandidateCache(Scan, () => now);
        const string nonce = "abcdef0123456789abcdef0123456789";
        var first = Announcement(nonce);
        Assert.True(cache.Observe(first, IPAddress.Parse("192.168.1.2")));
        var wrongScan = DiscoveryAnnouncement.Encode(Offer(Guid.NewGuid().ToString("N")), Guid.NewGuid(), Start, Start.AddSeconds(20));
        Assert.False(cache.Observe(wrongScan, IPAddress.Parse("192.168.1.2")));
        Assert.False(cache.Observe(first, IPAddress.Parse("192.168.1.3")));
        Assert.False(cache.Observe(Announcement(nonce, "impostor"), IPAddress.Parse("192.168.1.4")));
        Assert.False(cache.Observe(Announcement(nonce.ToUpperInvariant()), IPAddress.Parse("192.168.1.4")));
        Assert.True(cache.Observe(Announcement(Guid.NewGuid().ToString("N")), IPAddress.Parse("192.168.1.3")));
        var candidates = cache.GetCandidates();
        Assert.Equal(2, candidates.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.2"), candidates.Single(x => x.Offer.Nonce == nonce).Source);
        now = Start.AddSeconds(21);
        Assert.Empty(cache.GetCandidates());
        Assert.False(cache.Observe(Announcement(nonce), IPAddress.Parse("192.168.1.2")));
    }

    [Fact]
    public void ClearingVisibleResultsDoesNotAllowReplayWithinSameScan()
    {
        var cache = new DiscoveryCandidateCache(Scan, () => Start);
        var bytes = Announcement(Guid.NewGuid().ToString("N"));
        Assert.True(cache.Observe(bytes, IPAddress.Parse("10.1.1.2")));
        cache.Clear();
        Assert.False(cache.Observe(bytes, IPAddress.Parse("10.1.1.2")));
        Assert.Empty(cache.GetCandidates());
    }

    [Fact]
    public void CandidateCountAndInputRateAreBoundedAndClearHidesCandidates()
    {
        var now = Start;
        var cache = new DiscoveryCandidateCache(Scan, () => now);
        for (var i = 0; i < DiscoveryCandidateCache.MaximumCandidates; i++)
        {
            now = now.AddMilliseconds(100);
            var offer = Offer(Guid.NewGuid().ToString("N"));
            Assert.True(cache.Observe(DiscoveryAnnouncement.Encode(offer, Scan, now, now.AddSeconds(20)), IPAddress.Parse("10.1.1.2")));
        }
        Assert.False(cache.Observe(DiscoveryAnnouncement.Encode(Offer(Guid.NewGuid().ToString("N")), Scan, now, now.AddSeconds(20)), IPAddress.Parse("10.1.1.3")));
        Assert.Equal(DiscoveryCandidateCache.MaximumCandidates, cache.GetCandidates().Count);
        cache.Clear(); Assert.Empty(cache.GetCandidates());
        for (var i = 0; i < DiscoveryCandidateCache.MaximumPacketsPerSecond; i++)
            cache.Observe(new byte[] { 0xff }, IPAddress.Parse("10.1.1.2"));
        Assert.False(cache.Observe(DiscoveryAnnouncement.Encode(Offer(Guid.NewGuid().ToString("N")), Scan, now, now.AddSeconds(20)), IPAddress.Parse("10.1.1.2")));
        now = now.AddSeconds(1);
        Assert.True(cache.Observe(DiscoveryAnnouncement.Encode(Offer(Guid.NewGuid().ToString("N")), Scan, now, now.AddSeconds(20)), IPAddress.Parse("10.1.1.2")));
    }

    [Fact]
    public async Task StoppingScanClearsResultsAndRejectsAllLatePackets()
    {
        var cache = new DiscoveryCandidateCache(Scan, () => Start);
        Assert.True(cache.Observe(Announcement(Guid.NewGuid().ToString("N")), IPAddress.Parse("10.1.1.2")));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latePackets = Enumerable.Range(0, 16).Select(async _ =>
        {
            await gate.Task;
            return cache.Observe(Announcement(Guid.NewGuid().ToString("N")), IPAddress.Parse("10.1.1.2"));
        }).ToArray();
        cache.Stop();
        cache.Stop();
        gate.SetResult();
        Assert.All(await Task.WhenAll(latePackets), accepted => Assert.False(accepted));
        Assert.Empty(cache.GetCandidates());
        cache.Clear();
        Assert.False(cache.Observe(Announcement(Guid.NewGuid().ToString("N")), IPAddress.Parse("10.1.1.2")));
    }

    [Fact]
    public void RateLimitAppliesAcrossOneSecondWindowBoundaries()
    {
        var now = Start;
        var cache = new DiscoveryCandidateCache(Scan, () => now);
        now = now.AddMilliseconds(999);
        for (var i = 0; i < DiscoveryCandidateCache.MaximumPacketsPerSecond; i++)
            Assert.False(cache.Observe(new byte[] { 0xff }, IPAddress.Parse("10.1.1.2")));
        now = now.AddMilliseconds(1);
        var nonce = Guid.NewGuid().ToString("N");
        var message = DiscoveryAnnouncement.Encode(Offer(nonce), Scan, now, now.AddSeconds(20));
        Assert.False(cache.Observe(message, IPAddress.Parse("10.1.1.2")));
        now = now.AddMilliseconds(999);
        Assert.True(cache.Observe(message, IPAddress.Parse("10.1.1.2")));
    }

    [Fact]
    public void RejectsPublicOrLoopbackSourceAndClockRollbackInvalidatesCandidates()
    {
        var now = Start;
        var cache = new DiscoveryCandidateCache(Scan, () => now);
        var message = Announcement(Guid.NewGuid().ToString("N"));
        Assert.False(cache.Observe(message, IPAddress.Loopback));
        Assert.False(cache.Observe(message, IPAddress.Parse("8.8.8.8")));
        Assert.True(cache.Observe(message, IPAddress.Parse("192.168.1.2")));
        now = now.AddSeconds(-1);
        Assert.Empty(cache.GetCandidates());
    }
}

public sealed class DiscoveryLoopbackCacheTests
{
    [Fact]
    public void LoopbackCacheIsExplicitAndDoesNotBroadenDefaultPrivateNetworkCache()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero); var scan = Guid.NewGuid();
        var offer = new PeerIdentityOffer { Name = "test", Platform = "windows", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddSeconds(20) };
        var bytes = DiscoveryAnnouncement.Encode(offer, scan, now, now.AddSeconds(10));
        var normal = new DiscoveryCandidateCache(scan, () => now);
        var local = DiscoveryCandidateCache.ForLoopbackTest(scan, () => now);
        Assert.False(normal.Observe(bytes, IPAddress.Loopback));
        Assert.False(local.Observe(bytes, IPAddress.Parse("10.0.0.2")));
        Assert.True(local.Observe(bytes, IPAddress.Loopback));
        local.Stop(); Assert.Empty(local.GetCandidates());
    }
}
