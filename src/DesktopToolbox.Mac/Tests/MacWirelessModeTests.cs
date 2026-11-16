using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacWirelessModeTests
{
    private static MacWirelessCommandResult State(bool up) => new(0,
        $"awdl0: flags={(up ? "8863<UP," : "8862<")}BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500\n\tstatus: {(up ? "inactive" : "active")}\n");
    private static MacWirelessCommandResult Choice(bool enabled, string error = "") => new(0, Error: error, KeepEnabled: enabled);

    [Theory]
    [InlineData(true, MacAwdlState.Enabled)]
    [InlineData(false, MacAwdlState.Disabled)]
    public async Task RefreshReadsChoiceAndUpFlagWithoutChangingEither(bool up, MacAwdlState expected)
    {
        var runner = new ScriptedRunner(Choice(false), State(up));
        var snapshot = await new MacWirelessModeService(runner).RefreshAsync();
        Assert.Equal(expected, snapshot.State);
        Assert.False(snapshot.IsSmoothEnabled);
        Assert.Equal(false, snapshot.KeepEnabled);
        Assert.True(snapshot.CanChange);
        Assert.Equal([MacWirelessCommand.ReadKeepState, MacWirelessCommand.ReadState], runner.Commands);
    }

    [Fact]
    public async Task ASystemUpTransitionDoesNotClearTheEnabledChoiceOrAuthorizeAgain()
    {
        var runner = new ScriptedRunner(Choice(true), State(true));
        var snapshot = await new MacWirelessModeService(runner).RefreshAsync();
        Assert.Equal(MacAwdlState.Enabled, snapshot.State);
        Assert.True(snapshot.IsSmoothEnabled);
        Assert.Empty(snapshot.Error);
        Assert.Equal([MacWirelessCommand.ReadKeepState, MacWirelessCommand.ReadState], runner.Commands);
    }

    [Theory]
    [InlineData("", "", MacAwdlState.Unknown)]
    [InlineData("en1: flags=8863<UP,BROADCAST> mtu 1500", "", MacAwdlState.Unknown)]
    [InlineData("awdl0: status: active", "", MacAwdlState.Unknown)]
    [InlineData("", "ifconfig: interface awdl0 does not exist", MacAwdlState.Unavailable)]
    [InlineData("", "ifconfig: permission denied", MacAwdlState.Unknown)]
    public async Task UnknownInterfaceDoesNotInstallAnUnrequestedKeeper(string output, string error, MacAwdlState expected)
    {
        var runner = new ScriptedRunner(Choice(false), new(error.Length == 0 ? 0 : 1, output, error));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(true);
        Assert.Equal(expected, snapshot.State);
        Assert.False(snapshot.CanChange);
        Assert.Equal([MacWirelessCommand.ReadKeepState, MacWirelessCommand.ReadState], runner.Commands);
    }

    [Theory]
    [InlineData(true, 2, MacAwdlState.Disabled)]
    [InlineData(false, 3, MacAwdlState.Enabled)]
    public async Task SetReadsPersistentAndActualStateAfterAuthorization(bool smooth, int command, MacAwdlState expected)
    {
        var runner = new ScriptedRunner(Choice(!smooth), State(smooth), new(0), Choice(smooth), State(!smooth));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(smooth);
        Assert.Equal(expected, snapshot.State);
        Assert.Equal(smooth, snapshot.IsSmoothEnabled);
        Assert.Empty(snapshot.Error);
        Assert.Equal([MacWirelessCommand.ReadKeepState, MacWirelessCommand.ReadState, (MacWirelessCommand)command, MacWirelessCommand.ReadKeepState, MacWirelessCommand.ReadState], runner.Commands);
    }

    [Fact]
    public async Task AnAlreadyDownInterfaceStillRequiresInstallingTheKeeper()
    {
        var runner = new ScriptedRunner(Choice(false), State(false), new(0), Choice(true), State(false));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(true);
        Assert.True(snapshot.IsSmoothEnabled);
        Assert.Single(runner.Commands, command => command == MacWirelessCommand.EnableKeep);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AlreadyAtTheHealthyChoiceDoesNotPromptAgain(bool smooth)
    {
        var runner = new ScriptedRunner(Choice(smooth), State(!smooth));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(smooth);
        Assert.Equal(smooth, snapshot.IsSmoothEnabled);
        Assert.Empty(snapshot.Error);
        Assert.Equal([MacWirelessCommand.ReadKeepState, MacWirelessCommand.ReadState], runner.Commands);
    }

    [Fact]
    public async Task SuccessWithoutThePersistedChoiceIsNotSuccess()
    {
        var runner = new ScriptedRunner(Choice(false), State(true), new(0), Choice(false), State(false));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(true);
        Assert.False(snapshot.IsSmoothEnabled);
        Assert.NotEmpty(snapshot.Error);
    }

    [Fact]
    public async Task DisablingStillWorksWhenTheInterfaceReadFails()
    {
        var runner = new ScriptedRunner(Choice(true), new(1, Error: "permission denied"), new(0), Choice(false), State(true));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(false);
        Assert.Equal(false, snapshot.KeepEnabled);
        Assert.Equal(MacAwdlState.Enabled, snapshot.State);
        Assert.Single(runner.Commands, command => command == MacWirelessCommand.DisableKeep);
    }

    [Fact]
    public async Task DisablingCanAttemptRecoveryWhenThePolicyReadIsUnavailable()
    {
        var runner = new ScriptedRunner(new(-1, Error: "policy unavailable"), new(1), new(0), Choice(false), State(true));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(false);
        Assert.Equal(false, snapshot.KeepEnabled);
        Assert.Single(runner.Commands, command => command == MacWirelessCommand.DisableKeep);
    }

    [Theory]
    [InlineData(-128, "用户已取消。 (-128)", "取消")]
    [InlineData(1, "execution error: User interaction is not allowed. (-60007)", "授权")]
    [InlineData(1, "execution error: permission denied", "失败")]
    public async Task AuthorizationErrorsAreVisibleAndStillReadBack(int code, string error, string expected)
    {
        var runner = new ScriptedRunner(Choice(false), State(true), new(code, Error: error), Choice(false), State(true));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(true);
        Assert.Equal(MacAwdlState.Enabled, snapshot.State);
        Assert.Equal(false, snapshot.KeepEnabled);
        Assert.Contains(expected, snapshot.Error);
        Assert.Equal(5, runner.Commands.Count);
    }

    [Fact]
    public async Task AnUnknownActualStateDoesNotEraseAnInstalledChoice()
    {
        var runner = new ScriptedRunner(Choice(false), State(true), new(0), Choice(true), new(1, Error: "permission denied"));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(true);
        Assert.Equal(MacAwdlState.Unknown, snapshot.State);
        Assert.True(snapshot.IsSmoothEnabled);
        Assert.True(snapshot.CanChange);
        Assert.NotEmpty(snapshot.Error);
    }

    [Theory]
    [InlineData(true, false, "超时")]
    [InlineData(false, true, "取消")]
    public async Task InterruptedAuthorizationReportsTheActualPersistedOutcome(bool timeout, bool cancelled, string expected)
    {
        var runner = new ScriptedRunner(Choice(false), State(true), new(-1, TimedOut: timeout, Cancelled: cancelled), Choice(true), State(false));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(true);
        Assert.True(snapshot.IsSmoothEnabled);
        Assert.Contains(expected, snapshot.Error);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotStartCommands()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var runner = new ScriptedRunner();
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(true, stop.Token);
        Assert.Equal(MacAwdlState.Unknown, snapshot.State);
        Assert.Contains("取消", snapshot.Error);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task RepeatedClicksAreSerializedAndRecheckTheSavedChoice()
    {
        var runner = new StatefulRunner();
        var service = new MacWirelessModeService(runner);
        var results = await Task.WhenAll(service.SetSmoothModeAsync(true), service.SetSmoothModeAsync(true));
        Assert.All(results, result => Assert.True(result.IsSmoothEnabled));
        Assert.Equal(1, runner.Mutations);
        Assert.Equal(1, runner.MaxConcurrent);
    }

    [Fact]
    public async Task ANewServiceInstanceReadsTheSavedChoiceRatherThanInferringFromAwdl()
    {
        var runner = new StatefulRunner();
        await new MacWirelessModeService(runner).SetSmoothModeAsync(true);
        runner.RestoreAwdl();
        var snapshot = await new MacWirelessModeService(runner).RefreshAsync();
        Assert.True(snapshot.IsSmoothEnabled);
        Assert.Equal(MacAwdlState.Enabled, snapshot.State);
        Assert.Equal(1, runner.Mutations);
    }

    [Fact]
    public async Task ARegisteredButUnhealthyKeeperIsReportedWithoutPrompting()
    {
        var runner = new ScriptedRunner(Choice(true, "自动保持未运行，请关闭后重新开启。"), State(true));
        var snapshot = await new MacWirelessModeService(runner).RefreshAsync();
        Assert.True(snapshot.IsSmoothEnabled);
        Assert.True(snapshot.CanChange);
        Assert.NotEmpty(snapshot.Error);
        Assert.Equal(2, runner.Commands.Count);
    }

    [Fact]
    public async Task AnUnknownPolicyIsNotSilentlyTreatedAsAnOffChoice()
    {
        var runner = new ScriptedRunner(new(-1, Error: "读取保持状态失败"), State(true));
        var snapshot = await new MacWirelessModeService(runner).RefreshAsync();
        Assert.Null(snapshot.KeepEnabled);
        Assert.NotEmpty(snapshot.Error);
        Assert.Equal(2, runner.Commands.Count);
    }

    [Fact]
    public void ReadUsesOnlyTheFixedInterfaceAndUnknownOperationsAreRejected()
    {
        var read = MacWirelessCommandRunner.CreateStartInfo(MacWirelessCommand.ReadState);
        Assert.Equal("/sbin/ifconfig", read.FileName);
        Assert.Equal(["awdl0"], read.ArgumentList);
        Assert.Throws<ArgumentOutOfRangeException>(() => MacWirelessCommandRunner.CreateStartInfo((MacWirelessCommand)999));
    }

    [Fact]
    public async Task UnfinishedAuthorizationKeepsItsRecoveryMessage()
    {
        var runner = new ScriptedRunner(Choice(false), State(true), new(-1, TimedOut: true, AuthorizationPending: true), Choice(false), State(true));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(true);
        Assert.Equal(false, snapshot.KeepEnabled);
        Assert.Contains("尚未结束", snapshot.Error);
    }

    [Fact]
    public async Task OppositeRequestsAreSerializedAndFinishWithTheExplicitOffChoice()
    {
        var runner = new StatefulRunner();
        var service = new MacWirelessModeService(runner);
        await Task.WhenAll(service.SetSmoothModeAsync(true), service.SetSmoothModeAsync(false));
        var final = await service.RefreshAsync();
        Assert.Equal(false, final.KeepEnabled);
        Assert.Equal(MacAwdlState.Enabled, final.State);
        Assert.Equal(2, runner.Mutations);
        Assert.Equal(1, runner.MaxConcurrent);
    }

    [Fact]
    public async Task AFailedDisableDoesNotPretendThatTheKeeperStopped()
    {
        var runner = new ScriptedRunner(Choice(true), State(false), new(1, Error: "bootout failed"), Choice(true), State(false));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(false);
        Assert.True(snapshot.IsSmoothEnabled);
        Assert.NotEmpty(snapshot.Error);
    }

    [Fact]
    public async Task ACanceledDisableThatCommittedStillReportsTheActualOffChoice()
    {
        var runner = new ScriptedRunner(Choice(true), State(false), new(-1, Cancelled: true), Choice(false), State(true));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(false);
        Assert.Equal(false, snapshot.KeepEnabled);
        Assert.Equal(MacAwdlState.Enabled, snapshot.State);
        Assert.Contains("取消", snapshot.Error);
    }

    [Fact]
    public async Task AnUnknownPolicyCannotAuthorizeAnEnableBasedOnlyOnInterfaceFlags()
    {
        var runner = new ScriptedRunner(new(-1, Error: "policy read failed"), State(false));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(true);
        Assert.Null(snapshot.KeepEnabled);
        Assert.NotEmpty(snapshot.Error);
        Assert.Equal(2, runner.Commands.Count);
    }

    [Fact]
    public async Task AnOffChoiceWithAStillPausedInterfaceCanExplicitlyRestoreIt()
    {
        var runner = new ScriptedRunner(Choice(false), State(false), new(0), Choice(false), State(true));
        var snapshot = await new MacWirelessModeService(runner).SetSmoothModeAsync(false);
        Assert.Equal(false, snapshot.KeepEnabled);
        Assert.Equal(MacAwdlState.Enabled, snapshot.State);
        Assert.Single(runner.Commands, command => command == MacWirelessCommand.DisableKeep);
    }

    private sealed class ScriptedRunner(params MacWirelessCommandResult[] results) : IMacWirelessCommandRunner
    {
        private readonly Queue<MacWirelessCommandResult> replies = new(results);
        public List<MacWirelessCommand> Commands { get; } = [];
        public Task<MacWirelessCommandResult> RunAsync(MacWirelessCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(replies.Dequeue());
        }
    }

    private sealed class StatefulRunner : IMacWirelessCommandRunner
    {
        private bool up = true;
        private bool keep;
        private int concurrent;
        public int Mutations { get; private set; }
        public int MaxConcurrent { get; private set; }
        public void RestoreAwdl() => up = true;
        public async Task<MacWirelessCommandResult> RunAsync(MacWirelessCommand command, CancellationToken token)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref concurrent));
            try
            {
                await Task.Delay(10, token);
                if (command == MacWirelessCommand.ReadState) return State(up);
                if (command == MacWirelessCommand.ReadKeepState) return Choice(keep);
                Mutations++; keep = command == MacWirelessCommand.EnableKeep; up = !keep;
                return new(0);
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }
    }
}
