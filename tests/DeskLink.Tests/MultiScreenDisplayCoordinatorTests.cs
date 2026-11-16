using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class MultiScreenDisplayCoordinatorTests
{
    [Fact]
    public async Task CancellationBeforeDispatchCannotStartAnInputSwitch()
    {
        var (topology, state) = Setup();
        var router = new FakeRouter();
        using var cancel = new CancellationTokenSource();
        var coordinator = new MultiScreenDisplayCoordinator(topology, state, _ => { cancel.Cancel(); return Task.FromResult(Keyboard("mac")); }, router, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ApplyAsync("mac", cancel.Token));
        Assert.Empty(router.Commands);
        Assert.Null(state.Pending);
        Assert.Equal("desktop", state.VisibleDevice("shared27"));
    }

    [Fact]
    public async Task UnconfirmedKeyboardFocusNeverDispatchesDisplayCommands()
    {
        var (topology, state) = Setup();
        var router = new FakeRouter();
        var coordinator = new MultiScreenDisplayCoordinator(topology, state, _ => Task.FromResult(Keyboard("laptop")), router, TimeSpan.FromSeconds(1));
        Assert.Equal("failed", (await coordinator.ApplyAsync("mac", default)).Status);
        Assert.Empty(router.Commands);
        Assert.Equal("desktop", state.VisibleDevice("shared27"));
        var other = Setup().Item1;
        Assert.Throws<ArgumentException>(() => new MultiScreenDisplayCoordinator(other, state, _ => Task.FromResult(Keyboard("mac")), router, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task LaptopSwitchLeavesTheSharedScreenUntouchedAndMacSwitchUsesOnlyThatScreen()
    {
        var (topology, state) = Setup();
        var router = new FakeRouter();
        var active = "laptop";
        var coordinator = new MultiScreenDisplayCoordinator(topology, state, _ => Task.FromResult(Keyboard(active)), router, TimeSpan.FromSeconds(1));
        Assert.Equal("disabled", (await coordinator.ApplyAsync("laptop", default)).Status);
        Assert.Empty(router.Commands);
        active = "mac";
        Assert.Equal("acceptedUnverified", (await coordinator.ApplyAsync("mac", default)).Status);
        Assert.Equal("shared27", Assert.Single(router.Commands).Plan.ScreenId);
        Assert.Null(state.VisibleDevice("shared27"));
        Assert.Equal("mac", state.VisibleDevice("mac16"));
    }

    [Fact]
    public async Task TimedOutOperationKeepsScreenLockedUntilItsActualCompletion()
    {
        var (topology, state) = Setup();
        var router = new FakeRouter { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var coordinator = new MultiScreenDisplayCoordinator(topology, state, _ => Task.FromResult(Keyboard("mac")), router, TimeSpan.FromMilliseconds(20));
        Assert.Equal("acceptedUnverified", (await coordinator.ApplyAsync("mac", default)).Status);
        Assert.NotNull(state.Pending);
        Assert.Equal("failed", (await coordinator.ApplyAsync("mac", default)).Status);
        router.Pending.SetResult(new("verified"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (state.Pending is not null) await Task.Delay(10, timeout.Token);
        Assert.Equal("mac", state.VisibleDevice("shared27"));
    }

    [Fact]
    public async Task UnsentCommandAfterTimeoutRestoresPreviouslyConfirmedScreen()
    {
        var (topology, state) = Setup();
        var router = new FakeRouter { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var coordinator = new MultiScreenDisplayCoordinator(topology, state, _ => Task.FromResult(Keyboard("mac")), router, TimeSpan.FromMilliseconds(20));
        Assert.Equal("acceptedUnverified", (await coordinator.ApplyAsync("mac", default)).Status);
        router.Pending.SetResult(new("notAttempted"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (state.Pending is not null) await Task.Delay(10, timeout.Token);
        Assert.Equal("desktop", state.VisibleDevice("shared27"));
    }

    [Fact]
    public async Task ManualScreenRequiresConfirmationAndNeverCallsTheHardwareRouter()
    {
        var (topology, state) = Setup(manual: true);
        var router = new FakeRouter();
        var coordinator = new MultiScreenDisplayCoordinator(topology, state, _ => Task.FromResult(Keyboard("mac")), router, TimeSpan.FromSeconds(1));
        Assert.Equal("acceptedUnverified", (await coordinator.ApplyAsync("mac", default)).Status);
        Assert.Empty(router.Commands);
        var pending = Assert.IsType<MultiScreenPendingSwitch>(state.Pending);
        Assert.True(pending.Plan.RequiresManualSwitch);
        coordinator.ConfirmManualSwitch(pending.Id, true);
        Assert.Equal("mac", state.VisibleDevice("shared27"));
    }

    [Fact]
    public async Task FocusChangeDuringFirstScreenStopsRemainingScreenSwitches()
    {
        var topology = MultiScreenTopology.Create("desktop", ["mac"],
            [new MultiScreenDefinition("first", false, [new("desktop", "d1", 15), new("mac", "m1", 16)]) { AutoSwitchEnabled = true },
             new MultiScreenDefinition("second", false, [new("desktop", "d2", 15), new("mac", "m2", 16)]) { AutoSwitchEnabled = true }]);
        var state = new MultiScreenDisplayState(topology);
        state.ConfirmVisible("first", "desktop"); state.ConfirmVisible("second", "desktop");
        var current = Keyboard("mac");
        var router = new CallbackRouter(() => current = Keyboard("desktop") with { Revision = 2 });
        var coordinator = new MultiScreenDisplayCoordinator(topology, state, _ => Task.FromResult(current), router, TimeSpan.FromSeconds(1));
        var result = await coordinator.ApplyAsync("mac", default);
        Assert.Equal("failed", result.Status);
        Assert.Single(router.Commands);
        Assert.Equal("desktop", state.VisibleDevice("second"));
    }

    private static (MultiScreenTopology, MultiScreenDisplayState) Setup(bool manual = false)
    {
        var shared = new MultiScreenDefinition("shared27", false,
            [new("desktop", "desktop-shared", 15), new("mac", "mac-shared", 16)]) { AutoSwitchEnabled = true, ManualSwitchOnly = manual };
        var topology = MultiScreenTopology.Create("desktop", ["mac", "laptop"],
            [shared, new("mac16", false, [new("mac", "mac-small", 15)]), new("laptop-screen", true, [new("laptop", null, null)])]);
        var state = new MultiScreenDisplayState(topology);
        state.ConfirmVisible("shared27", "desktop"); state.ConfirmVisible("mac16", "mac");
        return (topology, state);
    }

    private static DeskLinkState Keyboard(string active) => new() { ProcessState = DeskLinkProcessState.Running,
        NativeControlAvailable = true, ActiveComputer = active, Revision = 1 };

    private sealed class FakeRouter : IMultiScreenDisplayRouter
    {
        public List<MultiScreenPendingSwitch> Commands { get; } = [];
        public TaskCompletionSource<DisplaySwitchOutcome>? Pending { get; set; }
        public Task<DisplaySwitchOutcome> ExecuteAsync(MultiScreenPendingSwitch command)
        { Commands.Add(command); return Pending?.Task ?? Task.FromResult(new DisplaySwitchOutcome("acceptedUnverified")); }
    }

    private sealed class CallbackRouter(Action callback) : IMultiScreenDisplayRouter
    {
        public List<MultiScreenPendingSwitch> Commands { get; } = [];
        public Task<DisplaySwitchOutcome> ExecuteAsync(MultiScreenPendingSwitch command)
        { Commands.Add(command); callback(); return Task.FromResult(new DisplaySwitchOutcome("verified")); }
    }
}
