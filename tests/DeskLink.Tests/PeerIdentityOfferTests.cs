using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PeerIdentityOfferTests
{
    [Fact]
    public async Task IdentityOfferUsesStableCertificateButFreshShortLivedChallengeWithoutPrivateKey()
    {
        using var folder = new TestFolder();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var first = await PeerIdentityOffer.CreateAsync(folder.Path, "Win-Two", "windows", now);
        var second = await PeerIdentityOffer.CreateAsync(folder.Path, "Win-Two", "windows", now);
        Assert.Equal(first.KeyboardFingerprint, second.KeyboardFingerprint);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.Equal(now.AddMinutes(10), first.ExpiresAtUtc);
        var json = first.ToJson(now);
        Assert.Equal(first, PeerIdentityOffer.Parse(json, now.AddMinutes(1)));
        Assert.DoesNotContain("PRIVATE KEY", json);
        Assert.Throws<ArgumentException>(() => PeerIdentityOffer.Parse(json, now.AddMinutes(11)));
    }

    [Fact]
    public async Task IdentityOfferRejectsUnknownPlatformsUnsafeNamesAndExpiredOrFutureData()
    {
        using var folder = new TestFolder();
        var now = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<ArgumentException>(() => PeerIdentityOffer.CreateAsync(folder.Path, "name\nbad", "windows", now));
        await Assert.ThrowsAsync<ArgumentException>(() => PeerIdentityOffer.CreateAsync(folder.Path, "Win-Two", "linux", now));
        var offer = await PeerIdentityOffer.CreateAsync(folder.Path, "Win-Two", "windows", now);
        Assert.Throws<ArgumentException>(() => (offer with { ExpiresAtUtc = now.AddDays(1) }).ToJson(now));
        Assert.Throws<ArgumentException>(() => (offer with { Nonce = "invalid" }).ToJson(now));
    }
}
