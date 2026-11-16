using DesktopToolbox.Windows.DeskSwitch;
using NianLink.Windows;

public class DisplayInputSelectionTests
{
    private static DisplayMonitorInfo Monitor(MonitorInput[] inputs, bool readable = true) =>
        new("monitor", "Example", 15, inputs, readable, "");

    [Fact] public void DisplaysReportedInputsByNameWithoutNumbers()
    {
        var options = DisplayInputSelection.Choices(Monitor([new(15, "DisplayPort 1 (15)"), new(17, "HDMI 1 (17)")]));
        Assert.Equal(new[] { "DisplayPort 1", "HDMI 1" }, options.Select(x => x.Label));
        Assert.Equal(new uint[] { 15, 17 }, options.Select(x => x.Code));
    }

    [Fact] public void RefusesUnavailableOrDuplicateInputs()
    {
        Assert.Empty(DisplayInputSelection.Choices(Monitor([])));
        Assert.Empty(DisplayInputSelection.Choices(Monitor([new(15, "DP (15)"), new(15, "DP (15)")])));
        Assert.Empty(DisplayInputSelection.Choices(Monitor([new(15, "DP (15)"), new(16, "DP2 (16)")], readable: false)));
    }

    [Theory] [InlineData(15u, 15u)] [InlineData(15u, 17u)] [InlineData(0u, 16u)]
    public void RefusesUnreportedOrSameInput(uint windows, uint mac) =>
        Assert.Throws<InvalidOperationException>(() => DisplayInputSelection.Validate(
            Monitor([new(15, "DisplayPort 1 (15)"), new(16, "DisplayPort 2 (16)")]), windows, mac));

    [Fact] public void AcceptsTwoDifferentReportedInputs()
    {
        var selected = DisplayInputSelection.Validate(Monitor([new(15, "DisplayPort 1 (15)"), new(16, "DisplayPort 2 (16)")]), 15, 16);
        Assert.Equal((15u, 16u), selected);
    }

    [Fact] public void AllowsPreviouslySavedSettingsAtStartupWhenDisplayIsTemporarilyUnavailable()
    {
        var saved = new DeskSwitchSettings { LinkDisplay = true, MonitorId = "monitor", LocalInput = 15, RemoteInput = 16 };
        Assert.True(DisplayInputSelection.CanUseSavedForStart(saved, null, []));
        Assert.True(DisplayInputSelection.CanUseSavedForStart(saved, Monitor([]), [Monitor([])]));
    }

    [Fact] public void DoesNotFallbackToSavedSettingsForDifferentOrReadableMonitor()
    {
        var saved = new DeskSwitchSettings { LinkDisplay = true, MonitorId = "monitor", LocalInput = 15, RemoteInput = 16 };
        var available = Monitor([new(15, "DP1"), new(16, "DP2")]);
        var other = available with { Id = "other" };
        Assert.False(DisplayInputSelection.CanUseSavedForStart(saved, available, [available]));
        Assert.False(DisplayInputSelection.CanUseSavedForStart(saved, other, [other]));
        Assert.False(DisplayInputSelection.CanUseSavedForStart(saved, null, [other]));
    }
}
