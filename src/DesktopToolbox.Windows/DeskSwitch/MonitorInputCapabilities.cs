using System.Globalization;
using System.Text.RegularExpressions;

namespace DesktopToolbox.Windows.DeskSwitch;

public sealed record MonitorInput(uint Value, string Label);
public sealed record DisplayMonitorInfo(string Id, string Name, uint? CurrentInput, MonitorInput[] Inputs, bool CanReadInput, string Message);

public static class MonitorInputCapabilities
{
    public static MonitorInput[] Parse(string capabilities)
    {
        if (capabilities.Length > 65536) return [];
        var vcp = Regex.Match(capabilities, @"\bvcp\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!vcp.Success) return [];
        var start = vcp.Index + vcp.Length;
        var end = start;
        var depth = 1;
        while (end < capabilities.Length && depth > 0)
        {
            if (capabilities[end] == '(') depth++;
            if (capabilities[end] == ')') depth--;
            end++;
        }
        if (depth != 0) return [];
        var block = capabilities[start..(end - 1)];
        var feature = Regex.Match(block, @"(?:^|\s)60\s*\(([^()]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!feature.Success) return [];
        return feature.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(code => uint.TryParse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .Where(value => value is > 0 and <= 255).Distinct().Select(value => new MonitorInput(value, Label(value))).ToArray();
    }

    public static string Label(uint input) => input switch
    {
        1 => "VGA 1", 2 => "VGA 2", 3 => "DVI 1", 4 => "DVI 2",
        15 => "DisplayPort 1", 16 => "DisplayPort 2", 17 => "HDMI 1", 18 => "HDMI 2",
        27 => "USB-C / 设备扩展输入", _ => "设备输入 " + input
    } + " (" + input + ")";
}
