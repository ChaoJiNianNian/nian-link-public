using NianLink.MultiDevice;
using NianLink.CandidateDiscovery;
using DesktopToolbox.DeskLink;
using System.IO;

namespace MultiDevice.Windows.Tests;

public sealed class DiscoveryUiLifetimeTests
{
    [Fact]
    public async Task StopWaitsForLateStartAndDisposesItWithoutPublishing()
    {
        var lifetime = new DiscoveryUiLifetime<FakeRun>();
        var gate = new TaskCompletionSource<FakeRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starting = lifetime.StartAsync(_ => { entered.SetResult(); return gate.Task; });
        await entered.Task;
        var stopping = lifetime.StopAsync();
        Assert.False(stopping.IsCompleted);
        var run = new FakeRun(); gate.SetResult(run);
        Assert.False(await starting);
        await stopping;
        Assert.True(run.Disposed);
        Assert.Null(lifetime.Current);
    }

    [Fact]
    public async Task StopDrainsExactlyOnceAndClosePermanentlyRejectsStart()
    {
        var lifetime = new DiscoveryUiLifetime<FakeRun>();
        var run = new FakeRun { WaitOnDispose = true };
        Assert.True(await lifetime.StartAsync(_ => Task.FromResult(run)));
        var first = lifetime.StopAsync();
        var second = lifetime.StopAsync();
        Assert.False(first.IsCompleted);
        run.Release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, run.DisposeCount);
        await lifetime.CloseAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.StartAsync(_ => Task.FromResult(new FakeRun())));
    }

    [Fact]
    public async Task CleanupFailurePermanentlyDisablesNewRuns()
    {
        var lifetime = new DiscoveryUiLifetime<FakeRun>();
        Assert.True(await lifetime.StartAsync(_ => Task.FromResult(new FakeRun { ThrowOnDispose = true })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.StopAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.StartAsync(_ => Task.FromResult(new FakeRun())));
    }

    [Fact]
    public async Task StopSignalsPendingFactoryBeforeWaitingForIt()
    {
        var lifetime = new DiscoveryUiLifetime<FakeRun>();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<FakeRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starting = lifetime.StartAsync(token => { started.SetResult(token); return release.Task; });
        var token = await started.Task;
        var stopping = lifetime.StopAsync();
        Assert.True(token.IsCancellationRequested);
        var run = new FakeRun(); release.SetResult(run);
        Assert.False(await starting);
        await stopping;
        Assert.True(run.Disposed);
    }

    [Fact]
    public async Task ReviewIsRevokedBeforeWaitingForScanDrain()
    {
        var events = new List<string>();
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = DiscoveryUiStop.StopAndReleaseAsync(
            () => { events.Add("revoke"); return Task.CompletedTask; },
            () => { events.Add("drain"); return drain.Task; });
        Assert.Equal(new[] { "revoke", "drain" }, events);
        Assert.False(stopping.IsCompleted);
        drain.SetResult(); await stopping;
    }

    [Fact]
    public async Task UnknownCleanupDuringLateStartupPoisonsOwner()
    {
        var lifetime = new DiscoveryUiLifetime<FakeRun>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startup = new TaskCompletionSource<FakeRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starting = lifetime.StartAsync(_ => { entered.SetResult(); return startup.Task; });
        await entered.Task;
        var stopping = lifetime.StopAsync();
        startup.SetException(new DiscoveryStartupCleanupException(new Exception("startup"), new Exception("cleanup")));
        await Assert.ThrowsAsync<DiscoveryStartupCleanupException>(() => starting);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stopping);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.StartAsync(_ => Task.FromResult(new FakeRun())));
    }

    [Fact]
    public async Task PairingStartupCleanupFailurePoisonsOwnerEvenWhenStopIsAlreadyWaiting()
    {
        var lifetime = new DiscoveryUiLifetime<FakeRun>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startup = new TaskCompletionSource<FakeRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starting = lifetime.StartAsync(_ => { entered.SetResult(); return startup.Task; });
        await entered.Task;
        var stopping = lifetime.StopAsync();
        startup.SetException(new PairingStartupCleanupException(new IOException("startup"), [new IOException("cleanup")]));
        await Assert.ThrowsAsync<PairingStartupCleanupException>(() => starting);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stopping);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.StartAsync(_ => Task.FromResult(new FakeRun())));
    }

    [Fact]
    public async Task OrdinaryPairingStartupFailureDoesNotPoisonOwner()
    {
        var lifetime = new DiscoveryUiLifetime<FakeRun>();
        await Assert.ThrowsAsync<IOException>(() => lifetime.StartAsync(_ => Task.FromException<FakeRun>(new IOException("connection failed"))));
        Assert.True(await lifetime.StartAsync(_ => Task.FromResult(new FakeRun())));
        await lifetime.CloseAsync();
    }

    private sealed class FakeRun : IAsyncDisposable
    {
        internal bool WaitOnDispose, ThrowOnDispose;
        internal bool Disposed => DisposeCount > 0;
        internal int DisposeCount;
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            if (WaitOnDispose) await Release.Task;
            if (ThrowOnDispose) throw new InvalidOperationException("synthetic cleanup failure");
        }
    }
}
