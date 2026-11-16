using DesktopToolbox.Windows.DeskSwitch;
namespace NianLink.Windows;
internal sealed record LegacyDisplaySettings
{
    public bool LinkDisplay { get; init; }
    public string MonitorId { get; init; } = "";
    public uint LocalInput { get; init; }
    public uint RemoteInput { get; init; }
}
internal static class DisplaySettingsImport
{
    internal static DeskSwitchSettings Prepare(DeskSwitchSettings current, LegacyDisplaySettings legacy, DisplayMonitorInfo[] monitors)
    {
        if (string.IsNullOrWhiteSpace(legacy.MonitorId) || legacy.LocalInput is 0 or > 255 ||
            legacy.RemoteInput is 0 or > 255 || legacy.LocalInput == legacy.RemoteInput)
            throw new InvalidOperationException("旧工具箱没有完整的显示器接口设置，请重新选择。 ");
        var matches = monitors.Where(m => m is not null && string.Equals(m.Id, legacy.MonitorId, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("旧设置中的显示器不在当前检测结果中或无法唯一确认，请重新选择。 ");
        var inputs = matches[0].Inputs ?? [];
        if (inputs.Length > 0 && (!inputs.Any(x => x.Value == legacy.LocalInput) || !inputs.Any(x => x.Value == legacy.RemoteInput)))
            throw new InvalidOperationException("旧设置的接口与显示器报告不一致，请重新选择。 ");
        return (current with { LinkDisplay = legacy.LinkDisplay, MonitorId = legacy.MonitorId,
            LocalInput = legacy.LocalInput, RemoteInput = legacy.RemoteInput }).Validate();
    }
}
