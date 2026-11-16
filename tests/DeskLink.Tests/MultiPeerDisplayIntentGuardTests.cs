using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class MultiPeerDisplayIntentGuardTests
{
    [Fact]
    public void PreviewRevisionsAndDelayedEventsNeverSwitchTheDisplay()
    {
        var guard = new MultiPeerDisplayIntentGuard("host", ["one", "two"]);
        var during = State("one", 11);
        Assert.False(guard.Accept(new("one", 11), during, previewActive: true));
        guard.RecordPreview(12);
        Assert.False(guard.Accept(new("one", 11), State("host", 12), previewActive: false));
        Assert.False(guard.Accept(new("host", 12), State("host", 12), previewActive: false));
        Assert.True(guard.Accept(new("two", 13), State("two", 13), previewActive: false));
        Assert.False(guard.Accept(new("two", 13), State("two", 13), previewActive: false));
    }

    [Fact]
    public void DisconnectedOutdatedOrUnapprovedFocusNeverChangesTheDisplay()
    {
        var guard = new MultiPeerDisplayIntentGuard("host", ["one", "two"]);
        Assert.False(guard.Accept(new("two", 4), State("one", 5), false));
        Assert.False(guard.Accept(new("stranger", 6), State("stranger", 6), false));
        Assert.False(guard.Accept(new("host", 7), State("host", 7) with { ConnectedComputers = [] }, false));
        Assert.False(guard.Accept(new("one", 8), State("one", 8) with { NativeControlAvailable = false }, false));
        Assert.True(guard.Accept(new("one", 9), State("one", 9), false));
    }

    private static DeskLinkState State(string active, long revision) => new()
    { ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true, ActiveComputer = active,
        Revision = revision, ConnectedComputers = ["one", "two"] };
}
