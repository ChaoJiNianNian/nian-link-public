using System.Text.RegularExpressions;

namespace DesktopToolbox.Windows.DeskSwitch;

public sealed record DeskSwitchSettings
{
    public string LocalName { get; init; } = "Win-PC";
    public string RemoteName { get; init; } = "My-Mac";
    public string MonitorId { get; init; } = "";
    public uint LocalInput { get; init; }
    public uint RemoteInput { get; init; }
    public bool LinkDisplay { get; init; }
    public bool ClipboardEnabled { get; init; }

    public DeskSwitchSettings Validate()
    {
        if (!IsDnsName(LocalName, 64) || !IsDnsName(RemoteName, 64))
            throw new ArgumentException("两台电脑名称须为不超过 64 字符的英文、数字、连字符或点分隔名称，且每段不能以连字符开头或结尾。");
        if (string.Equals(LocalName, RemoteName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("两台电脑的名称不能相同。");
        if (MonitorId is null || MonitorId.Length > 256 || MonitorId.Any(char.IsControl))
            throw new ArgumentException("显示器标识不能超过 256 字符，且不能包含控制字符。");
        if (LocalInput > 255 || RemoteInput > 255)
            throw new ArgumentException("显示器输入源须为 0 到 255，0 表示尚未配置。");
        if (LinkDisplay && (string.IsNullOrWhiteSpace(MonitorId) || LocalInput == 0 || RemoteInput == 0 || LocalInput == RemoteInput))
            throw new ArgumentException("启用画面联动前，请选择显示器并设置两个不同的有效输入源（1 到 255）。");
        return this;
    }

    internal static bool IsDnsName(string? value, int maximumLength) =>
        !string.IsNullOrEmpty(value) && value.Length <= maximumLength &&
        value.Split('.').All(label => label.Length is >= 1 and <= 63 &&
            Regex.IsMatch(label, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\z", RegexOptions.CultureInvariant));
}
