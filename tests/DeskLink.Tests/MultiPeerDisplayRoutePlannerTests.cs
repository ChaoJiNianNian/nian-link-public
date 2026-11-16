using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class MultiPeerDisplayRoutePlannerTests
{
    [Fact]
    public void RejectsAmbiguousInputsEvenWhenTheConflictingDeviceIsNotTheTarget()
    {
        var mapping = new Dictionary<string, MultiPeerMonitorSettings>
        { ["one"] = new("screen", 15, 16), ["two"] = new("screen", 15, 17), ["three"] = new("screen", 15, 17) };
        Assert.Null(MultiPeerDisplayRoutePlanner.Plan("host", "host", "one", mapping));
    }
    private static readonly IReadOnlyDictionary<string, MultiPeerMonitorSettings> Mapping =
        new Dictionary<string, MultiPeerMonitorSettings> { ["one"] = new("screen", 15, 16), ["two"] = new("screen", 15, 17) };

    [Fact]
    public void UsesCurrentlyVisibleComputersVideoPathForEachTransition()
    {
        Assert.Equal(new MultiPeerDisplayRoute("host", "screen", 16), MultiPeerDisplayRoutePlanner.Plan("host", "host", "one", Mapping));
        Assert.Equal(new MultiPeerDisplayRoute("one", "screen", 15), MultiPeerDisplayRoutePlanner.Plan("host", "one", "host", Mapping));
        Assert.Equal(new MultiPeerDisplayRoute("one", "screen", 17), MultiPeerDisplayRoutePlanner.Plan("host", "one", "two", Mapping));
    }

    [Fact]
    public void RefusesMissingOrDifferentPhysicalMonitorMappings()
    {
        Assert.Null(MultiPeerDisplayRoutePlanner.Plan("host", "host", "unknown", Mapping));
        Assert.Null(MultiPeerDisplayRoutePlanner.Plan("host", "one", "one", Mapping));
        var different = new Dictionary<string, MultiPeerMonitorSettings>(Mapping) { ["two"] = new("other", 15, 17) };
        Assert.Null(MultiPeerDisplayRoutePlanner.Plan("host", "one", "two", different));
        var mismatchedHost = new Dictionary<string, MultiPeerMonitorSettings>(Mapping) { ["two"] = new("screen", 18, 17) };
        Assert.Null(MultiPeerDisplayRoutePlanner.Plan("host", "one", "two", mismatchedHost));
    }
}
