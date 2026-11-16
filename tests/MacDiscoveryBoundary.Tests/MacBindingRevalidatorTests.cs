using System.Net.NetworkInformation;
using MacBindingRevalidator = MacBindingTestsOnly.MacBindingRevalidator;
using MacCandidateNetworkBinding = NianLink.MacCandidateDiscovery.MacCandidateNetworkBinding;

namespace MacDiscoveryBoundary.Tests;

public sealed class MacBindingRevalidatorTests
{
    private static MacCandidateNetworkBinding.Snapshot Good() => new("en1", "en1", 15,
        NetworkInterfaceType.Wireless80211, OperationalStatus.Up, 0x0a2a0004, 0xffffff00, "en1");
    private static MacBindingRevalidator New(Func<CancellationToken, Task<MacCandidateNetworkBinding.Snapshot>> read, int timeout = 2000) =>
        MacBindingRevalidator.ForTest(Good(), read, TimeSpan.FromMilliseconds(timeout));

    [Fact]
    public async Task EachSuccessSamplesAgain()
    {
        var count = 0;
        var state = New(_ => { count++; return Task.FromResult(Good()); });
        Assert.True(await state.CheckAsync(default));
        Assert.True(await state.CheckAsync(default));
        Assert.Equal(2, count);
        Assert.False(state.RevocationToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData("name")][InlineData("id")][InlineData("index")][InlineData("address")]
    [InlineData("mask")][InlineData("type")][InlineData("status")][InlineData("bsd")]
    public async Task AnyMappingChangePermanentlyRevokes(string change)
    {
        var observed = Good();
        var state = New(_ => Task.FromResult(observed));
        Assert.True(await state.CheckAsync(default));
        observed = change switch
        {
            "name" => observed with { Name = "en2" }, "id" => observed with { Id = "en2" },
            "index" => observed with { Index = 16 }, "address" => observed with { Address = 0x0a2a0005 },
            "mask" => observed with { Mask = 0xffff0000 }, "type" => observed with { Type = NetworkInterfaceType.Ethernet },
            "status" => observed with { Status = OperationalStatus.Down }, "bsd" => observed with { BsdName = "en2" },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        Assert.False(await state.CheckAsync(default));
        Assert.True(state.RevocationToken.IsCancellationRequested);
        observed = Good();
        Assert.False(await state.CheckAsync(default));
    }

    [Theory]
    [InlineData("cancel")][InlineData("revoke")][InlineData("timeout")][InlineData("concurrent")]
    public async Task FailedOrOverlappingCheckCannotBeRevivedByLateSuccess(string reason)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TaskCompletionSource<MacCandidateNetworkBinding.Snapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = New(_ => { entered.TrySetResult(); return source.Task; }, reason == "timeout" ? 100 : 2000);
        using var cancellation = new CancellationTokenSource();
        var pending = state.CheckAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (reason == "cancel") cancellation.Cancel();
        if (reason == "revoke") state.Revoke();
        if (reason == "concurrent") Assert.False(await state.CheckAsync(default));
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(state.RevocationToken.IsCancellationRequested);
        source.SetResult(Good());
        Assert.False(await state.CheckAsync(default));
    }

    [Fact]
    public async Task ExceptionPermanentlyRevokes()
    {
        var state = New(_ => Task.FromException<MacCandidateNetworkBinding.Snapshot>(new IOException()));
        Assert.False(await state.CheckAsync(default));
        Assert.True(state.RevocationToken.IsCancellationRequested);
    }

    [Fact]
    public void ProductionStateHasNoSyntheticFactory()
    {
        var type = typeof(MacCandidateNetworkBinding).Assembly.GetType("NianLink.MacCandidateDiscovery.MacBindingRevalidator", true)!;
        Assert.Null(type.GetMethod("ForTest", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
    }
}
