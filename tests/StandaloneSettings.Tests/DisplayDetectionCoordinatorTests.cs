using NianLink.Windows;

public class DisplayDetectionCoordinatorTests
{
    [Fact] public async Task SlowDetectionDoesNotBlockOtherOperationsAndIgnoresLateResultAfterEdit()
    {
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new DisplayDetectionCoordinator<int>(_ => release.Task);
        var detection = coordinator.DetectAsync();
        var operations = new StandaloneOperationGate();
        var started = false;
        await operations.RunAsync(() => { started = true; return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(started); Assert.False(detection.IsCompleted);
        coordinator.Invalidate();
        release.SetResult(12);
        Assert.False((await detection).Accepted);
    }

    [Fact] public async Task EarlierDetectionCannotOverwriteNewerResult()
    {
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var coordinator = new DisplayDetectionCoordinator<int>(_ => ++count == 1 ? first.Task : second.Task);
        var a = coordinator.DetectAsync(); var b = coordinator.DetectAsync();
        second.SetResult(2); var latest = await b;
        first.SetResult(1); var stale = await a;
        Assert.True(latest.Accepted); Assert.Equal(2, latest.Value); Assert.False(stale.Accepted);
    }

    [Fact] public async Task CancellingScanDoesNotCancelIndependentWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new DisplayDetectionCoordinator<int>(async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return 1; });
        var detection = coordinator.DetectAsync(); await entered.Task;
        coordinator.Invalidate();
        Assert.False((await detection.WaitAsync(TimeSpan.FromSeconds(1))).Accepted);
        Assert.Equal(4, await Task.FromResult(4));
    }

    [Fact] public async Task StaleDetectionFailureDoesNotReplaceUserFeedback()
    {
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new DisplayDetectionCoordinator<int>(_ => release.Task);
        var detection = coordinator.DetectAsync(); coordinator.Invalidate();
        release.SetException(new IOException("late detection failure"));
        Assert.False((await detection).Accepted);
    }

    [Fact] public async Task LaterImportCannotApplyAfterUserChangesSettings()
    {
        using var coordinator = new DisplayDetectionCoordinator<int>(_ => Task.FromResult(1));
        Assert.True((await coordinator.DetectAsync()).Accepted);
        var ticket = coordinator.CaptureVersion();
        var read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var import = ApplyIfCurrent();
        coordinator.Invalidate(); read.SetResult(1);
        Assert.False(await import);
        async Task<bool> ApplyIfCurrent() { await read.Task; return coordinator.IsCurrent(ticket); }
    }
}
