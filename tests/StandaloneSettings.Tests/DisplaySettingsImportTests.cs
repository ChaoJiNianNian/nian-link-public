using DesktopToolbox.Windows.DeskSwitch;
using NianLink.Windows;
using System.Text.Json;

public class DisplaySettingsImportTests
{
    private static DeskSwitchSettings Current() => new() { LocalName = "Windows-New", RemoteName = "Mac-New", ClipboardEnabled = true };
    private static LegacyDisplaySettings Old() => new() { LinkDisplay = true, MonitorId = "monitor-A|0", LocalInput = 15, RemoteInput = 16 };
    private static DisplayMonitorInfo Monitor(string id = "monitor-A|0", MonitorInput[]? inputs = null) =>
        new(id, "Example monitor", 15, inputs ?? [], true, "ready");

    [Fact] public void ImportsOnlyDisplayFieldsWhenMonitorMatches()
    {
        var value = DisplaySettingsImport.Prepare(Current(), Old(), [Monitor()]);
        Assert.True(value.LinkDisplay); Assert.Equal("monitor-A|0", value.MonitorId);
        Assert.Equal(15u, value.LocalInput); Assert.Equal(16u, value.RemoteInput);
        Assert.Equal("Windows-New", value.LocalName); Assert.Equal("Mac-New", value.RemoteName); Assert.True(value.ClipboardEnabled);
    }
    [Fact] public void RejectsMissingOrAmbiguousMonitor()
    {
        Assert.Throws<InvalidOperationException>(() => DisplaySettingsImport.Prepare(Current(), Old(), [Monitor("another|0")]));
        Assert.Throws<InvalidOperationException>(() => DisplaySettingsImport.Prepare(Current(), Old(), [Monitor(), Monitor()]));
    }
    [Theory] [InlineData(0u, 16u)] [InlineData(15u, 15u)] [InlineData(256u, 16u)]
    public void RejectsInvalidInputNumbers(uint local, uint remote) =>
        Assert.Throws<InvalidOperationException>(() => DisplaySettingsImport.Prepare(Current(), Old() with { LocalInput = local, RemoteInput = remote }, [Monitor()]));
    [Fact] public void RejectsConflictingReportedInputs() =>
        Assert.Throws<InvalidOperationException>(() => DisplaySettingsImport.Prepare(Current(), Old(), [Monitor(inputs: [new(15,"DP1"), new(17,"HDMI")])]));
    [Fact] public void DoesNotEnableFeatureWhenOldConfigurationWasDisabled() =>
        Assert.False(DisplaySettingsImport.Prepare(Current(), Old() with { LinkDisplay = false }, [Monitor()]).LinkDisplay);
    [Fact] public void ReadsOnlyDisplaySettingsFromOldSettingsJson()
    {
        const string json = "{\"localName\":\"OldName\",\"remoteName\":\"OldMac\",\"clipboardEnabled\":false,\"monitorId\":\"monitor-A|0\",\"localInput\":15,\"remoteInput\":16,\"linkDisplay\":true}";
        var legacy = JsonSerializer.Deserialize<LegacyDisplaySettings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var value = DisplaySettingsImport.Prepare(Current(), legacy, [Monitor()]);
        Assert.Equal("Windows-New", value.LocalName); Assert.True(value.ClipboardEnabled);
        Assert.Equal("monitor-A|0", value.MonitorId); Assert.True(value.LinkDisplay);
    }
}
