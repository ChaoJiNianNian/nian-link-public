using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class MultiPeerDisplayStateTests
{
    private static readonly Dictionary<string, MultiPeerMonitorSettings> Mapping = new()
    {
        ["one"] = new("screen", 15, 16),
        ["two"] = new("screen", 15, 17)
    };

    [Fact]
    public void UsesConfirmedVisibleDeviceRatherThanKeyboardFocusAndRejectsConcurrentCommands()
    {
        var state = new MultiPeerDisplayState("host");
        state.ConfirmVisible("one");
        var command = state.Begin("two", "two", 5, Mapping);
        Assert.Equal(new MultiPeerDisplayRoute("one", "screen", 17), command.Route);
        Assert.Null(state.VisibleDevice);
        Assert.Throws<InvalidOperationException>(() => state.Begin("host", "host", 6, Mapping));
        Assert.Throws<InvalidOperationException>(() => state.ConfirmVisible("host"));
        state.Complete(command.Id, "verified");
        Assert.Equal("two", state.VisibleDevice);
    }

    [Fact]
    public void UncertainOutcomeRequiresExplicitConfirmationAndCannotAcceptLateReplies()
    {
        var state = new MultiPeerDisplayState("host");
        state.ConfirmVisible("host");
        var command = state.Begin("one", "one", 3, Mapping);
        state.Complete(command.Id, "acceptedUnverified");
        Assert.Null(state.VisibleDevice);
        Assert.Throws<InvalidOperationException>(() => state.Begin("host", "host", 4, Mapping));
        Assert.Throws<InvalidOperationException>(() => state.Complete(command.Id, "verified"));
        state.ConfirmVisible("one");
        var next = state.Begin("host", "host", 4, Mapping);
        Assert.Throws<InvalidOperationException>(() => state.Complete(command.Id, "verified"));
        state.Complete(next.Id, "failed");
        Assert.Null(state.VisibleDevice);
    }

    [Fact]
    public void RejectsUnknownMappingsAndStaleKeyboardState()
    {
        var state = new MultiPeerDisplayState("host");
        Assert.Throws<InvalidOperationException>(() => state.Begin("one", "one", 1, Mapping));
        state.ConfirmVisible("host");
        Assert.Throws<InvalidOperationException>(() => state.Begin("one", "two", 1, Mapping));
        Assert.Throws<InvalidOperationException>(() => state.Begin("unknown", "unknown", 1, Mapping));
        var command = state.Begin("one", "one", 3, Mapping);
        state.Complete(command.Id, "verified");
        Assert.Throws<InvalidOperationException>(() => state.Begin("host", "host", 2, Mapping));
    }
}
