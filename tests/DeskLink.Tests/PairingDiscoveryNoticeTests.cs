using System.Text;
using System.Text.Json.Nodes;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingDiscoveryNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static PairingDiscoveryNotice Notice() => new("host", "peer", new string('A', 64),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 52345, Now.AddSeconds(25), Now.AddMinutes(2));

    [Fact]
    public void RoundTripPreservesTheTargetSessionAndContainsNoRouteOrSecret()
    {
        var notice = Notice() with { HostFingerprint = new string('a', 64) };
        var bytes = notice.Encode(Now);
        Assert.True(PairingDiscoveryNotice.TryParse(bytes, Now, out var parsed));
        Assert.Equal(notice with { HostFingerprint = new string('A', 64) }, parsed);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("address", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(PairingDiscoveryNotice.TryParse(bytes, notice.ExpiresAtUtc, out _));
    }

    [Theory]
    [InlineData("hostName", "bad name")]
    [InlineData("peerName", "HOST")]
    [InlineData("hostFingerprint", "bad")]
    [InlineData("offerNonce", "00000000-0000-0000-0000-000000000000")]
    [InlineData("sessionId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("transactionId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("type", "offer")]
    public void RejectsInvalidIdentityOrMessageType(string field, string value)
    {
        var json = JsonNode.Parse(Notice().Encode(Now))!.AsObject();
        json[field] = value;
        Assert.False(PairingDiscoveryNotice.TryParse(Encoding.UTF8.GetBytes(json.ToJsonString()), Now, out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(1023)] [InlineData(65536)]
    public void RejectsInvalidPorts(int port)
    {
        var notice = Notice() with { Port = port };
        Assert.Throws<ArgumentException>(() => notice.Encode(Now));
        var json = JsonNode.Parse(Notice().Encode(Now))!.AsObject(); json["port"] = port;
        Assert.False(PairingDiscoveryNotice.TryParse(Encoding.UTF8.GetBytes(json.ToJsonString()), Now, out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(31)]
    public void RejectsExpiredOrLongLivedNotices(int seconds)
    {
        var notice = Notice() with { ExpiresAtUtc = Now.AddSeconds(seconds) };
        Assert.Throws<ArgumentException>(() => notice.Encode(Now));
        var json = JsonNode.Parse(Notice().Encode(Now))!.AsObject();
        json["expiresAtUtc"] = notice.ExpiresAtUtc;
        Assert.False(PairingDiscoveryNotice.TryParse(Encoding.UTF8.GetBytes(json.ToJsonString()), Now, out _));
    }

    [Fact]
    public void RejectsMissingExtraDuplicateOrWronglyTypedFields()
    {
        var valid = Encoding.UTF8.GetString(Notice().Encode(Now));
        var extra = valid.Insert(1, "\"token\":\"not-allowed\",");
        var duplicate = valid.Insert(1, "\"version\":1,");
        var missing = JsonNode.Parse(valid)!.AsObject(); missing.Remove("peerName");
        var wrong = JsonNode.Parse(valid)!.AsObject(); wrong["port"] = "52345";
        var wrongVersion = JsonNode.Parse(valid)!.AsObject(); wrongVersion["version"] = 2;
        foreach (var value in new[] { extra, duplicate, missing.ToJsonString(), wrong.ToJsonString(), wrongVersion.ToJsonString() })
            Assert.False(PairingDiscoveryNotice.TryParse(Encoding.UTF8.GetBytes(value), Now, out _));
    }

    [Fact]
    public void RejectsInvalidUtf8TruncatedFramesAndOversizedFrames()
    {
        Assert.False(PairingDiscoveryNotice.TryParse(Array.Empty<byte>(), Now, out _));
        Assert.False(PairingDiscoveryNotice.TryParse(new byte[] { 0xff }, Now, out _));
        Assert.False(PairingDiscoveryNotice.TryParse(new byte[PairingDiscoveryNotice.MaximumBytes + 1], Now, out _));
        var data = Notice().Encode(Now);
        Assert.False(PairingDiscoveryNotice.TryParse(data.AsMemory(0, data.Length - 1), Now, out _));
    }

    [Fact]
    public void SessionDeadlineIsDistinctAndNoticeStillExpiresFirst()
    {
        var notice = Notice() with { SessionExpiresAtUtc = Now.AddMinutes(2) };
        var bytes = notice.Encode(Now);
        Assert.True(PairingDiscoveryNotice.TryParse(bytes, Now, out var result));
        Assert.Equal(notice.SessionExpiresAtUtc, result!.SessionExpiresAtUtc);
        Assert.False(PairingDiscoveryNotice.TryParse(bytes, notice.ExpiresAtUtc, out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(24)] [InlineData(121)]
    public void RejectsInvalidSessionDeadline(int seconds)
    {
        var invalid = Notice() with { SessionExpiresAtUtc = Now.AddSeconds(seconds) };
        Assert.Throws<ArgumentException>(() => invalid.Encode(Now));
        var json = JsonNode.Parse(Notice().Encode(Now))!.AsObject();
        json["sessionExpiresAtUtc"] = invalid.SessionExpiresAtUtc;
        Assert.False(PairingDiscoveryNotice.TryParse(Encoding.UTF8.GetBytes(json.ToJsonString()), Now, out _));
    }

    [Fact]
    public void RejectsMissingOrDuplicateSessionDeadline()
    {
        var valid = Encoding.UTF8.GetString(Notice().Encode(Now));
        var missing = JsonNode.Parse(valid)!.AsObject();
        missing.Remove("sessionExpiresAtUtc");
        Assert.False(PairingDiscoveryNotice.TryParse(Encoding.UTF8.GetBytes(missing.ToJsonString()), Now, out _));
        var duplicate = valid.Insert(1, "\"sessionExpiresAtUtc\":\"2026-11-12T12:01:00Z\",");
        Assert.False(PairingDiscoveryNotice.TryParse(Encoding.UTF8.GetBytes(duplicate), Now, out _));
    }
}
