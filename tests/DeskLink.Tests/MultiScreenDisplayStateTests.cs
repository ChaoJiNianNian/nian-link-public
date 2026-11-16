using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class MultiScreenDisplayStateTests
{
    [Fact]
    public void SharedDesktopMacMonitorSwitchDoesNotChangeMacSmallScreenOrLaptopScreen()
    {
        var topology = MultiScreenTopology.Create("desktop", ["laptop", "mac"],
        [
            new("shared27", false, [new("desktop", "desktop-shared", 15), new("mac", "mac-shared", 16)]),
            new("mac16", false, [new("mac", "mac-small", 15)]),
            new("laptop", true, [new("laptop", null, null)])
        ]);
        var state = new MultiScreenDisplayState(topology);
        Assert.Equal(new[] { "shared27" }, topology.SharedScreensForDevice("mac"));
        Assert.Equal(new[] { "shared27" }, topology.SharedScreensForDevice("desktop"));
        Assert.Empty(topology.SharedScreensForDevice("laptop"));
        state.ConfirmVisible("shared27", "desktop"); state.ConfirmVisible("mac16", "mac");
        var request = state.Begin("shared27", "mac", "mac", 1)!;
        Assert.Equal("shared27", request.Plan.ScreenId);
        Assert.Equal("mac", state.VisibleDevice("mac16"));
        state.Complete(request.Id, "verified");
        Assert.Equal("mac", state.VisibleDevice("shared27"));
        Assert.Equal("mac", state.VisibleDevice("mac16"));
        Assert.Null(state.Begin("laptop", "laptop", "laptop", 2));
        Assert.Throws<InvalidOperationException>(() => state.Begin("shared27", "laptop", "laptop", 3));
    }

    private static MultiScreenDisplayState Create()
    {
        var topology = MultiScreenTopology.Create("desktop", ["laptop", "mac"],
        [
            new("left", false, [new("desktop", "desktop-left", 15), new("mac", "mac-left", 16)]),
            new("right", false, [new("desktop", "desktop-right", 15), new("laptop", "laptop-right", 17)]),
            new("portable", true, [new("laptop", null, null)])
        ]);
        return new MultiScreenDisplayState(topology);
    }

    [Fact]
    public void AnUnconfirmedResultInvalidatesOnlyTheScreenThatWasSwitched()
    {
        var state = Create();
        state.ConfirmVisible("left", "desktop"); state.ConfirmVisible("right", "laptop");
        var command = state.Begin("left", "mac", "mac", 1)!;
        Assert.Null(state.VisibleDevice("left"));
        Assert.Equal("laptop", state.VisibleDevice("right"));
        state.Complete(command.Id, "acceptedUnverified");
        Assert.Null(state.VisibleDevice("left"));
        Assert.Equal("laptop", state.VisibleDevice("right"));
        Assert.Throws<InvalidOperationException>(() => state.Begin("left", "desktop", "desktop", 2));
    }

    [Fact]
    public void ConcurrentDisplayCommandsAndStaleKeyboardFocusAreRejected()
    {
        var state = Create();
        state.ConfirmVisible("left", "desktop"); state.ConfirmVisible("right", "desktop");
        var command = state.Begin("left", "mac", "mac", 5)!;
        Assert.Throws<InvalidOperationException>(() => state.Begin("right", "laptop", "laptop", 6));
        Assert.Throws<InvalidOperationException>(() => state.ConfirmVisible("left", "desktop"));
        state.Complete(command.Id, "verified");
        Assert.Equal("mac", state.VisibleDevice("left"));
        Assert.Throws<InvalidOperationException>(() => state.Begin("right", "laptop", "desktop", 6));
        Assert.Throws<InvalidOperationException>(() => state.Begin("right", "laptop", "laptop", 4));
    }

    [Fact]
    public void BuiltInScreenNeedsNoDisplayCommand()
    {
        var state = Create();
        Assert.Null(state.Begin("portable", "laptop", "laptop", 1));
        Assert.Throws<InvalidOperationException>(() => state.Begin("portable", "mac", "mac", 2));
    }
}
