using NianLink.Windows;

public sealed class StandaloneSettingsTests
{
    [Fact]
    public async Task SaveUpdatesCollaborationOnlyAfterPersistingNewNames()
    {
        var stored = "old"; var accepted = "old"; var collaboration = "old";
        var coordinator = new StandaloneSettingsCoordinator<string>(value => { stored = value; return Task.CompletedTask; },
            value => accepted = value, () => { collaboration = stored; return Task.CompletedTask; }, () => Task.CompletedTask);
        await coordinator.SaveAsync("new");
        Assert.Equal("new", stored); Assert.Equal("new", accepted); Assert.Equal("new", collaboration);
    }

    [Fact]
    public async Task FailedSaveDoesNotChangeEitherCacheOrReloadCollaboration()
    {
        var accepted = "old"; var reloads = 0;
        var coordinator = new StandaloneSettingsCoordinator<string>(_ => Task.FromException(new IOException("save failed")),
            value => accepted = value, () => { reloads++; return Task.CompletedTask; }, () => Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => coordinator.SaveAsync("new"));
        Assert.Equal("old", accepted); Assert.Equal(0, reloads);
    }

    [Fact]
    public async Task SettingsStartReloadsPersistedNamesBeforeStartingEngine()
    {
        var stored = "new"; var collaboration = "old"; string? startedWith = null;
        var coordinator = new StandaloneSettingsCoordinator<string>(_ => Task.CompletedTask, _ => { },
            () => { collaboration = stored; return Task.CompletedTask; }, () => { startedWith = collaboration; return Task.CompletedTask; });
        await coordinator.StartAsync();
        Assert.Equal("new", startedWith);
    }

    [Fact]
    public async Task PendingNamesAreSavedBeforeSettingsPageStarts()
    {
        var stored = "old"; var accepted = "old"; var collaboration = "old"; string? startedWith = null;
        var coordinator = new StandaloneSettingsCoordinator<string>(value => { stored = value; return Task.CompletedTask; },
            value => accepted = value, () => { collaboration = stored; return Task.CompletedTask; },
            () => { startedWith = collaboration; return Task.CompletedTask; });
        await coordinator.StartAsync("edited", "old");
        Assert.Equal("edited", stored); Assert.Equal("edited", accepted); Assert.Equal("edited", startedWith);
    }

    [Fact]
    public async Task FailedPendingSaveBlocksBothSettingsAndMiniStart()
    {
        var starts = 0; var reloads = 0;
        var coordinator = new StandaloneSettingsCoordinator<string>(_ => Task.FromException(new IOException("save failed")), _ => { },
            () => { reloads++; return Task.CompletedTask; }, () => { starts++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync("edited", "old"));
        await Assert.ThrowsAsync<IOException>(() => coordinator.SaveIfChangedAsync("edited", "old"));
        Assert.Equal(0, starts); Assert.Equal(0, reloads);
    }

    [Fact]
    public async Task UnchangedFormDoesNotSaveButStillReloadsForStart()
    {
        var saves = 0; var reloads = 0; var starts = 0;
        var coordinator = new StandaloneSettingsCoordinator<string>(_ => { saves++; return Task.CompletedTask; }, _ => { },
            () => { reloads++; return Task.CompletedTask; }, () => { starts++; return Task.CompletedTask; });
        await coordinator.SaveIfChangedAsync("same", "same");
        await coordinator.StartAsync("same", "same");
        Assert.Equal(0, saves); Assert.Equal(1, reloads); Assert.Equal(1, starts);
    }

    [Fact]
    public async Task FailedReloadAfterSavePreservesSavedValueAndBlocksStartUntilRetry()
    {
        var stored = "old"; var accepted = "old"; var failing = true; var started = false;
        var coordinator = new StandaloneSettingsCoordinator<string>(value => { stored = value; return Task.CompletedTask; },
            value => accepted = value, () => failing ? Task.FromException(new IOException("reload failed")) : Task.CompletedTask,
            () => { started = true; return Task.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => coordinator.SaveAsync("new"));
        Assert.Equal("new", stored); Assert.Equal("new", accepted);
        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync()); Assert.False(started);
        failing = false; await coordinator.StartAsync(); Assert.True(started);
    }

    [Fact]
    public async Task SaveCannotInterleaveWithMiniStartupAndRunningEngineRejectsLaterChange()
    {
        var gate = new StandaloneOperationGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stored = "old"; var current = "old"; var collaboration = "old"; var running = false;
        var coordinator = new StandaloneSettingsCoordinator<string>(value =>
            {
                if (running) throw new InvalidOperationException("stop before changing settings");
                stored = value; return Task.CompletedTask;
            }, value => current = value, () => { collaboration = stored; return Task.CompletedTask; }, () => Task.CompletedTask);
        var mini = gate.RunAsync(async () =>
        {
            await coordinator.SaveIfChangedAsync("Mac-A", current);
            await coordinator.StartAsync();
            entered.SetResult();
            await release.Task;
            running = true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var save = gate.RunAsync(() => coordinator.SaveAsync("Mac-B"));
        Assert.False(save.IsCompleted);
        release.SetResult(); await mini;
        await Assert.ThrowsAsync<InvalidOperationException>(() => save);
        Assert.Equal("Mac-A", stored); Assert.Equal(stored, current); Assert.Equal(stored, collaboration);
    }

    [Fact]
    public async Task MiniReadsFormAfterSettingsStartReleasesSharedGate()
    {
        var gate = new StandaloneOperationGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = "old"; var stored = "old"; var reloads = 0; var miniStarts = 0; var saves = 0;
        var coordinator = new StandaloneSettingsCoordinator<string>(value => { saves++; stored = value; return Task.CompletedTask; },
            value => current = value, () => { reloads++; return Task.CompletedTask; }, async () => { entered.SetResult(); await release.Task; });
        var settingsStart = gate.RunAsync(() => coordinator.StartAsync("new", current));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var mini = gate.RunAsync(async () => { await coordinator.SaveIfChangedAsync("new", current); miniStarts++; });
        Assert.False(mini.IsCompleted);
        release.SetResult(); await settingsStart; await mini;
        Assert.Equal("new", stored); Assert.Equal("new", current);
        Assert.Equal(1, saves); Assert.Equal(2, reloads); Assert.Equal(1, miniStarts);
    }

    [Fact]
    public async Task CancelledWaitOrFailedStartNeverLeavesGateLocked()
    {
        var gate = new StandaloneOperationGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = gate.RunAsync(async () => { entered.SetResult(); await release.Task; throw new IOException("start failed"); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var waiting = gate.RunAsync(() => Task.CompletedTask, cancelled.Token); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult(); await Assert.ThrowsAsync<IOException>(() => active);
        var completed = false;
        await gate.RunAsync(() => { completed = true; return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(completed);
    }
}
