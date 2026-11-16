using DesktopToolbox.Windows.DeskSwitch;
using NianLink.MultiDevice;

public sealed class MultiDeviceDisplaySelectionTests
{
    private static readonly DisplayMonitorInfo Monitor = new("monitor", "显示器", 15,
        [new(15, "DisplayPort 1"), new(16, "DisplayPort 2"), new(17, "HDMI 1")], true, "");

    [Fact]
    public void SupportsASubsetOfApprovedDevicesUsingDistinctReportedPorts()
    {
        var mapping = MultiDeviceDisplaySelection.Validate(Monitor, "host", ["one", "two", "three"],
            new Dictionary<string, uint> { ["host"] = 15, ["one"] = 16, ["two"] = 17 });
        Assert.Equal((uint)16, mapping.Inputs["one"]);
        Assert.False(mapping.Inputs.ContainsKey("three"));
    }

    [Fact]
    public void RejectsUnknownDevicesDuplicatePortsAndUnavailableReports()
    {
        Assert.Throws<InvalidOperationException>(() => MultiDeviceDisplaySelection.Validate(Monitor, "host", ["one"],
            new Dictionary<string, uint> { ["host"] = 15, ["unknown"] = 16 }));
        Assert.Throws<InvalidOperationException>(() => MultiDeviceDisplaySelection.Validate(Monitor, "host", ["one"],
            new Dictionary<string, uint> { ["host"] = 15, ["one"] = 15 }));
        Assert.Throws<InvalidOperationException>(() => MultiDeviceDisplaySelection.Validate(Monitor, "host", ["one"],
            new Dictionary<string, uint> { ["host"] = 15, ["one"] = 18 }));
        Assert.Throws<InvalidOperationException>(() => MultiDeviceDisplaySelection.Validate(Monitor with { CanReadInput = false }, "host", ["one"],
            new Dictionary<string, uint> { ["host"] = 15, ["one"] = 16 }));
    }
}
