using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class MultiPeerPreviewManagerTests
{
    [Fact]
    public async Task ShutdownKeepsTheSessionAliveWhileDisplayOperationIsUnresolved()
    {
        var keyboard = new Keyboard();
        var topology = MultiScreenTopology.Create("host", ["one"],
            [new MultiScreenDefinition("shared", false, [new("host", "h", 15), new("one", "m", 16)]) { AutoSwitchEnabled = true }]);
        var screens = new MultiScreenDisplayState(topology);
        screens.ConfirmVisible("shared", "host");
        var router = new PendingScreenRouter();
        var display = new MultiScreenDisplayCoordinator(topology, screens, keyboard.GetStateAsync, router, TimeSpan.FromMilliseconds(20));
        var manager = new MultiPeerPreviewManager("host", ["one"], keyboard, display: display);
        Assert.Equal("acceptedUnverified", (await manager.SwitchNormalAsync("host", "one")).VideoStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.DisposeAsync());
        Assert.NotNull(screens.Pending);
        router.Done.SetResult(new("verified"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (screens.Pending is not null) await Task.Delay(10, timeout.Token);
        await manager.DisposeAsync();
    }

    private sealed class PendingScreenRouter : IMultiScreenDisplayRouter
    {
        public TaskCompletionSource<DisplaySwitchOutcome> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DisplaySwitchOutcome> ExecuteAsync(MultiScreenPendingSwitch command) => Done.Task;
    }

    [Fact]
    public async Task ThreeComputerLayoutKeepsPreviewAndLaptopFromSwitchingTheSharedMonitor()
    {
        var keyboard = new Keyboard();
        var shared = new MultiScreenDefinition("shared27", false,
            [new("host", "host-monitor", 15), new("one", "mac-monitor", 16)]) { AutoSwitchEnabled = true };
        var topology = MultiScreenTopology.Create("host", ["one", "two"],
            [shared, new("mac16", false, [new("one", "mac-small", 15)]), new("laptop", true, [new("two", null, null)])]);
        var screens = new MultiScreenDisplayState(topology);
        screens.ConfirmVisible("shared27", "host"); screens.ConfirmVisible("mac16", "one");
        var router = new ScreenRouter();
        var display = new MultiScreenDisplayCoordinator(topology, screens, keyboard.GetStateAsync, router, TimeSpan.FromSeconds(1));
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard, display: display);
        var preview = await manager.BeginAsync("host", "one");
        Assert.True(preview.IsActive);
        await manager.OnFocusChangedAsync(new("one", (await keyboard.GetStateAsync(default)).Revision));
        await manager.EndAsync("host", preview.Id!);
        await manager.OnFocusChangedAsync(new("host", (await keyboard.GetStateAsync(default)).Revision));
        Assert.Empty(router.Commands);
        var laptop = await manager.SwitchNormalAsync("host", "two");
        Assert.True(laptop.InputTransferred);
        Assert.Equal("disabled", laptop.VideoStatus);
        Assert.Empty(router.Commands);
        var mac = await manager.SwitchNormalAsync("host", "one");
        Assert.True(mac.InputTransferred);
        Assert.Equal("acceptedUnverified", mac.VideoStatus);
        Assert.Equal("shared27", Assert.Single(router.Commands).Plan.ScreenId);
        Assert.Equal("one", screens.VisibleDevice("mac16"));
    }

    private sealed class ScreenRouter : IMultiScreenDisplayRouter
    {
        public List<MultiScreenPendingSwitch> Commands { get; } = [];
        public Task<DisplaySwitchOutcome> ExecuteAsync(MultiScreenPendingSwitch command)
        { Commands.Add(command); return Task.FromResult(new DisplaySwitchOutcome("acceptedUnverified")); }
    }

    [Fact]
    public async Task OnlyOnePreviewCanOwnKeyboardAndOnlyItsDevicesCanReturnIt()
    {
        var keyboard = new Keyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard);
        var first = await manager.BeginAsync("host", "one");
        Assert.True(first.IsActive);
        Assert.Equal("one", keyboard.Active);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.BeginAsync("host", "two"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.EndAsync("two", first.Id!));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.PrepareNormalSwitchAsync("two"));
        Assert.False((await manager.ReadForDeviceAsync("two")).HasSession);
        var returned = await manager.EndAsync("one", first.Id!);
        Assert.False(returned.HasSession);
        Assert.Equal("host", keyboard.Active);
        var next = await manager.BeginAsync("host", "two");
        Assert.True(next.IsActive);
        Assert.Equal("two", keyboard.Active);
        await manager.EndAsync("host", next.Id!);
    }

    [Fact]
    public async Task ExpiredPreviewReturnsKeyboardToViewer()
    {
        var clock = new Clock();
        var keyboard = new Keyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard, clock);
        var preview = await manager.BeginAsync("host", "one");
        Assert.True(preview.IsActive);
        clock.Advance(TimeSpan.FromSeconds(9));
        var state = await manager.ReadAsync();
        Assert.False(state.HasSession);
        Assert.Equal("host", keyboard.Active);
    }

    [Fact]
    public async Task StaleEndDoesNotExpireOrClaimReturnOfAnotherSession()
    {
        var clock = new Clock(); var keyboard = new Keyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard, clock);
        var old = await manager.BeginAsync("host", "one");
        await manager.EndAsync("host", old.Id!);
        var next = await manager.BeginAsync("host", "one");
        Assert.True(next.IsActive); Assert.NotEqual(old.Id, next.Id);
        clock.Advance(TimeSpan.FromSeconds(9));
        var switches = keyboard.SwitchCalls;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EndAsync("one", old.Id!));
        Assert.Equal("one", keyboard.Active);
        Assert.Equal(switches, keyboard.SwitchCalls);
        Assert.False((await manager.EndAsync("one", next.Id!)).HasSession);
        Assert.Equal("host", keyboard.Active);
    }

    [Fact]
    public async Task UnauthorizedEndDoesNotExpireAnotherDevicesSession()
    {
        var clock = new Clock(); var keyboard = new Keyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard, clock);
        var active = await manager.BeginAsync("host", "one");
        clock.Advance(TimeSpan.FromSeconds(9));
        var switches = keyboard.SwitchCalls;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.EndAsync("two", active.Id!));
        Assert.Equal("one", keyboard.Active);
        Assert.Equal(switches, keyboard.SwitchCalls);
        Assert.False((await manager.EndAsync("one", active.Id!)).HasSession);
    }

    [Fact]
    public async Task ValidExpiredEndAndRepeatedEndRemainCompatibleIncludingUppercaseId()
    {
        var clock = new Clock(); var keyboard = new Keyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one"], keyboard, clock);
        var active = await manager.BeginAsync("host", "one");
        clock.Advance(TimeSpan.FromSeconds(9));
        var upper = active.Id!.ToUpperInvariant();
        Assert.False((await manager.EndAsync("one", upper)).HasSession);
        Assert.Equal("host", keyboard.Active);
        Assert.False((await manager.EndAsync("one", upper)).HasSession);
    }

    [Fact]
    public async Task StaleEndQueuedBehindNewBeginCannotReturnTheNewSession()
    {
        var keyboard = new BlockingKeyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard);
        var begin = manager.BeginAsync("host", "one");
        await keyboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var staleEnd = manager.EndAsync("one", Guid.NewGuid().ToString("N"));
        Assert.False(staleEnd.IsCompleted);
        keyboard.Resume.TrySetResult();
        var active = await begin;
        Assert.True(active.IsActive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => staleEnd);
        Assert.Equal("one", keyboard.Active);
        await manager.EndAsync("one", active.Id!);
    }

    [Fact]
    public async Task OnlyTheViewerCanRenewAndHeartbeatExtendsTheLease()
    {
        var clock = new Clock();
        var keyboard = new Keyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard, clock);
        var preview = await manager.BeginAsync("host", "one");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.RenewAsync("one", preview.Id!));
        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.True((await manager.RenewAsync("host", preview.Id!)).IsActive);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await manager.ReadAsync()).IsActive);
        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.False((await manager.ReadAsync()).HasSession);
        Assert.Equal("host", keyboard.Active);
    }

    [Fact]
    public async Task AManualSwitchToAnotherDeviceIsNeverOverriddenByPreviewReturn()
    {
        var keyboard = new Keyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard);
        var preview = await manager.BeginAsync("host", "one");
        keyboard.MoveExternally("two");
        var result = await manager.EndAsync("host", preview.Id!);
        Assert.Equal(DeskflowPreviewPhase.Recovery, result.Phase);
        Assert.Equal("two", keyboard.Active);
        keyboard.MoveExternally("one");
        result = await manager.EndAsync("host", preview.Id!);
        Assert.False(result.HasSession);
        Assert.Equal("host", keyboard.Active);
    }

    [Fact]
    public async Task PreviewFromAnotherComputerExplainsHowToMoveKeyboardWithoutTakingIt()
    {
        var keyboard = new Keyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one"], keyboard);
        var result = await manager.BeginAsync("one", "one");
        Assert.False(result.HasSession);
        Assert.Contains("先把共享键鼠切到当前电脑", result.Message);
        Assert.Equal("host", keyboard.Active);
    }

    [Fact]
    public async Task RemoteViewerReturnsToItsOwnDeviceWithoutUsingFirstPeerShortcut()
    {
        var keyboard = new Keyboard(); keyboard.MoveExternally("two");
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard);
        var preview = await manager.BeginAsync("two", "two");
        Assert.True(preview.IsActive);
        Assert.Equal("host", keyboard.Active);
        Assert.Equal("请使用归还按钮", preview.ReturnShortcut);
        var result = await manager.EndAsync("host", preview.Id!);
        Assert.False(result.HasSession);
        Assert.Equal("two", keyboard.Active);
    }

    [Fact]
    public async Task AConcurrentNormalSwitchCannotRacePastPreviewAdmission()
    {
        var keyboard = new BlockingKeyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard);
        var normal = manager.SwitchNormalAsync("host", "two");
        await keyboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var preview = manager.BeginAsync("host", "one");
        keyboard.Resume.TrySetResult();
        Assert.True((await normal).InputTransferred);
        Assert.False((await preview).HasSession);
        Assert.Equal("two", keyboard.Active);
    }

    [Fact]
    public async Task DisplayIgnoresPreviewAndDelayedFocusButFollowsANewNormalSwitch()
    {
        var keyboard = new Keyboard(); var display = new Display();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard, display: display);
        var preview = await manager.BeginAsync("host", "one");
        var previewRevision = (await keyboard.GetStateAsync(default)).Revision;
        await manager.OnFocusChangedAsync(new("one", previewRevision));
        Assert.Empty(display.Targets);
        await manager.EndAsync("host", preview.Id!);
        var returnRevision = (await keyboard.GetStateAsync(default)).Revision;
        await manager.OnFocusChangedAsync(new("host", returnRevision));
        Assert.Empty(display.Targets);
        Assert.True((await manager.SwitchNormalAsync("host", "two")).InputTransferred);
        Assert.Equal(new[] { "two" }, display.Targets);
        var switchRevision = (await keyboard.GetStateAsync(default)).Revision;
        await manager.OnFocusChangedAsync(new("two", switchRevision));
        Assert.Equal(new[] { "two" }, display.Targets);
    }

    [Fact]
    public async Task ANewerFocusAfterKeyboardReplyIsNotReportedAsACompletedSwitch()
    {
        var keyboard = new MovesAfterReplyKeyboard(); var display = new Display();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard, display: display);
        var result = await manager.SwitchNormalAsync("host", "one");
        Assert.False(result.InputTransferred);
        Assert.Equal("two", result.ActiveComputer);
        Assert.Empty(display.Targets);
    }

    private sealed class MovesAfterReplyKeyboard : IMultiPeerKeyboard
    {
        private string active = "host";
        private long revision = 1;
        public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
        public Task<DeskLinkState> GetStateAsync(CancellationToken ct) => Task.FromResult(new DeskLinkState
        { ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true, ActiveComputer = active,
            ConnectedComputers = ["one", "two"], Revision = revision });
        public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long expectedRevision, CancellationToken ct)
        {
            active = "two"; revision += 2;
            return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target });
        }
        public Task<DeskLinkState> StopAsync(CancellationToken ct) => GetStateAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Display : IUnifiedDisplayControl
    {
        public List<string> Targets { get; } = [];
        public Task<DisplaySwitchOutcome> ApplyAsync(string computer, CancellationToken cancellationToken)
        { Targets.Add(computer); return Task.FromResult(new DisplaySwitchOutcome("verified")); }
    }

    private sealed class BlockingKeyboard : IMultiPeerKeyboard
    {
        private long revision = 1;
        private int first = 1;
        public string Active { get; private set; } = "host";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
        public async Task<DeskLinkState> GetStateAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref first, 0) == 1) { Entered.TrySetResult(); await Resume.Task.WaitAsync(ct); }
            return new DeskLinkState { ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true,
                ActiveComputer = Active, ConnectedComputers = ["one", "two"], Revision = revision };
        }
        public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long expectedRevision, CancellationToken ct)
        {
            if (Active != expected || revision != expectedRevision) return Task.FromResult(new DeskLinkSwitchResult { Error = "stale" });
            Active = target; revision++;
            return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target });
        }
        public Task<DeskLinkState> StopAsync(CancellationToken ct) => GetStateAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Keyboard : IMultiPeerKeyboard
    {
        private long revision = 1;
        public string Active { get; private set; } = "host";
        public int SwitchCalls { get; private set; }
        public void MoveExternally(string target) { Active = target; revision++; }
        public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
        public Task<DeskLinkState> GetStateAsync(CancellationToken ct) => Task.FromResult(new DeskLinkState
        { ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true, ActiveComputer = Active,
            ConnectedComputers = ["one", "two"], Revision = revision });
        public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long expectedRevision, CancellationToken ct)
        {
            SwitchCalls++;
            if (Active != expected || revision != expectedRevision) return Task.FromResult(new DeskLinkSwitchResult { Error = "stale" });
            Active = target; revision++;
            return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target });
        }
        public Task<DeskLinkState> StopAsync(CancellationToken ct) => GetStateAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
}
