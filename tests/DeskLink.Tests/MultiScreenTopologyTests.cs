using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class MultiScreenTopologyTests
{
    private static MultiScreenTopology Example() => MultiScreenTopology.Create("desktop", ["laptop", "mac"],
    [
        new("left", false, [new("desktop", "desktop-left", 15), new("mac", "mac-left", 16)]),
        new("right", false, [new("desktop", "desktop-right", 15)]),
        new("portable", true, [new("laptop", "laptop-internal", null)])
    ]);

    [Fact]
    public void PlansOnlyTheSelectedSharedScreenUsingTheCurrentlyVisibleComputer()
    {
        var topology = Example();
        var plan = topology.Plan("left", "desktop", "mac");
        Assert.Equal(new MultiScreenSwitchPlan("left", "desktop", "desktop-left", 15, 16), plan);
        Assert.Null(topology.Plan("left", "mac", "mac"));
        Assert.Throws<InvalidOperationException>(() => topology.Plan("right", "desktop", "mac"));
    }

    [Fact]
    public void BuiltInDisplayDoesNotIssueAnInputSwitchAndCannotDisplayAnotherComputer()
    {
        var topology = Example();
        Assert.Null(topology.Plan("portable", null, "laptop"));
        Assert.Throws<InvalidOperationException>(() => topology.Plan("portable", "laptop", "mac"));
    }

    [Fact]
    public void UnknownVisibleSourceCannotIssueACommandForAnExternalDisplay()
    {
        var topology = Example();
        Assert.Throws<InvalidOperationException>(() => topology.Plan("left", null, "mac"));
        Assert.Throws<InvalidOperationException>(() => topology.Plan("left", "laptop", "mac"));
    }

    [Fact]
    public void UnresolvedLocalMonitorOnlyBlocksCommandsThatMustRunOnThatComputer()
    {
        var topology = MultiScreenTopology.Create("desktop", ["mac"],
            [new("left", false, [new("desktop", "desktop-left", 15), new("mac", null, 16)])]);
        Assert.NotNull(topology.Plan("left", "desktop", "mac"));
        Assert.Throws<InvalidOperationException>(() => topology.Plan("left", "mac", "desktop"));
        Assert.Null(topology.Plan("left", "mac", "mac"));
    }

    [Fact]
    public void MonitorWithoutReliableDdcProducesAnExplicitManualSwitchInstruction()
    {
        var topology = MultiScreenTopology.Create("desktop", ["mac"],
            [new MultiScreenDefinition("left", false, [new("desktop", null, null), new("mac", null, null)]) { ManualSwitchOnly = true }]);
        var plan = topology.Plan("left", "desktop", "mac");
        Assert.NotNull(plan);
        Assert.True(plan.RequiresManualSwitch);
        Assert.Null(plan.ExpectedInput);
        Assert.Null(plan.TargetInput);
    }

    [Fact]
    public void AutomaticDisplayLinkageMustBeExplicitAndCannotApplyToADeviceOnlyScreen()
    {
        var shared = new MultiScreenDefinition("left", false, [new("desktop", "host-screen", 15), new("mac", "mac-screen", 16)]);
        var topology = MultiScreenTopology.Create("desktop", ["mac"], [shared]);
        Assert.Empty(topology.AutomaticScreensForDevice("mac"));
        var enabled = MultiScreenTopology.Create("desktop", ["mac"], [shared with { AutoSwitchEnabled = true }]);
        Assert.Equal(new[] { "left" }, enabled.AutomaticScreensForDevice("mac"));
        Assert.Throws<ArgumentException>(() => MultiScreenTopology.Create("desktop", ["mac"],
            [new MultiScreenDefinition("mac-only", false, [new("mac", "mac-screen", 15)]) { AutoSwitchEnabled = true }]));
    }

    [Fact]
    public void RejectsAmbiguousPortsAndDuplicateLocalMonitorIdentities()
    {
        Assert.Throws<ArgumentException>(() => MultiScreenTopology.Create("desktop", ["mac"],
            [new("left", false, [new("desktop", "screen1", 15), new("mac", "screen2", 15)])]));
        Assert.Throws<ArgumentException>(() => MultiScreenTopology.Create("desktop", ["mac"],
            [new("left", false, [new("desktop", "same", 15)]), new("right", false, [new("desktop", "same", 16)])]));
        Assert.Throws<ArgumentException>(() => MultiScreenTopology.Create("desktop", ["mac"],
            [new("internal", true, [new("desktop", "one", null), new("mac", "two", null)])]));
    }
}
