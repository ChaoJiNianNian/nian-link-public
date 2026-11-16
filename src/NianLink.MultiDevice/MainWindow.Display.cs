using System.Windows;
using System.Windows.Controls;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Windows.DeskSwitch;
using NianLink.Windows;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    private readonly Dictionary<string, ComboBox> displayChoices = new(StringComparer.Ordinal);
    private DisplayMonitorInfo[] detectedDisplays = [];
    private long displayFormGeneration;
    private bool updatingDisplays;
    private SafeDisplayBridgeProcess? displayBridge;

    private void ResetDisplayGroup()
    {
        displayFormGeneration++;
        if (GroupMonitorChoice is null || GroupDisplayRows is null) return;
        updatingDisplays = true;
        try { detectedDisplays = []; GroupMonitorChoice.ItemsSource = null; GroupDisplayRows.Children.Clear(); displayChoices.Clear(); }
        finally { updatingDisplays = false; }
    }

    private async void DetectDisplay_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        var current = host ?? throw new InvalidOperationException("请先准备主控并启动共享。");
        if (current.Devices.Count == 0) throw new InvalidOperationException("请先启动共享并添加设备。");
        var helper = Path.Combine(AppContext.BaseDirectory, "DisplayBridge.exe");
        GroupDisplayStatus.Text = "正在检测显示器…";
        displayBridge ??= new SafeDisplayBridgeProcess(helper);
        var monitors = await displayBridge.ListAsync(CancellationToken.None);
        if (!ReferenceEquals(host, current)) return;
        detectedDisplays = monitors; displayFormGeneration++;
        updatingDisplays = true;
        try { GroupMonitorChoice.ItemsSource = monitors; GroupMonitorChoice.SelectedItem = monitors.Length == 1 ? monitors[0] : null; }
        finally { updatingDisplays = false; }
        if (monitors.Length == 0) { GroupDisplayRows.Children.Clear(); displayChoices.Clear(); GroupDisplayStatus.Text = "未检测到显示器，请检查连接。"; return; }
        GroupDisplayStatus.Text = monitors.Length == 1 ? "请选择每台电脑连接的接口。" : "请选择连接多台电脑的显示器。";
        await RefreshDisplayRowsAsync();
    });

    private async void GroupMonitorChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || updatingDisplays) return;
        try { await RefreshDisplayRowsAsync(); }
        catch (Exception error) { GroupDisplayStatus.Text = "读取接口设置未完成。"; ShowFailure(error, "读取接口设置"); }
    }

    private async Task RefreshDisplayRowsAsync()
    {
        var generation = ++displayFormGeneration;
        GroupDisplayRows.Children.Clear(); displayChoices.Clear();
        var current = host;
        if (current is null || GroupMonitorChoice.SelectedItem is not DisplayMonitorInfo monitor) return;
        if (detectedDisplays.Count(item => string.Equals(item.Id, monitor.Id, StringComparison.Ordinal)) != 1 ||
            detectedDisplays.Count(item => string.Equals(item.Name, monitor.Name, StringComparison.OrdinalIgnoreCase)) != 1)
        { GroupDisplayStatus.Text = "检测到无法区分的显示器，请先确认连接。"; return; }
        var choices = DisplayInputSelection.Choices(monitor);
        if (choices.Length < 2) { GroupDisplayStatus.Text = "无法识别显示器接口，请检查连接。"; return; }
        var peers = current.Devices.Select(device => device.Name).ToArray();
        var saved = new Dictionary<string, MultiPeerMonitorSettings?>(StringComparer.Ordinal);
        foreach (var peerName in peers) saved[peerName] = await current.GetDisplayPreferenceAsync(peerName, CancellationToken.None);
        if (generation != displayFormGeneration || !ReferenceEquals(host, current) || !ReferenceEquals(GroupMonitorChoice.SelectedItem, monitor)) return;
        var matching = saved.Values.Where(value => value is not null && value.MonitorId == monitor.Id).Cast<MultiPeerMonitorSettings>().ToArray();
        uint? hostInput = matching.Length > 0 && matching.Select(value => value.HostInput).Distinct().Count() == 1 ? matching[0].HostInput : null;
        AddDisplayChoice(HostName.Text.Trim(), "本机", choices, hostInput, false);
        foreach (var peerName in peers)
        {
            var previous = saved[peerName];
            AddDisplayChoice(peerName, DisplayDeviceName(peerName), choices, previous?.MonitorId == monitor.Id ? previous.PeerInput : null, true);
        }
    }

    private void AddDisplayChoice(string device, string label, DisplayInputChoice[] choices, uint? selected, bool optional)
    {
        var items = optional ? new[] { new DisplayInputChoice(0, "不使用此显示器") }.Concat(choices).ToArray() : choices;
        var box = new ComboBox { ItemsSource = items, DisplayMemberPath = "Label", SelectedValuePath = "Code", SelectedValue = selected ?? (optional ? 0u : null) };
        displayChoices.Add(device, box);
        GroupDisplayRows.Children.Add(new TextBlock { Text = label + "连接的接口" });
        GroupDisplayRows.Children.Add(box);
    }

    private async void SaveDisplayGroup_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        var current = host ?? throw new InvalidOperationException("请先启动主控共享。");
        if (GroupMonitorChoice.SelectedItem is not DisplayMonitorInfo monitor) throw new InvalidOperationException("请先检测并选择显示器。");
        var hostName = HostName.Text.Trim();
        if (!displayChoices.TryGetValue(hostName, out var hostChoice) || hostChoice.SelectedValue is not uint hostInput || hostInput == 0)
            throw new InvalidOperationException("请选择本机连接的显示器接口。");
        var inputs = new Dictionary<string, uint>(StringComparer.Ordinal) { [hostName] = hostInput };
        foreach (var device in current.Devices)
            if (displayChoices.TryGetValue(device.Name, out var choice) && choice.SelectedValue is uint input && input != 0) inputs.Add(device.Name, input);
        if (inputs.Count == 1)
        {
            await current.SetMonitorGroupAsync(new Dictionary<string, MultiPeerMonitorSettings>(), CancellationToken.None);
            GroupDisplayStatus.Text = "已清除设备的显示器接口设置。";
            return;
        }
        var mapping = MultiDeviceDisplaySelection.Validate(monitor, hostName, current.Devices.Select(device => device.Name), inputs);
        var preferences = inputs.Where(item => item.Key != hostName).ToDictionary(item => item.Key,
            item => new MultiPeerMonitorSettings(mapping.MonitorId, hostInput, item.Value), StringComparer.Ordinal);
        await current.SetMonitorGroupAsync(preferences, CancellationToken.None);
        GroupDisplayStatus.Text = "接口已保存。自动切屏尚未启用，目前只切换键鼠。";
    });
}
