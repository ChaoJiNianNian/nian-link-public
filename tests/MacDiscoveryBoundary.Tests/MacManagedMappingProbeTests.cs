using MacManagedMappingProbe;

namespace MacDiscoveryBoundary.Tests;

public sealed class MacManagedMappingProbeTests
{
    [Theory]
    [InlineData()]
    [InlineData("--run")]
    [InlineData("--run", "bad", "10.42.0.4")]
    [InlineData("--run", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "invalid")]
    [InlineData("--run", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "::1")]
    [InlineData("--other", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "10.42.0.4")]
    public async Task RequiresExplicitRunDigestAndIpv4(params string[] args)
    {
        using var output = new StringWriter();
        Assert.Equal(2, await MappingProbeProgram.RunAsync(args, output));
        Assert.Contains("not_started", output.ToString());
    }

    [Fact]
    public async Task NonMacReturnsOnlyUnavailable()
    {
        if (OperatingSystem.IsMacOS()) return;
        using var output = new StringWriter();
        Assert.Equal(1, await MappingProbeProgram.RunAsync(["--run", new string('a', 64), "10.42.0.4"], output));
        Assert.Equal("{\"version\":1,\"status\":\"unavailable\"}", output.ToString().Trim());
    }
}
