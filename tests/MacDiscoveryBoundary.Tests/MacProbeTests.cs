using System.Text.Json;
using NianLink.MacDiscoveryBoundary;

namespace MacDiscoveryBoundary.Tests;

public sealed class MacProbeTests
{
    [Theory]
    [InlineData()]
    [InlineData("--help")]
    [InlineData("--run", "extra")]
    public void MissingOrExtraArgumentsNeverCollect(params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var called = false;
        var result = MacProbe.Run(args, true, () => { called = true; return ["en0"]; }, output, errors);
        Assert.NotEqual(0, result);
        Assert.False(called);
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void ExplicitRunOnNonMacNeverCallsNativeCollector()
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var called = false;
        var result = MacProbe.Run(["--run"], false, () => { called = true; return ["en0"]; }, output, errors);
        Assert.NotEqual(0, result);
        Assert.False(called);
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void SuccessIsBoundedJsonWithOnlyVersionAndCandidateNames()
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        Assert.Equal(0, MacProbe.Run(["--run"], true, () => ["en0", "en5"], output, errors));
        Assert.Equal("", errors.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var properties = json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "version", "vlan_compatible_candidates" }, properties);
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(new[] { "en0", "en5" }, json.RootElement.GetProperty("vlan_compatible_candidates").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public void CollectorExceptionIsSanitizedAndEmitsNoPartialEvidence()
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = MacProbe.Run(["--run"], true, () => throw new InvalidOperationException("private detail"), output, errors);
        Assert.NotEqual(0, code);
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain("private detail", errors.ToString());
    }

    [Theory]
    [InlineData("bad\nname")]
    [InlineData("")]
    [InlineData("contains space")]
    public void InvalidEvidenceDoesNotEmitOutput(string invalid)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        Assert.NotEqual(0, MacProbe.Run(["--run"], true, () => ["en0", invalid], output, errors));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void DuplicateAndExcessEvidenceAreRejected()
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        Assert.NotEqual(0, MacProbe.Run(["--run"], true, () => ["en0", "en0"], output, errors));
        Assert.NotEqual(0, MacProbe.Run(["--run"], true, () => Enumerable.Range(0, 1025).Select(i => "en" + i).ToArray(), output, errors));
        Assert.Equal("", output.ToString());
    }
}
