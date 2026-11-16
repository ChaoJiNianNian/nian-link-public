using NianLink.Windows;

namespace Shutdown.Tests;

public sealed class ShutdownCoordinatorTests
{
    [Fact]
    public async Task AlreadyStoppedCanExitWithoutCallingStop()
    {
        var called = false;
        var result = await new ShutdownCoordinator().PrepareAsync(() => false, () => { called = true; return Task.FromResult<string?>(null); });
        Assert.True(result.CanExit); Assert.False(called);
    }

    [Fact]
    public async Task SuccessfulStopAllowsExit()
    {
        var running = true;
        var result = await new ShutdownCoordinator().PrepareAsync(() => running, () => { running = false; return Task.FromResult<string?>(null); });
        Assert.True(result.CanExit); Assert.Null(result.Error);
    }

    [Fact]
    public async Task IncompleteStopKeepsUiAliveAndCanBeRetried()
    {
        var running = true; var stopAttempts = 0; var gate = new ShutdownCoordinator();
        Task<string?> Stop() { stopAttempts++; if (stopAttempts == 2) running = false; return Task.FromResult<string?>("清理未完成"); }
        var first = await gate.PrepareAsync(() => running, Stop);
        Assert.False(first.CanExit); Assert.Contains("清理未完成", first.Error);
        // The next stop succeeded without a warning.
        var second = await gate.PrepareAsync(() => running, () => { running = false; stopAttempts++; return Task.FromResult<string?>(null); });
        Assert.True(second.CanExit); Assert.Equal(2, stopAttempts);
    }

    [Fact]
    public async Task CoordinatorWarningRequiresUserToCheckBeforeRetryingExit()
    {
        var running = true; var gate = new ShutdownCoordinator();
        var first = await gate.PrepareAsync(() => running, () => { running = false; return Task.FromResult<string?>("显示器回切失败"); });
        Assert.False(first.CanExit); Assert.Contains("显示器回切失败", first.Error);
        var retry = await gate.PrepareAsync(() => running, () => throw new InvalidOperationException("must not stop twice"));
        Assert.True(retry.CanExit);
    }

    [Fact]
    public async Task ExceptionNeverPermitsExitAndLaterAttemptCanSucceed()
    {
        var gate = new ShutdownCoordinator(); var running = true;
        var first = await gate.PrepareAsync(() => running, () => Task.FromException<string?>(new IOException("拒绝")));
        Assert.False(first.CanExit); Assert.Contains("拒绝", first.Error);
        var second = await gate.PrepareAsync(() => running, () => { running = false; return Task.FromResult<string?>(null); });
        Assert.True(second.CanExit);
    }

    [Fact]
    public async Task ConcurrentExitAttemptsDoNotStopTwice()
    {
        var gate = new ShutdownCoordinator(); var running = true; var calls = 0;
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> Stop() { calls++; return pending.Task; }
        var first = gate.PrepareAsync(() => running, Stop);
        var second = await gate.PrepareAsync(() => running, Stop);
        Assert.True(second.Busy); Assert.False(second.CanExit); Assert.Equal(1, calls);
        running = false; pending.SetResult(null);
        Assert.True((await first).CanExit);
    }

    [Fact]
    public async Task FailureToReadRunningStateNeverPermitsExit()
    {
        var result = await new ShutdownCoordinator().PrepareAsync(() => throw new InvalidOperationException("状态不可用"), () => Task.FromResult<string?>(null));
        Assert.False(result.CanExit); Assert.Contains("状态不可用", result.Error);
    }
}
