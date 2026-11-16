using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.ViewModels;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacLinkViewModelTests
{
    [Fact]
    public async Task ARunningClientIsNotReportedReadyWithoutAuthenticatedControlAndConnection()
    {
        var service = new FakeService { State = new() { IsRunning = true, IsConnected = true } };
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        Assert.False(model.CanSwitch);
        service.Publish(service.State with { ControlConnected = true });
        Assert.True(model.CanSwitch);
    }

    [Fact]
    public async Task MissingWindowsAddressDoesNotStartTheCore()
    {
        var service = new FakeService();
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        await model.StartAsync();
        Assert.Equal(0, service.Starts);
        Assert.Contains("Windows", model.ErrorMessage);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task PairingImportRequiresSeparateUserConfirmationBeforeItConnects()
    {
        var service = new FakeService();
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        await model.PreviewPairingAsync("candidate");
        Assert.NotNull(model.PendingPairing);
        Assert.Equal(0, service.PairingsAccepted);
        model.RejectPairing();
        Assert.Null(model.PendingPairing);
        Assert.Equal(0, service.PairingsAccepted);
    }

    [Fact]
    public async Task ConfirmingPairingUsesTheExactPreviewAndUpdatesDeviceNames()
    {
        var service = new FakeService();
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        await model.PreviewPairingAsync("candidate");
        await model.AcceptPairingAsync();
        Assert.Equal(1, service.PairingsAccepted);
        Assert.Equal("Office-Win", model.WindowsName);
        Assert.Equal("Studio-Mac", model.MacName);
        Assert.Equal("192.0.2.101", model.WindowsHost);
        Assert.Null(model.PendingPairing);
    }

    [Fact]
    public async Task PairingApprovalFailureRemainsVisibleOnTheConfirmationCardAfterBackgroundSnapshots()
    {
        var service = new FakeService { AcceptFailure = new InvalidOperationException("本机确认未通过，请重新使用真实点击。") };
        using var model = new MacLinkViewModel(service);
        await model.InitializeAsync(); await model.PreviewPairingAsync("candidate");
        var preview = model.PendingPairing;
        await model.AcceptPairingAsync();
        Assert.Equal(service.AcceptFailure.Message, model.PairingErrorMessage);
        Assert.NotEmpty(model.PairingStatusMessage);
        for (var index = 0; index < 3; index++) service.Publish(new() { StatusMessage = "原生共享已停止。" });
        Assert.Equal(service.AcceptFailure.Message, model.PairingErrorMessage);
        Assert.Same(preview, model.PendingPairing);
        Assert.False(model.IsBusy); Assert.Equal(0, service.Starts);
    }

    [Fact]
    public async Task PairingApprovalShowsProgressWhileTheServiceWaitsAndKeepsItAcrossBackgroundSnapshots()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService { PairingCompletion = completion };
        using var model = new MacLinkViewModel(service);
        await model.InitializeAsync(); await model.PreviewPairingAsync("candidate");
        var progressNotified = false;
        model.PropertyChanged += (_, _) =>
        {
            if (model.IsBusy && model.PairingStatusMessage.Contains("正在", StringComparison.Ordinal)) progressNotified = true;
        };
        var accepting = model.AcceptPairingAsync();
        try
        {
            Assert.True(model.IsBusy); Assert.False(accepting.IsCompleted);
            Assert.True(progressNotified, "The waiting progress must be observable before the service completes.");
            Assert.Contains("正在", model.PairingStatusMessage); Assert.Contains("配对", model.PairingStatusMessage);
            Assert.Empty(model.PairingErrorMessage);
            var progress = model.PairingStatusMessage;
            for (var index = 0; index < 3; index++) service.Publish(new() { StatusMessage = "原生共享已停止。" });
            Assert.Equal(progress, model.PairingStatusMessage); Assert.Empty(model.PairingErrorMessage);
            Assert.NotNull(model.PendingPairing);
        }
        finally { completion.TrySetResult(true); await accepting; }
        Assert.False(model.IsBusy); Assert.Null(model.PendingPairing);
        Assert.Empty(model.PairingErrorMessage); Assert.Contains("已确认", model.PairingStatusMessage);
    }

    [Fact]
    public async Task PairingPreviewFeedbackSurvivesPollingAndChangesOnlyWithTheNextPairingAction()
    {
        var service = new FakeService { PreviewFailure = new IOException("配对文件内容无效。") };
        using var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        Assert.Empty(model.PairingStatusMessage); Assert.Empty(model.PairingErrorMessage);
        await model.PreviewPairingAsync("invalid");
        Assert.Equal(service.PreviewFailure.Message, model.PairingErrorMessage); Assert.Null(model.PendingPairing);
        service.Publish(new());
        Assert.Equal(service.PreviewFailure.Message, model.PairingErrorMessage);
        service.PreviewFailure = null;
        await model.PreviewPairingAsync("candidate");
        Assert.Empty(model.PairingErrorMessage); Assert.Contains("核对", model.PairingStatusMessage);
        var success = model.PairingStatusMessage;
        service.Publish(new());
        Assert.Equal(success, model.PairingStatusMessage); Assert.NotNull(model.PendingPairing);
        Assert.Equal(0, service.PairingsAccepted); Assert.Equal(0, service.Starts);
        model.RejectPairing();
        Assert.Null(model.PendingPairing); Assert.Empty(model.PairingErrorMessage);
        Assert.Contains("未接受", model.PairingStatusMessage);
        var rejected = model.PairingStatusMessage;
        service.Publish(new()); Assert.Equal(rejected, model.PairingStatusMessage);
    }

    [Fact]
    public async Task FinalPairingApprovalStillReachesTheServiceInsideTheCallersSynchronousClickContext()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clickIsCurrent = false; var called = false; var sawCurrentClick = false;
        var service = new FakeService
        {
            PairingCompletion = completion,
            OnAcceptPairing = () => { called = true; sawCurrentClick = clickIsCurrent; }
        };
        using var model = new MacLinkViewModel(service);
        await model.InitializeAsync(); await model.PreviewPairingAsync("candidate");
        clickIsCurrent = true;
        var accepting = model.AcceptPairingAsync();
        clickIsCurrent = false;
        try { Assert.True(called); Assert.True(sawCurrentClick); }
        finally { completion.TrySetResult(true); await accepting; }
        Assert.Null(model.PendingPairing); Assert.Equal(1, service.PairingsAccepted);
        Assert.Empty(model.PairingErrorMessage); Assert.Contains("已确认", model.PairingStatusMessage);
    }

    [Fact]
    public async Task DisconnectedOrUnknownTargetCannotRequestACombinedSwitch()
    {
        var service = new FakeService();
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        await model.SwitchAsync("My-Mac");
        Assert.Equal(0, service.Switches);
        service.Publish(new() { IsRunning = true, IsConnected = true, ControlConnected = true });
        await model.SwitchAsync("arbitrary-command");
        Assert.Equal(0, service.Switches);
    }

    [Fact]
    public async Task APartialVideoFailureNeverSaysEverythingWasSwitchedSuccessfully()
    {
        var service = new FakeService { State = new() { IsRunning = true, IsConnected = true, ControlConnected = true }, Outcome = new(false, "My-Mac", true, "failed", "显示器未接受切源") };
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        await model.SwitchAsync("My-Mac");
        Assert.Contains("显示器", model.ErrorMessage);
        Assert.DoesNotContain("全部成功", model.StatusMessage);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task StartFailureLeavesControlsUsableAndShowsTheActualError()
    {
        var service = new FakeService { StartFailure = new IOException("找不到原版核心") };
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        model.WindowsHost = "192.0.2.101";
        await model.StartAsync();
        Assert.Contains("找不到原版核心", model.ErrorMessage);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task FingerprintApprovalIsExplicitAndUsesThePendingFingerprint()
    {
        var service = new FakeService { State = new() { PendingPeerFingerprint = "SHA256:ABC" } };
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        Assert.Null(service.PeerDecision);
        await model.ConfirmPeerAsync(true);
        Assert.Equal(("SHA256:ABC", true), service.PeerDecision);
    }

    [Fact]
    public async Task AcceptedButUnverifiedVideoIsShownAsPartialInsteadOfFailedOrVerified()
    {
        var service = new FakeService { State = new() { IsRunning = true, IsConnected = true, ControlConnected = true }, Outcome = new(false, "My-Mac", true, "acceptedUnverified", "") };
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        await model.SwitchAsync("My-Mac");
        Assert.Equal("", model.ErrorMessage);
        Assert.Contains("待", model.StatusMessage);
        Assert.Contains("接受", model.StatusMessage);
    }

    [Fact]
    public async Task DisabledVideoDoesNotClaimADisplayCommandWasSent()
    {
        var service = new FakeService { State = new() { IsRunning = true, IsConnected = true, ControlConnected = true }, Outcome = new(true, "My-Mac", true, "disabled", "") };
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        await model.SwitchAsync("My-Mac");
        Assert.Contains("关闭", model.StatusMessage);
        Assert.DoesNotContain("命令已接受", model.StatusMessage);
    }

    [Fact]
    public async Task RefreshKeepsAnUnverifiedDisplayOutcomeVisibleUntilAnotherOperation()
    {
        var service = new FakeService { State = new() { IsRunning = true, IsConnected = true, ControlConnected = true }, Outcome = new(false, "My-Mac", true, "acceptedUnverified", "") };
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        await model.SwitchAsync("My-Mac");
        await model.RefreshAsync();
        Assert.Contains("接受", model.StatusMessage);
        Assert.Contains("待", model.StatusMessage);
        Assert.Equal("", model.ErrorMessage);
    }

    [Fact]
    public void ApprovedPairingCannotControlAWindowsHostDifferentFromTheInputConnection()
    {
        var settings = new MacConnectionSettings { WindowsHost = "192.0.2.101" };
        settings.ValidateApprovedPairing("192.0.2.101", "Win-PC", "My-Mac");
        Assert.Throws<InvalidOperationException>(() => settings.ValidateApprovedPairing("192.0.2.102", "Win-PC", "My-Mac"));
        Assert.Throws<InvalidOperationException>(() => settings.ValidateApprovedPairing("192.0.2.101", "Other-Win", "My-Mac"));
        Assert.Throws<InvalidOperationException>(() => settings.ValidateApprovedPairing("192.0.2.101", "Win-PC", "Other-Mac"));
    }

    [Fact]
    public async Task ADisconnectedMacClientCanStillRecoverToConfirmedWindowsButCannotSwitchToMac()
    {
        var service = new FakeService { State = new() { ControlConnected = true }, Outcome = new(false, "Win-PC", true, "acceptedUnverified", "") };
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        Assert.True(model.CanReturnToWindows);
        Assert.False(model.CanSwitch);
        await model.SwitchAsync("Win-PC");
        Assert.Equal(1, service.Switches);
        await model.SwitchAsync("My-Mac");
        Assert.Equal(1, service.Switches);
    }

    [Fact]
    public async Task PendingDisplayCleanupKeepsStopAvailableAfterTheInputCoreStopped()
    {
        var service = new FakeService { State = new() { HasPendingDisplayCleanup = true, ControlConnected = true } };
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        Assert.True(model.CanStop); Assert.False(model.CanStart);
        Assert.False(model.CanEditConnection); Assert.False(model.CanReturnToWindows);
        await model.StopAsync();
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task AHealthyConnectionKeepsTheSmallWindowWhileSwitchButtonsAreBusy()
    {
        var service = new FakeService { State = new() { IsRunning = true, IsConnected = true, ControlConnected = true } };
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        var sawBusy = false;
        model.PropertyChanged += (_, _) =>
        {
            if (!model.IsBusy) return;
            sawBusy = true;
            Assert.False(model.CanSwitch);
            Assert.True(model.CanUseCompactView);
        };
        await model.SwitchAsync("My-Mac");
        Assert.True(sawBusy);
        Assert.True(model.CanUseCompactView);
    }

    [Fact]
    public async Task AVideoFailureReopensDetailsInsteadOfHidingTheErrorBehindTwoButtons()
    {
        var service = new FakeService
        {
            State = new() { IsRunning = true, IsConnected = true, ControlConnected = true },
            Outcome = new(false, "Win-PC", true, "failed", "显示器没有完成切换")
        };
        var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        Assert.True(model.CanUseCompactView);
        await model.SwitchAsync("Win-PC");
        Assert.False(model.CanUseCompactView);
        Assert.Contains("显示器", model.ErrorMessage);
    }
    [Fact]
    public async Task APollingDisplayFailureReopensTheWindowWithoutAMacButtonOutcome()
    {
        var connected = new MacLinkSnapshot { IsRunning = true, IsConnected = true, ControlConnected = true };
        var service = new FakeService { State = connected };
        using var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        Assert.True(model.CanUseCompactView);
        service.Publish(connected with { HasDisplayRecoveryError = true, DisplayRecoveryMessage = "Mac 显示命令未能确认" });
        Assert.Empty(model.ErrorMessage);
        Assert.False(model.CanUseCompactView);
        service.Publish(connected with { DisplayRecoveryMessage = "Mac 已接受回切命令，画面待确认" });
        Assert.True(model.CanUseCompactView);
    }

    [Theory]
    [InlineData("Studio-Mac", "● 正在控制 Mac")]
    [InlineData("Office-Windows", "● 正在控制 Windows")]
    [InlineData("unknown-peer", "控制目标待确认")]
    public async Task CurrentTargetUsesAuthenticatedCustomDeviceNamesAndShortHumanLabels(string activeComputer, string expected)
    {
        var service = new FakeService { State = new()
        {
            Settings = new() { WindowsName = "Office-Windows", MacName = "Studio-Mac" },
            IsRunning = true, IsConnected = true, ControlConnected = true, ActiveComputer = activeComputer
        } };
        using var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        model.WindowsName = "Unsaved-Windows"; model.MacName = "Unsaved-Mac";
        Assert.Equal(expected, model.ActiveComputerLabel);
        Assert.Equal(activeComputer == "Office-Windows", model.IsWindowsActive);
        Assert.Equal(activeComputer == "Studio-Mac", model.IsMacActive);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task AStaleTargetIsNotShownAsCurrentlyControlledWhenARequiredChannelIsGone(bool running, bool inputConnected, bool controlConnected)
    {
        var service = new FakeService { State = new()
        {
            Settings = new() { WindowsName = "Office-Windows", MacName = "Studio-Mac" },
            IsRunning = running, IsConnected = inputConnected, ControlConnected = controlConnected, ActiveComputer = "Studio-Mac"
        } };
        using var model = new MacLinkViewModel(service);
        await model.InitializeAsync();
        Assert.Equal("控制目标待确认", model.ActiveComputerLabel);
        Assert.False(model.IsWindowsActive); Assert.False(model.IsMacActive);
    }

    private sealed class FakeService : IMacLinkService
    {
        public MacLinkSnapshot State { get; set; } = new();
        public MacSwitchOutcome Outcome { get; set; } = new(true, "My-Mac", true, "acceptedUnverified", "");
        public Exception? StartFailure { get; init; }
        public Exception? PreviewFailure { get; set; }
        public Exception? AcceptFailure { get; set; }
        public TaskCompletionSource<bool>? PairingCompletion { get; init; }
        public Action? OnAcceptPairing { get; init; }
        public int Starts { get; private set; }
        public int Switches { get; private set; }
        public int PairingsAccepted { get; private set; }
        public (string, bool)? PeerDecision { get; private set; }
        public event Action<MacLinkSnapshot>? Changed;
        public void Publish(MacLinkSnapshot state) { State = state; Changed?.Invoke(state); }
        public Task<MacLinkSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task StartAsync(MacConnectionSettings settings, CancellationToken cancellationToken = default) { Starts++; if (StartFailure is not null) throw StartFailure; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConfirmPeerAsync(string fingerprint, bool accept, CancellationToken cancellationToken = default) { PeerDecision = (fingerprint, accept); return Task.CompletedTask; }
        public Task<PairingPreview> PreviewPairingAsync(string json, CancellationToken cancellationToken = default) => PreviewFailure is { } failure
            ? Task.FromException<PairingPreview>(failure) : Task.FromResult(new PairingPreview("preview-1", "192.0.2.101", 24801, "Office-Win", "Studio-Mac", "AABB"));
        public Task AcceptPairingAsync(string previewId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("preview-1", previewId); OnAcceptPairing?.Invoke(); PairingsAccepted++;
            if (AcceptFailure is { } failure) return Task.FromException(failure);
            return PairingCompletion is { } completion ? completion.Task : Task.CompletedTask;
        }
        public Task<MacSwitchOutcome> SwitchAsync(string target, CancellationToken cancellationToken = default) { Switches++; return Task.FromResult(Outcome); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
