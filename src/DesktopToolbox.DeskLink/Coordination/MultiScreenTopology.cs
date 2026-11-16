namespace DesktopToolbox.DeskLink.Coordination;

/// <summary>One computer's explicitly confirmed connection to one physical screen.</summary>
public sealed record MultiScreenAttachment(string DeviceName, string? LocalMonitorId, uint? Input);
public sealed record MultiScreenDefinition(string ScreenId, bool IsBuiltIn, IReadOnlyList<MultiScreenAttachment> Attachments)
{
    public bool ManualSwitchOnly { get; init; }
    public bool AutoSwitchEnabled { get; init; }
}
public sealed record MultiScreenSwitchPlan(string ScreenId, string SourceDevice, string? SourceLocalMonitorId, uint? ExpectedInput, uint? TargetInput, bool RequiresManualSwitch = false);

/// <summary>Read-only display wiring. It plans commands only; it never changes hardware.</summary>
public sealed class MultiScreenTopology
{
    private readonly Dictionary<string, MultiScreenDefinition> screens;
    private readonly Dictionary<string, string> devices;
    private MultiScreenTopology(Dictionary<string, MultiScreenDefinition> screens, Dictionary<string, string> devices)
    { this.screens = screens; this.devices = devices; }

    public IReadOnlyCollection<MultiScreenDefinition> Screens => Array.AsReadOnly(screens.Values.ToArray());

    /// <summary>Only screens shared by multiple computers may need a physical input switch.</summary>
    public IReadOnlyList<string> SharedScreensForDevice(string device)
    {
        if (!devices.TryGetValue(device, out var canonical)) throw new ArgumentException("电脑不在已确认的配置中。", nameof(device));
        return Array.AsReadOnly(screens.Values.Where(screen => !screen.IsBuiltIn && screen.Attachments.Count > 1 &&
            screen.Attachments.Any(item => item.DeviceName == canonical)).Select(screen => screen.ScreenId).ToArray());
    }

    public IReadOnlyList<string> AutomaticScreensForDevice(string device)
    {
        if (!devices.TryGetValue(device, out var canonical)) throw new ArgumentException("电脑不在已确认的配置中。", nameof(device));
        return Array.AsReadOnly(screens.Values.Where(screen => screen.AutoSwitchEnabled && !screen.IsBuiltIn && screen.Attachments.Count > 1 &&
            screen.Attachments.Any(item => item.DeviceName == canonical)).Select(screen => screen.ScreenId).ToArray());
    }

    public static MultiScreenTopology Create(string host, IEnumerable<string> peers, IEnumerable<MultiScreenDefinition> definitions)
    {
        DeskflowProfile.ValidateName(host);
        ArgumentNullException.ThrowIfNull(peers); ArgumentNullException.ThrowIfNull(definitions);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [host] = host };
        foreach (var name in peers.Take(5))
        {
            DeskflowProfile.ValidateName(name);
            if (names.Count >= 5 || !names.TryAdd(name, name)) throw new ArgumentException("设备名称重复或数量超过限制。", nameof(peers));
        }
        var result = new Dictionary<string, MultiScreenDefinition>(StringComparer.OrdinalIgnoreCase);
        var localMonitors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var screen in definitions.Take(17))
        {
            if (screen is null || result.Count >= 16 || string.IsNullOrWhiteSpace(screen.ScreenId) || screen.ScreenId.Length > 64 ||
                screen.ScreenId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') || screen.Attachments is null)
                throw new ArgumentException("屏幕信息无效。", nameof(definitions));
            var attachments = screen.Attachments.ToArray();
            if (attachments.Length is 0 or > 5 || screen.IsBuiltIn && (attachments.Length != 1 || screen.ManualSwitchOnly || screen.AutoSwitchEnabled) ||
                screen.AutoSwitchEnabled && attachments.Length < 2)
                throw new ArgumentException("屏幕连接数量无效。", nameof(definitions));
            var attachedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inputs = new HashSet<uint>();
            var normalized = new List<MultiScreenAttachment>();
            foreach (var item in attachments)
            {
                if (item is null || !names.TryGetValue(item.DeviceName, out var canonical) || !attachedDevices.Add(canonical) ||
                    item.LocalMonitorId is { } local && (string.IsNullOrWhiteSpace(local) || local.Length > 256 || local.Any(char.IsControl) || !localMonitors.Add(canonical + "\0" + local)) ||
                    !ValidInput(screen, item.Input, inputs))
                    throw new ArgumentException("屏幕连接或输入接口存在冲突。", nameof(definitions));
                normalized.Add(item with { DeviceName = canonical });
            }
            if (!result.TryAdd(screen.ScreenId, screen with { Attachments = Array.AsReadOnly(normalized.ToArray()) }))
                throw new ArgumentException("屏幕名称重复。", nameof(definitions));
        }
        if (result.Count == 0) throw new ArgumentException("请至少添加一块屏幕。", nameof(definitions));
        return new(result, names);
    }

    /// <summary>Plans a change for exactly one screen; null means no physical input change is needed.</summary>
    public MultiScreenSwitchPlan? Plan(string screenId, string? confirmedVisibleDevice, string targetDevice)
    {
        if (!screens.TryGetValue(screenId, out var screen) || !devices.TryGetValue(targetDevice, out var target))
            throw new ArgumentException("屏幕或目标电脑不在已确认的配置中。");
        var destination = screen.Attachments.SingleOrDefault(item => item.DeviceName == target)
            ?? throw new InvalidOperationException("目标电脑未连接这块屏幕。");
        if (screen.IsBuiltIn) return null;
        if (confirmedVisibleDevice is null || !devices.TryGetValue(confirmedVisibleDevice, out var visible))
            throw new InvalidOperationException("请先确认这块屏幕当前显示哪台电脑。");
        var source = screen.Attachments.SingleOrDefault(item => item.DeviceName == visible)
            ?? throw new InvalidOperationException("当前显示的电脑未连接这块屏幕。");
        if (visible == target) return null;
        if (screen.ManualSwitchOnly) return new(screen.ScreenId, source.DeviceName, source.LocalMonitorId, source.Input, destination.Input, true);
        if (source.LocalMonitorId is null) throw new InvalidOperationException("请先确认当前电脑上的显示器身份，才能切换这块屏幕。");
        return new(screen.ScreenId, source.DeviceName, source.LocalMonitorId, source.Input, destination.Input);
    }

    private static bool ValidInput(MultiScreenDefinition screen, uint? input, HashSet<uint> used)
    {
        if (screen.IsBuiltIn) return input is null;
        if (input is null) return screen.ManualSwitchOnly;
        return input is > 0 and <= 255 && used.Add(input.Value);
    }
}
