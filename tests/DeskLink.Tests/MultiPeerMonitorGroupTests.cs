using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class MultiPeerMonitorGroupTests
{
    [Fact]
    public void GroupCreatesOneDistinctInputPerExplicitlyMappedComputer()
    {
        var group = MultiPeerMonitorGroup.Create("host", new Dictionary<string, MultiPeerMonitorSettings>
        { ["one"] = new("screen", 15, 16), ["two"] = new("screen", 15, 17) });
        Assert.Equal("screen", group.MonitorId);
        Assert.Equal(15u, group.Inputs["host"]);
        Assert.Equal(16u, group.Inputs["one"]);
        Assert.Equal(17u, group.Inputs["two"]);
    }

    [Fact]
    public void DuplicatePortsAndInconsistentDisplaysAreRejectedForTheWholeGroup()
    {
        Assert.Throws<ArgumentException>(() => MultiPeerMonitorGroup.Create("host", new Dictionary<string, MultiPeerMonitorSettings>
        { ["one"] = new("screen", 15, 16), ["two"] = new("screen", 15, 16) }));
        Assert.Throws<ArgumentException>(() => MultiPeerMonitorGroup.Create("host", new Dictionary<string, MultiPeerMonitorSettings>
        { ["one"] = new("screen", 15, 16), ["two"] = new("other", 15, 17) }));
        Assert.Throws<ArgumentException>(() => MultiPeerMonitorGroup.Create("host", new Dictionary<string, MultiPeerMonitorSettings>
        { ["one"] = new("screen", 15, 16), ["two"] = new("screen", 18, 17) }));
    }

    [Fact]
    public void GroupDoesNotAcceptCaseVariantsOrTheHostAsAnotherDevice()
    {
        Assert.Throws<ArgumentException>(() => MultiPeerMonitorGroup.Create("host", new Dictionary<string, MultiPeerMonitorSettings>
        { ["one"] = new("screen", 15, 16), ["ONE"] = new("screen", 15, 17) }));
        Assert.Throws<ArgumentException>(() => MultiPeerMonitorGroup.Create("host", new Dictionary<string, MultiPeerMonitorSettings>
        { ["HOST"] = new("screen", 15, 16) }));
    }
}
