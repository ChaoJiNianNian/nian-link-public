using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.ViewModels;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacWirelessViewModelTests
{
    [Fact]
    public async Task StartupReadsActualStateWithoutChangingTheNetwork()
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Enabled) };
        await using var model = new MacWirelessViewModel(service);
        Assert.False(model.CanChange);
        await model.RefreshAsync();
        Assert.True(model.CanChange);
        Assert.False(model.IsSmoothEnabled);
        Assert.Equal(0, service.Changes);
    }

    [Fact]
    public async Task ToggleWaitsForAuthorizationAndUsesReadBackRatherThanOptimisticState()
    {
        var completion = new TaskCompletionSource<MacWirelessSnapshot>();
        var service = new FakeWirelessService { State = new(MacAwdlState.Enabled), PendingChange = completion.Task };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        var change = model.SetSmoothModeAsync(true);
        Assert.True(model.IsBusy);
        Assert.False(model.IsSmoothEnabled);
        Assert.False(model.CanChange);
        Assert.Contains("授权", model.StatusMessage);
        await model.SetSmoothModeAsync(true);
        await model.RefreshAsync();
        Assert.Equal(1, service.Changes);
        completion.SetResult(new(MacAwdlState.Disabled));
        await change;
        Assert.True(model.IsSmoothEnabled);
        Assert.True(model.CanChange);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task CanceledAuthorizationReadsActualStateAndOffersTheToggleAgain()
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Enabled), Failure = new OperationCanceledException() };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        await model.SetSmoothModeAsync(true);
        Assert.False(model.IsSmoothEnabled);
        Assert.True(model.CanChange);
        Assert.Contains("取消", model.ErrorMessage);
    }

    [Fact]
    public async Task FailedChangeDisplaysActualStateAndKeepsAnActionableChineseMessage()
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Enabled), Failure = new IOException("helper failed") };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        await model.SetSmoothModeAsync(true);
        Assert.False(model.IsSmoothEnabled);
        Assert.True(model.CanChange);
        Assert.Contains("未完成", model.ErrorMessage);
        await model.RefreshAsync();
        Assert.Contains("未完成", model.ErrorMessage);
    }

    [Fact]
    public async Task ReadBackFailureNeverClaimsTheRequestedModeWasEnabled()
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Enabled), Result = new(MacAwdlState.Unknown, Error: "无法读取实际状态") };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        await model.SetSmoothModeAsync(true);
        Assert.False(model.IsSmoothEnabled);
        Assert.False(model.CanChange);
        Assert.Contains("实际状态", model.ErrorMessage);
    }

    [Fact]
    public async Task AnExternalSystemChangeIsReflectedWithoutEnforcingThePreviousChoice()
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Disabled) };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        Assert.True(model.IsSmoothEnabled);
        service.State = new(MacAwdlState.Enabled);
        await model.RefreshAsync();
        Assert.False(model.IsSmoothEnabled);
        Assert.Equal(0, service.Changes);
    }

    [Theory]
    [InlineData(MacAwdlState.Unknown)]
    [InlineData(MacAwdlState.Unavailable)]
    public async Task UnknownOrUnavailableStateCannotRequestAChange(MacAwdlState state)
    {
        var service = new FakeWirelessService { State = new(state) };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        await model.SetSmoothModeAsync(true);
        Assert.False(model.CanChange);
        Assert.Equal(0, service.Changes);
    }

    [Fact]
    public async Task ClosingCancelsPendingWorkAndDoesNotRestoreTheManualNetworkChoice()
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Disabled), WaitForCancellation = true };
        var model = new MacWirelessViewModel(service);
        var refresh = model.RefreshAsync();
        await model.DisposeAsync();
        await refresh;
        Assert.True(service.WasCanceled);
        await model.RefreshAsync();
        await model.SetSmoothModeAsync(false);
        Assert.Equal(0, service.Changes);
        Assert.False(model.CanChange);
    }

    [Fact]
    public async Task CancelingPendingWorkForAnAbortedCloseAllowsLaterRefreshAndChanges()
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Enabled), WaitForCancellation = true };
        await using var model = new MacWirelessViewModel(service);
        var pending = model.RefreshAsync();
        await model.CancelPendingAsync();
        await pending;
        service.WaitForCancellation = false;
        await model.RefreshAsync();
        await model.SetSmoothModeAsync(true);
        Assert.True(model.IsSmoothEnabled);
        Assert.True(model.CanChange);
        Assert.Equal(1, service.Changes);
    }
    [Theory]
    [InlineData(MacAwdlState.Disabled, true, "已开启", true)]
    [InlineData(MacAwdlState.Enabled, true, "已开启，正在恢复…", true)]
    [InlineData(MacAwdlState.Unknown, true, "已开启，等待恢复…", true)]
    [InlineData(MacAwdlState.Unavailable, true, "已开启，等待恢复…", true)]
    [InlineData(MacAwdlState.Enabled, false, "已关闭", true)]
    [InlineData(MacAwdlState.Disabled, false, "已关闭，互联尚未恢复", true)]
    public async Task PersistedChoiceControlsTheSwitchWhileActualStateControlsRecoveryStatus(MacAwdlState state, bool keep, string status, bool canChange)
    {
        var service = new FakeWirelessService { State = new(state, KeepEnabled: keep) };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        Assert.Equal(keep, model.IsSmoothEnabled);
        Assert.Equal(status, model.StatusMessage);
        Assert.Equal(canChange, model.CanChange);
        Assert.Equal(0, service.Changes);
    }

    [Theory]
    [InlineData(MacAwdlState.Unknown)]
    [InlineData(MacAwdlState.Unavailable)]
    [InlineData(MacAwdlState.Enabled)]
    public async Task MissingPreferenceNeverDiscardsAnAlreadyConfirmedChoice(MacAwdlState state)
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Disabled, KeepEnabled: true) };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        service.State = new(state);
        await model.RefreshAsync();
        Assert.True(model.IsSmoothEnabled);
        Assert.True(model.CanChange);
        Assert.Equal(0, service.Changes);
        service.State = new(MacAwdlState.Enabled, KeepEnabled: false);
        await model.RefreshAsync();
        Assert.False(model.IsSmoothEnabled);
        Assert.Equal("已关闭", model.StatusMessage);
    }

    [Fact]
    public async Task RefreshExceptionKeepsTheChosenModeAndAllowsTurningItOff()
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Disabled, KeepEnabled: true) };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        service.RefreshFailure = new IOException("status read failed");
        await model.RefreshAsync();
        Assert.True(model.IsSmoothEnabled);
        Assert.True(model.CanChange);
        Assert.Contains("读取", model.ErrorMessage);
        service.Result = new(MacAwdlState.Unknown, KeepEnabled: false);
        await model.SetSmoothModeAsync(false);
        Assert.False(model.IsSmoothEnabled);
        Assert.Equal(1, service.Changes);
    }

    [Theory]
    [InlineData(MacAwdlState.Unknown)]
    [InlineData(MacAwdlState.Unavailable)]
    public async Task KeptModeCanBeManuallyStoppedEvenWhenAwdlIsUnavailable(MacAwdlState state)
    {
        var service = new FakeWirelessService { State = new(state, KeepEnabled: true), Result = new(state, KeepEnabled: false) };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        await model.SetSmoothModeAsync(false);
        Assert.False(model.IsSmoothEnabled);
        Assert.Equal(1, service.Changes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedManualStopNeverOptimisticallyDiscardsTheConfirmedChoice(bool canceled)
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Disabled, KeepEnabled: true), Failure = canceled ? new OperationCanceledException() : new IOException("stop failed") };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        await model.SetSmoothModeAsync(false);
        Assert.True(model.IsSmoothEnabled);
        Assert.True(model.CanChange);
        Assert.Contains(canceled ? "取消" : "未完成", model.ErrorMessage);
    }

    [Fact]
    public async Task RestoredHealthyKeepStateClearsThePreviousRecoveryError()
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Disabled, Error: "自动保持暂未确认", KeepEnabled: true) };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        Assert.NotEmpty(model.ErrorMessage);
        service.State = new(MacAwdlState.Disabled, KeepEnabled: true);
        await model.RefreshAsync();
        Assert.Empty(model.ErrorMessage);
    }

    [Fact]
    public async Task CancelingPendingRefreshAndDisposingDoNotStopThePersistedMode()
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Disabled, KeepEnabled: true) };
        var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        service.WaitForCancellation = true;
        var pending = model.RefreshAsync();
        await model.CancelPendingAsync();
        await pending;
        Assert.True(model.IsSmoothEnabled);
        Assert.Equal(0, service.Changes);
        await model.DisposeAsync();
        Assert.True(model.IsSmoothEnabled);
        Assert.False(model.CanChange);
        Assert.Equal(0, service.Changes);
    }

    [Fact]
    public async Task CommittedStopWithFailedInterfaceRestoreCanBeRetriedWithoutChangingTheChoice()
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Disabled, KeepEnabled: true), Result = new(MacAwdlState.Disabled, Error: "互联恢复未完成", KeepEnabled: false) };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        await model.SetSmoothModeAsync(false);
        Assert.False(model.IsSmoothEnabled);
        Assert.True(model.NeedsRestoreRetry);
        Assert.True(model.CanRetryRestore);
        Assert.Equal("已关闭，互联尚未恢复", model.StatusMessage);
        service.Result = new(MacAwdlState.Enabled, KeepEnabled: false);
        await model.RetryRestoreAsync();
        Assert.False(model.IsSmoothEnabled);
        Assert.False(model.NeedsRestoreRetry);
        Assert.False(model.CanRetryRestore);
        Assert.Empty(model.ErrorMessage);
        Assert.Equal(2, service.Changes);
    }

    [Theory]
    [InlineData(MacAwdlState.Unknown)]
    [InlineData(MacAwdlState.Unavailable)]
    public async Task CommittedStopAllowsAnExplicitRestoreRetryWithoutReadableInterfaceState(MacAwdlState state)
    {
        var service = new FakeWirelessService { State = new(state, KeepEnabled: false), Result = new(MacAwdlState.Enabled, KeepEnabled: false) };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        Assert.False(model.CanChange);
        Assert.True(model.CanRetryRestore);
        await model.RetryRestoreAsync();
        Assert.Equal(1, service.Changes);
        Assert.False(model.NeedsRestoreRetry);
        Assert.Equal("已关闭", model.StatusMessage);
    }

    [Fact]
    public async Task RestoreRetrySharesTheAuthorizationGateAndIgnoresDuplicateClicks()
    {
        var completion = new TaskCompletionSource<MacWirelessSnapshot>();
        var service = new FakeWirelessService { State = new(MacAwdlState.Disabled, KeepEnabled: false), PendingChange = completion.Task };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        var retry = model.RetryRestoreAsync();
        Assert.True(model.IsBusy);
        Assert.False(model.CanRetryRestore);
        await model.RetryRestoreAsync();
        await model.SetSmoothModeAsync(true);
        Assert.Equal(1, service.Changes);
        completion.SetResult(new(MacAwdlState.Enabled, KeepEnabled: false));
        await retry;
        Assert.False(model.IsBusy);
        Assert.False(model.NeedsRestoreRetry);
    }

    [Theory]
    [InlineData(MacAwdlState.Disabled, true)]
    [InlineData(MacAwdlState.Enabled, true)]
    [InlineData(MacAwdlState.Enabled, false)]
    [InlineData(MacAwdlState.Disabled, null)]
    [InlineData(MacAwdlState.Enabled, null)]
    [InlineData(MacAwdlState.Unknown, null)]
    public async Task NormalAndLegacyStatesNeverOfferOrRunRestoreRetry(MacAwdlState state, bool? keep)
    {
        var service = new FakeWirelessService { State = new(state, KeepEnabled: keep) };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        Assert.False(model.NeedsRestoreRetry);
        Assert.False(model.CanRetryRestore);
        await model.RetryRestoreAsync();
        Assert.Equal(0, service.Changes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CanceledOrFailedRestoreRetryKeepsTheConfirmedOffChoiceAndOffersRetryAgain(bool canceled)
    {
        var service = new FakeWirelessService { State = new(MacAwdlState.Unknown, KeepEnabled: false), Failure = canceled ? new OperationCanceledException() : new IOException("restore failed") };
        await using var model = new MacWirelessViewModel(service);
        await model.RefreshAsync();
        await model.RetryRestoreAsync();
        Assert.False(model.IsSmoothEnabled);
        Assert.True(model.NeedsRestoreRetry);
        Assert.True(model.CanRetryRestore);
        Assert.Contains(canceled ? "取消" : "未完成", model.ErrorMessage);
        Assert.Equal(1, service.Changes);
    }

    private sealed class FakeWirelessService : IMacWirelessModeService
    {
        public MacWirelessSnapshot State = new(MacAwdlState.Unknown);
        public MacWirelessSnapshot? Result;
        public Task<MacWirelessSnapshot>? PendingChange;
        public Exception? Failure;
        public Exception? RefreshFailure;
        public bool WaitForCancellation;
        public bool WasCanceled;
        public int Changes;
        public async Task<MacWirelessSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (WaitForCancellation)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { WasCanceled = true; throw; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (RefreshFailure is not null) throw RefreshFailure;
            return State;
        }
        public async Task<MacWirelessSnapshot> SetSmoothModeAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            Changes++;
            if (Failure is not null) throw Failure;
            if (PendingChange is not null) return await PendingChange;
            return State = Result ?? new(enabled ? MacAwdlState.Disabled : MacAwdlState.Enabled);
        }
    }
}