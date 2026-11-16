using System.Text;
using System.Reflection;
using MacWifiAdapterTestsOnly;

namespace MacDiscoveryBoundary.Tests;

public sealed class MacWifiEvidenceReaderTests
{
    private static (int, int) Reply(byte[] output, string json, int status = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        bytes.CopyTo(output, 0);
        return (status, bytes.Length);
    }
    private static Task<string[]> Read(string json, int status = 0) =>
        WifiEvidenceReader.ReadForTestAsync(output => Reply(output, json, status), TimeSpan.FromSeconds(2));

    [Fact]
    public async Task ValidEvidenceReturnsOnlyNames()
    {
        Assert.Equal(new[] { "en1", "en2.3" }, await Read("{\"version\":1,\"wifi_candidates\":[\"en1\",\"en2.3\"]}"));
    }

    [Theory]
    [InlineData("{\"version\":2,\"wifi_candidates\":[\"en1\"]}")]
    [InlineData("{\"version\":1,\"version\":1,\"wifi_candidates\":[\"en1\"]}")]
    [InlineData("{\"version\":1,\"wifi_candidates\":[\"en1\"],\"wifi_candidates\":[\"en2\"]}")]
    [InlineData("{\"version\":1,\"wifi_candidates\":[]}")]
    [InlineData("{\"version\":1,\"wifi_candidates\":[\"en1\",\"en1\"]}")]
    [InlineData("{\"version\":1,\"wifi_candidates\":[\"../en1\"]}")]
    [InlineData("{\"version\":1,\"wifi_candidates\":[4]}")]
    [InlineData("{\"version\":1,\"wifi_candidates\":[\"en1\"],\"ssid\":\"x\"}")]
    [InlineData("{\"version\":1,\"wifi_candidates\":[\"en1\"]} trailing")]
    public async Task MalformedOrAmbiguousEvidenceIsRejected(string json) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(json));

    [Fact]
    public async Task StatusLengthAndEncodingFailuresAreRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read("{\"version\":1,\"wifi_candidates\":[\"en1\"]}", 2));
        foreach (var length in new[] { -1, 0, 16385 })
            await Assert.ThrowsAsync<InvalidOperationException>(() => WifiEvidenceReader.ReadForTestAsync(_ => (0, length), TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WifiEvidenceReader.ReadForTestAsync(b => { b[0] = 0xff; return (0, 1); }, TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WifiEvidenceReader.ReadForTestAsync(_ => throw new IOException(), TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WifiEvidenceReader.ReadForTestAsync(_ => throw new DllNotFoundException(), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ExcessNamesAndOversizedNamesAreRejected()
    {
        var names = string.Join(',', Enumerable.Range(0, 129).Select(i => $"\"en{i}\""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read($"{{\"version\":1,\"wifi_candidates\":[{names}]}}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read($"{{\"version\":1,\"wifi_candidates\":[\"{new string('a', 64)}\"]}}"));
    }

    [Fact]
    public void InjectionEntryIsAbsentFromProductionAssembly()
    {
        var type = typeof(NianLink.MacCandidateDiscovery.MacCandidateNetworkBinding).Assembly
            .GetType("NianLink.MacCandidateDiscovery.WifiEvidenceReader", throwOnError: true)!;
        Assert.Null(type.GetMethod("ReadForTestAsync", System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public void PinnedBridgeRejectsMissingOrDifferentFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.False(WifiEvidenceReader.MatchesPinnedBridgeForTest(path));
            Assert.False(WifiEvidenceReader.MatchesPinnedBridgeForTest(path + ".missing"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ActualFileDigestMustMatchTheContainingAssemblyMetadata()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [97, 98, 99]);
            Assert.True(WifiEvidenceReader.MatchesPinnedBridgeForTest(path));
            File.WriteAllBytes(path, [97, 98, 100]);
            Assert.False(WifiEvidenceReader.MatchesPinnedBridgeForTest(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PinMetadataRequiresExactlyOneValidLowercaseSha256()
    {
        const string key = "NianWifiEvidencePinnedSha256";
        var valid = new string('a', 64);
        Assert.Equal(valid, WifiEvidenceReader.SelectPin([new(key, valid)]));
        Assert.Null(WifiEvidenceReader.SelectPin([]));
        Assert.Null(WifiEvidenceReader.SelectPin([new(key, valid), new(key, valid)]));
        Assert.Null(WifiEvidenceReader.SelectPin([new(key, new string('A', 64))]));
        Assert.Null(WifiEvidenceReader.SelectPin([new(key, new string('g', 64))]));
        Assert.Null(WifiEvidenceReader.SelectPin([new(key, "abc")]));
        Assert.Null(WifiEvidenceReader.SelectPin([new("other", valid)]));
        Assert.Equal(valid, WifiEvidenceReader.SelectPin([new("other", "x"), new(key, valid)]));
    }

    [Fact]
    public void DefaultCandidateAssemblyDoesNotCarryOldHardcodedPin()
    {
        var assembly = typeof(NianLink.MacCandidateDiscovery.MacCandidateNetworkBinding).Assembly;
        var values = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(x => x.Key == "NianWifiEvidencePinnedSha256").ToArray();
        Assert.Empty(values);
    }

    [Fact]
    public async Task TimedOutCallKeepsItsBufferAndBlocksAnotherCallUntilFinished()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        byte[]? captured = null;
        var first = WifiEvidenceReader.ReadForTestAsync(buffer =>
        {
            captured = buffer;
            entered.Set();
            release.Wait();
            var result = Reply(buffer, "{\"version\":1,\"wifi_candidates\":[\"en1\"]}");
            finished.Set();
            return result;
        }, TimeSpan.FromMilliseconds(100));
        Task? idle = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<TimeoutException>(() => first);
            Assert.NotNull(captured);
            idle = WifiEvidenceReader.WaitForNativeIdleAsync();
            Assert.False(idle.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => WifiEvidenceReader.ReadForTestAsync(_ => (0, 0), TimeSpan.FromSeconds(1)));
            GC.Collect(); GC.WaitForPendingFinalizers();
        }
        finally { release.Set(); }
        await idle!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(first.IsFaulted);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { return Read("{\"version\":1,\"wifi_candidates\":[\"en2\"]}").GetAwaiter().GetResult().SequenceEqual(["en2"]); }
            catch (InvalidOperationException) { return false; }
        }, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ProductionReaderFailsClosedOnNonMac()
    {
        if (!OperatingSystem.IsMacOS())
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => WifiEvidenceReader.ReadAsync());
    }
}
