using DesktopToolbox.Windows.DeskSwitch;
using NianLink.Windows;

namespace NianLink.MultiDevice;

internal sealed record MultiDeviceDisplayMapping(string MonitorId, IReadOnlyDictionary<string, uint> Inputs);

/// <summary>Only maps explicitly approved devices to distinct ports reported by the selected monitor.</summary>
internal static class MultiDeviceDisplaySelection
{
    internal static MultiDeviceDisplayMapping Validate(DisplayMonitorInfo monitor, string host, IEnumerable<string> peers,
        IReadOnlyDictionary<string, uint> inputs)
    {
        ArgumentNullException.ThrowIfNull(monitor); ArgumentNullException.ThrowIfNull(peers); ArgumentNullException.ThrowIfNull(inputs);
        var allowed = new HashSet<string>(peers, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(host) || allowed.Count is < 1 or > 4 || allowed.Contains(host) ||
            allowed.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(monitor.Id) ||
            !inputs.ContainsKey(host) || inputs.Count < 2 || inputs.Count > allowed.Count + 1 ||
            inputs.Keys.Any(name => name != host && !allowed.Contains(name)) || inputs.Values.Distinct().Count() != inputs.Count)
            throw new InvalidOperationException("请为主控和需要切屏的设备选择不同接口。");
        var choices = DisplayInputSelection.Choices(monitor);
        if (choices.Length < 2 || inputs.Values.Any(value => !choices.Any(choice => choice.Code == value)))
            throw new InvalidOperationException("请选择显示器当前报告的有效接口。");
        return new(monitor.Id, new Dictionary<string, uint>(inputs, StringComparer.Ordinal));
    }
}
