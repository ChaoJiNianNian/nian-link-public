using DesktopToolbox.Windows.DeskSwitch;

namespace NianLink.Windows;

internal sealed record DisplayInputChoice(uint Code, string Label);

/// <summary>Only offer interfaces reported by this monitor; the user identifies the connected computer.</summary>
internal static class DisplayInputSelection
{
    internal static DisplayInputChoice[] Choices(DisplayMonitorInfo monitor)
    {
        var inputs = monitor.Inputs ?? [];
        if (!monitor.CanReadInput || inputs.Length < 2 || inputs.Any(x => x is null || x.Value is 0 or > 255) ||
            inputs.Select(x => x.Value).Distinct().Count() != inputs.Length) return [];
        return inputs.Select((input, index) => new DisplayInputChoice(input.Value, FriendlyName(input, index))).ToArray();
    }

    internal static (uint Windows, uint Mac) Validate(DisplayMonitorInfo monitor, uint? windows, uint? mac)
    {
        var choices = Choices(monitor);
        if (choices.Length < 2) throw new InvalidOperationException("无法识别可用的显示器接口，请检查显示器连接后重新检测。");
        if (windows is null || mac is null || !choices.Any(x => x.Code == windows) || !choices.Any(x => x.Code == mac))
            throw new InvalidOperationException("请选择 Windows 和 Mac 各自连接的显示器接口。");
        if (windows == mac) throw new InvalidOperationException("Windows 和 Mac 不能选择同一个显示器接口。");
        return (windows.Value, mac.Value);
    }

    // A temporary detection failure must not prevent the user from starting their
    // already configured keyboard sharing. This never creates or saves a mapping.
    internal static bool CanUseSavedForStart(DeskSwitchSettings saved, DisplayMonitorInfo? selected, DisplayMonitorInfo[]? detected)
    {
        if (!saved.LinkDisplay || string.IsNullOrWhiteSpace(saved.MonitorId) || saved.LocalInput is 0 or > 255 ||
            saved.RemoteInput is 0 or > 255 || saved.LocalInput == saved.RemoteInput) return false;
        if (selected is null) return detected is null || detected.Length == 0;
        return detected is not null && detected.Count(x => x is not null && string.Equals(x.Id, saved.MonitorId, StringComparison.Ordinal)) == 1 &&
            string.Equals(selected.Id, saved.MonitorId, StringComparison.Ordinal) && Choices(selected).Length == 0;
    }

    private static string FriendlyName(MonitorInput input, int index)
    {
        // Existing labels contain an internal VCP number in parentheses. It is not useful to users.
        var label = (input.Label ?? "").Trim();
        var suffix = " (" + input.Value + ")";
        if (label.EndsWith(suffix, StringComparison.Ordinal)) label = label[..^suffix.Length].Trim();
        if (string.IsNullOrWhiteSpace(label) || label.StartsWith("设备输入 ", StringComparison.Ordinal))
            return "其他接口 " + (char)('A' + Math.Min(index, 25));
        return label;
    }
}
