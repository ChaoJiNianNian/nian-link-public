using DesktopToolbox.Windows.DeskSwitch;
using NianLink.Windows;

public class DisplayMappingGuardTests
{
    private static readonly DeskSwitchSettings Saved = new() { LinkDisplay = true, MonitorId = "screen", LocalInput = 15, RemoteInput = 16 };

    [Fact] public void AllowsUnchangedDisplayWithEditedNames() =>
        DisplayMappingGuard.EnsureUnchangedForStart(Saved with { LocalName = "New Windows" }, Saved);

    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void RefusesToStartWithAnyUnsavedDisplayChange(int change)
    {
        var pending = change switch { 1 => Saved with { LinkDisplay = false }, 2 => Saved with { MonitorId = "other" },
            3 => Saved with { LocalInput = 17 }, _ => Saved with { RemoteInput = 18 } };
        Assert.Throws<InvalidOperationException>(() => DisplayMappingGuard.EnsureUnchangedForStart(pending, Saved));
    }
}
