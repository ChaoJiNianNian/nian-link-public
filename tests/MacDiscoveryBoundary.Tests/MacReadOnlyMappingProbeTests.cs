using System.Net;
using System.Net.NetworkInformation;
using MacMappingTestsOnly;
using MacNetworkRecord = NianLink.MacCandidateDiscovery.MacNetworkRecord;
using MacWifiEvidence = NianLink.MacCandidateDiscovery.MacWifiEvidence;

namespace MacDiscoveryBoundary.Tests;

public sealed class MacReadOnlyMappingProbeTests
{
    private static MacNetworkRecord Network() => new("en1", "en1", 15, NetworkInterfaceType.Wireless80211,
        OperationalStatus.Up, IPAddress.Parse("10.42.0.4"), IPAddress.Parse("255.255.255.0"));
    private static ApprovedMapping Approved() => new("en1", 15, IPAddress.Parse("10.42.0.4"), IPAddress.Parse("255.255.255.0"));
    private static MappingSample Good() => new([Network()], [new MacWifiEvidence("en1", 15)], [Network()], true);
    private static Task<MappingSample> Sample(MappingSample sample) => Task.FromResult(sample);

    [Fact]
    public async Task MatchingSnapshotIsOnlyDiagnostic()
    {
        var result = await new MacReadOnlyMappingProbe().ReadForTestAsync(Approved(), () => Sample(Good()), TimeSpan.FromSeconds(2), default);
        Assert.Equal("en1", result.BsdName);
        Assert.Equal(15, result.InterfaceIndex);
        Assert.False(result.HasLiveNetworkOrigin);
    }

    [Theory]
    [InlineData("address")][InlineData("mask")][InlineData("name")][InlineData("id")]
    [InlineData("index")][InlineData("wifi")][InlineData("approval")][InlineData("duplicate")]
    [InlineData("unrelated_change")][InlineData("index_duplicate")]
    public async Task MismatchAndChangedSnapshotsPermanentlyFail(string change)
    {
        var sample = Good(); var network = Network();
        sample = change switch
        {
            "address" => sample with { After = [network with { Address = IPAddress.Parse("10.42.0.5") }] },
            "mask" => sample with { After = [network with { Mask = IPAddress.Parse("255.255.0.0") }] },
            "name" => sample with { After = [network with { Name = "en2" }] },
            "id" => sample with { After = [network with { Id = "en2" }] },
            "index" => sample with { After = [network with { Index = 16 }] },
            "wifi" => sample with { Wifi = [new MacWifiEvidence("en1", 16)] },
            "approval" => sample with { ApprovedStillAvailable = false },
            "duplicate" => sample with { Wifi = [new MacWifiEvidence("en1", 15), new MacWifiEvidence("en1", 15)] },
            "unrelated_change" => sample with { After = [network, network with { Id = "en2", Name = "en2", Index = 16, Address = IPAddress.Parse("10.42.0.9") }] },
            "index_duplicate" => sample with { Wifi = [new MacWifiEvidence("en1", 15), new MacWifiEvidence("en2", 15)] },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var probe = new MacReadOnlyMappingProbe();
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.ReadForTestAsync(Approved(), () => Sample(sample), TimeSpan.FromSeconds(2), default));
        Assert.True(probe.IsRevoked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.ReadForTestAsync(Approved(), () => Sample(Good()), TimeSpan.FromSeconds(2), default));
    }

    [Theory]
    [InlineData("cancel")][InlineData("invalidate")][InlineData("timeout")]
    public async Task LateSuccessCannotUndoCancellationInvalidationOrTimeout(string reason)
    {
        var source = new TaskCompletionSource<MappingSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var probe = new MacReadOnlyMappingProbe();
        var pending = probe.ReadForTestAsync(Approved(), () => { entered.TrySetResult(); return source.Task; },
            reason == "timeout" ? TimeSpan.FromMilliseconds(80) : TimeSpan.FromSeconds(5), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (reason == "cancel") cancel.Cancel();
        if (reason == "invalidate") probe.Invalidate();
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        Assert.True(probe.IsRevoked);
        source.SetResult(Good());
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.ReadForTestAsync(Approved(), () => Sample(Good()), TimeSpan.FromSeconds(2), default));
    }

    [Fact]
    public async Task FailedSamplerPermanentlyInvalidates()
    {
        var probe = new MacReadOnlyMappingProbe();
        await Assert.ThrowsAnyAsync<Exception>(() => probe.ReadForTestAsync(Approved(), () => Task.FromException<MappingSample>(new IOException()), TimeSpan.FromSeconds(2), default));
        Assert.True(probe.IsRevoked);
    }

    [Theory]
    [InlineData("id")][InlineData("index")][InlineData("address")][InlineData("mask")]
    public async Task ObservationMustMatchApprovedMapping(string change)
    {
        var approved = change switch
        {
            "id" => Approved() with { Id = "en2" },
            "index" => Approved() with { Index = 16 },
            "address" => Approved() with { Address = IPAddress.Parse("10.42.0.9") },
            "mask" => Approved() with { Mask = IPAddress.Parse("255.255.0.0") },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var probe = new MacReadOnlyMappingProbe();
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.ReadForTestAsync(approved, () => Sample(Good()), TimeSpan.FromSeconds(2), default));
        Assert.True(probe.IsRevoked);
    }

    [Fact]
    public void ProductionAssemblyContainsNoInjectedMappingEntry()
    {
        var type = typeof(NianLink.MacCandidateDiscovery.MacCandidateNetworkBinding).Assembly
            .GetType("NianLink.MacCandidateDiscovery.MacReadOnlyMappingProbe", throwOnError: true)!;
        Assert.Null(type.GetMethod("ReadForTestAsync", System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public));
    }

    [Fact]
    public async Task SeparateDiscoveryAndHandoffSamplesWaitForEachOtherInsteadOfFailing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new MacReadOnlyMappingProbe().ReadCoordinatedForTestAsync(Approved(), async _ =>
        { entered.TrySetResult(); await release.Task; return Good(); }, () => Task.CompletedTask, TimeSpan.FromSeconds(3), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = new MacReadOnlyMappingProbe().ReadCoordinatedForTestAsync(Approved(), _ =>
        { secondEntered.TrySetResult(); return Task.FromResult(Good()); }, () => Task.CompletedTask, TimeSpan.FromSeconds(3), default);
        Assert.False(secondEntered.Task.IsCompleted);
        release.SetResult();
        Assert.Equal("en1", (await first).BsdName);
        Assert.Equal("en1", (await second).BsdName);
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task TimedOutNativeCallKeepsSamplingSlotUntilActuallyIdle()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeStillRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;
        var first = new MacReadOnlyMappingProbe().ReadCoordinatedForTestAsync(Approved(), _ =>
        { entered.TrySetResult(); return Task.FromException<MappingSample>(new TimeoutException()); },
            () => nativeStillRunning.Task, TimeSpan.FromMilliseconds(150), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
            var second = new MacReadOnlyMappingProbe().ReadCoordinatedForTestAsync(Approved(), _ =>
            { secondEntered = true; return Task.FromResult(Good()); }, () => Task.CompletedTask, TimeSpan.FromMilliseconds(150), default);
            await Assert.ThrowsAnyAsync<Exception>(() => second.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(secondEntered);
        }
        finally { nativeStillRunning.TrySetResult(); }
        var final = new MacReadOnlyMappingProbe().ReadCoordinatedForTestAsync(Approved(), _ => Task.FromResult(Good()),
            () => Task.CompletedTask, TimeSpan.FromSeconds(3), default);
        Assert.Equal("en1", (await final).BsdName);
        Assert.True(first.IsFaulted);
    }

    [Fact]
    public async Task CancelledQueuedObservationCannotInvokeLater()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var called = false;
        var first = new MacReadOnlyMappingProbe().ReadCoordinatedForTestAsync(Approved(), async _ =>
        { entered.TrySetResult(); await release.Task; return Good(); }, () => Task.CompletedTask, TimeSpan.FromSeconds(3), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancel = new CancellationTokenSource();
        var second = new MacReadOnlyMappingProbe().ReadCoordinatedForTestAsync(Approved(), _ =>
        { called = true; return Task.FromResult(Good()); }, () => Task.CompletedTask, TimeSpan.FromSeconds(3), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => second.WaitAsync(TimeSpan.FromSeconds(2)));
        release.SetResult();
        Assert.Equal("en1", (await first).BsdName);
        Assert.False(called);
    }
}
