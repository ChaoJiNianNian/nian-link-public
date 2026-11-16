using System.Security.Cryptography;
using MacManagedWifiProbe;
using MacWifiAdapterTestsOnly;

namespace MacDiscoveryBoundary.Tests;

public sealed class MacManagedWifiProbeTests
{
    [Theory]
    [InlineData()]
    [InlineData("--run")]
    [InlineData("--run", "bad")]
    [InlineData("--other", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("--run", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "extra")]
    public async Task RequiresExactExplicitRunAndDigest(params string[] args)
    {
        using var output = new StringWriter();
        Assert.Equal(2, await ProbeProgram.RunAsync(args, output));
        Assert.Contains("not_started", output.ToString());
    }

    [Fact]
    public async Task NonMacCannotRunProbe()
    {
        if (OperatingSystem.IsMacOS()) return;
        using var output = new StringWriter();
        Assert.Equal(1, await ProbeProgram.RunAsync(["--run", new string('a', 64)], output));
        Assert.Contains("unavailable", output.ToString());
    }

    [Fact]
    public async Task DigestMustMatchExactlyAndMissingFileIsRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var digest = Convert.ToHexString(SHA256.HashData([1, 2, 3]));
            Assert.True(await ProbeDigest.MatchesAsync(path, digest));
            Assert.False(await ProbeDigest.MatchesAsync(path, new string('0', 64)));
            Assert.False(await ProbeDigest.MatchesAsync(path + ".missing", digest));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ProbeAndNativeLoaderShareFixedAbsolutePath()
    {
        Assert.Equal(WifiEvidenceReader.LibraryPath, ProbeProgram.LibraryPath);
        Assert.True(Path.IsPathFullyQualified(ProbeProgram.LibraryPath));
        Assert.Equal(Path.GetFullPath(ProbeProgram.LibraryPath), ProbeProgram.LibraryPath);
    }
}
