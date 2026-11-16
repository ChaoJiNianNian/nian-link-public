using System.Net;
using NianLink.CandidateDiscovery;

namespace ConditionalCommitIsolation.Tests;

public sealed class CandidateSyntheticRulesTests
{
    private static readonly IPAddress Local = IPAddress.Parse("192.168.20.10");
    private static readonly byte[] Nonce = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
    [Fact]
    public void OnlyFixedNonIdentityFramesAndSameLocalEndpointAreAccepted()
    {
        var query = CandidateSyntheticRules.Packet(false, Nonce);
        Assert.Equal(22, query.Length);
        Assert.True(CandidateSyntheticRules.Accept(query, false, Nonce, Local, 53000, 53000, Local, 12, 12, false));
        Assert.False(CandidateSyntheticRules.Accept(query, true, Nonce, Local, 53000, 53000, Local, 12, 12, false));
        Assert.False(CandidateSyntheticRules.Accept(query, false, Nonce, IPAddress.Parse("192.168.20.11"), 53000, 53000, Local, 12, 12, false));
        Assert.False(CandidateSyntheticRules.Accept(query, false, Nonce, Local, 53001, 53000, Local, 12, 12, false));
        Assert.False(CandidateSyntheticRules.Accept(query, false, Nonce, Local, 53000, 53000, Local, 13, 12, false));
        Assert.False(CandidateSyntheticRules.Accept(query, false, Nonce, Local, 53000, 53000, Local, 12, 12, true));
        Assert.False(CandidateSyntheticRules.Accept(query, false, new byte[16], Local, 53000, 53000, Local, 12, 12, false));
        Assert.False(CandidateSyntheticRules.Accept(query[..21], false, Nonce, Local, 53000, 53000, Local, 12, 12, false));
        Assert.Throws<ArgumentException>(() => CandidateSyntheticRules.Packet(false, new byte[16]));
    }
}
