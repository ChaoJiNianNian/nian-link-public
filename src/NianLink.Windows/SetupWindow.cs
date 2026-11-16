using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopToolbox.Core;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Windows.DeskSwitch;

namespace NianLink.Windows;

// Standalone configuration entry for the same Deskflow-backed session as the mini window.
internal sealed partial class SetupWindow : Window
{
    private readonly DeskSwitchModule module;
    private readonly IIntegratedDeskSwitchService keyboard;
    private readonly StandaloneSettingsCoordinator<DeskSwitchSettings> settingsCoordinator;
    private readonly Func<Task> showMini, exit;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly StandaloneOperationGate operationGate = new();
    private readonly DisplayDetectionCoordinator<DisplayMonitorInfo[]> monitorDetection;
    private DeskSwitchSettings current;
    private DisplayMonitorInfo[]? detectedMonitors;
    private string? choiceMonitorId;
    private string? exportedPairingPath;
    private bool busy, allowClose, shutdownPending, applyingMonitorResults, initialDetectionStarted;

    public SetupWindow(DeskSwitchModule module, IIntegratedDeskSwitchService keyboard, IDisplayBridge displayBridge, DeskSwitchSettings initial,
        Action<DeskSwitchSettings> updated, Func<Task> reloadCollaboration, Func<Task> showMini, Func<Task> exit)
    {
        this.module = module; this.keyboard = keyboard; this.showMini = showMini; this.exit = exit; current = initial;
        monitorDetection = new(token => displayBridge.ListAsync(token));
        settingsCoordinator = new(value => InvokeAsync("deskSwitch.save", new { settings = value }),
            value => { current = value; updated(value); }, reloadCollaboration, () => InvokeAsync("deskSwitch.start"));
        InitializeComponent();
        LocalName.Text = initial.LocalName; RemoteName.Text = initial.RemoteName;
        DisplayLink.IsChecked = initial.LinkDisplay;
        if (initial.LinkDisplay) MonitorFeedback.Text = "已保存显示器设置；需要更换设备时请重新检测。";
        WindowsInputChoice.SelectionChanged += (_, _) => OnDisplayEdited();
        MacInputChoice.SelectionChanged += (_, _) => OnDisplayEdited();
        DisplayLink.Checked += (_, _) => OnDisplayEdited();
        DisplayLink.Unchecked += (_, _) => OnDisplayEdited();
        UpdateDisplayControls();
        Loaded += async (_, _) =>
        {
            if (initialDetectionStarted) return;
            initialDetectionStarted = true;
            await DetectWithFeedbackAsync();
        };
        Closing += (_, args) => { if (!allowClose) { args.Cancel = true; Hide(); } };
        timer.Tick += (_, _) => RefreshStatus(); timer.Start(); RefreshStatus();
    }
    private static JsonElement Json(object? value = null) => JsonSerializer.SerializeToElement(value ?? new { }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private async Task InvokeAsync(string method, object? value = null) { await module.InvokeAsync(method, Json(value), CancellationToken.None); RefreshStatus(); }
    private DeskSwitchSettings ReadForm(bool allowSavedDisplayForStart = false)
    {
        var linkDisplay = DisplayLink.IsChecked == true;
        var selected = MonitorList.SelectedItem as MonitorOption;
        var useSaved = linkDisplay && allowSavedDisplayForStart &&
            DisplayInputSelection.CanUseSavedForStart(current, selected?.Monitor, detectedMonitors);
        if (linkDisplay && selected is null && !useSaved)
            throw new InvalidOperationException("请先检测并选择连接两台电脑的显示器。 ");
        var monitorId = linkDisplay && !useSaved ? selected!.Monitor.Id : current.MonitorId;
        var (a, b) = linkDisplay && !useSaved
            ? DisplayInputSelection.Validate(selected!.Monitor, WindowsInputChoice.SelectedValue as uint?, MacInputChoice.SelectedValue as uint?)
            : (current.LocalInput, current.RemoteInput);
        var next = (current with { LocalName = LocalName.Text.Trim(), RemoteName = RemoteName.Text.Trim(), LinkDisplay = linkDisplay,
            MonitorId = monitorId, LocalInput = a, RemoteInput = b }).Validate();
        if (linkDisplay && !useSaved && detectedMonitors is not null)
            _ = DisplaySettingsImport.Prepare(next, new LegacyDisplaySettings { LinkDisplay = true, MonitorId = monitorId, LocalInput = a, RemoteInput = b }, detectedMonitors);
        if (linkDisplay && DeskLinkDisplayBindingFactory.FromSettings(next) is null)
            throw new InvalidOperationException("未能识别 Mac 回切所需的显示器信息，请重新检测或检查连接。 ");
        return next;
    }
    private Task SaveAsync(bool allowDisplayChange = false) => WithFormDisabledAsync(async () =>
    {
        monitorDetection.Invalidate();
        var pending = ReadForm(allowSavedDisplayForStart: !allowDisplayChange);
        if (!allowDisplayChange) DisplayMappingGuard.EnsureUnchangedForStart(pending, current);
        await settingsCoordinator.SaveAsync(pending);
    });
    private Task StartAsync() => WithFormDisabledAsync(async () =>
    {
        monitorDetection.Invalidate();
        var pending = ReadForm(allowSavedDisplayForStart: true);
        DisplayMappingGuard.EnsureUnchangedForStart(pending, current);
        await settingsCoordinator.StartAsync(pending, current);
    });

    internal Task StartFromMiniAsync(Func<Task> start, CancellationToken token)
        => WithFormDisabledAsync(async () =>
        {
            monitorDetection.Invalidate();
            var pending = ReadForm(allowSavedDisplayForStart: true);
            DisplayMappingGuard.EnsureUnchangedForStart(pending, current);
            await settingsCoordinator.SaveIfChangedAsync(pending, current);
            token.ThrowIfCancellationRequested();
            await start();
        }, token);

    private Task WithFormDisabledAsync(Func<Task> action, CancellationToken token = default)
        => operationGate.RunAsync(async () =>
        {
            if (shutdownPending) throw new InvalidOperationException("正在停止共享，请稍后重试。");
            var enabled = IsEnabled; IsEnabled = false;
            try { await action(); }
            finally { IsEnabled = enabled; }
        }, token);
    internal async Task<ShutdownPreparation> PrepareShutdownAsync(Func<Task<ShutdownPreparation>> prepare)
    {
        shutdownPending = true;
        ShutdownPreparation result = default;
        try
        {
            await operationGate.RunAsync(async () => { result = await prepare(); });
            if (!result.CanExit) shutdownPending = false;
            return result;
        }
        catch { shutdownPending = false; throw; }
    }
    private async Task ConfirmAsync()
    {
        var pending = keyboard.Snapshot.Session.PendingPeerFingerprint;
        if (string.IsNullOrWhiteSpace(pending)) throw new InvalidOperationException("当前没有等待确认的 Mac 指纹。");
        await InvokeAsync("deskSwitch.confirmPeer", new { fingerprint = pending });
    }
    private async Task ExportPairingAsync()
    {
        exportedPairingPath = null;
        RevealExport.Visibility = Visibility.Collapsed;
        ExportFeedback.Visibility = Visibility.Visible;
        ExportFeedback.Text = "请选择配对文件的保存位置。";
        try
        {
            var result = await module.InvokeAsync("deskSwitch.exportPairing", Json(), CancellationToken.None);
            var response = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (response.GetProperty("canceled").GetBoolean())
            {
                ExportFeedback.Text = "已取消选择，未导出新的配对文件。";
                return;
            }
            var path = response.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("没有收到配对文件的保存位置，请重试。");
            exportedPairingPath = path;
            ExportFeedback.Text = $"已保存到：{path}\n请通过微信等方式私下发送给自己的 Mac。";
            RevealExport.Visibility = Visibility.Visible;
        }
        catch
        {
            ExportFeedback.Text = "导出未完成，请检查提示后重试。";
            throw;
        }
        finally { RefreshStatus(); }
    }
    private async Task RevealPairingAsync()
    {
        if (string.IsNullOrWhiteSpace(exportedPairingPath)) return;
        try
        {
            var result = await module.InvokeAsync("deskSwitch.revealPairing", Json(), CancellationToken.None);
            var response = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (!response.GetProperty("opened").GetBoolean())
            {
                var detail = response.TryGetProperty("message", out var message) ? message.GetString() : null;
                ExportFeedback.Text = $"配对文件已保存到：{exportedPairingPath}\n无法打开所在文件夹。{detail}";
            }
        }
        catch (Exception error)
        {
            ExportFeedback.Text = $"配对文件已保存到：{exportedPairingPath}\n无法打开所在文件夹：{error.Message}";
        }
    }
    private async Task ExportMacAsync()
    {
        MacExportFeedback.Visibility = Visibility.Visible;
        MacExportFeedback.Text = "正在检查 Mac 测试安装包。";
        try
        {
            var result = await module.InvokeAsync("deskSwitch.exportMacInstaller", Json(), CancellationToken.None);
            var response = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (response.GetProperty("canceled").GetBoolean()) { MacExportFeedback.Text = "已取消导出。"; return; }
            var path = response.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("没有收到安装包的保存位置，请重试。");
            MacExportFeedback.Text = $"已保存到：{path}\n这是未公证的测试包，请通过微信私发到自己的 Mac。";
        }
        catch
        {
            MacExportFeedback.Text = "导出未完成，请检查提示。";
            throw;
        }
    }
    private sealed record MonitorOption(DisplayMonitorInfo Monitor)
    {
        public string Label => Monitor.Name + (Monitor.CanReadInput ? " · 已检测" : " · 暂无法读取输入");
    }
    private async Task<bool> DetectMonitorsAsync()
    {
        if (keyboard.IsRunning)
        {
            MonitorFeedback.Text = "请先停止共享，再检测显示器。";
            return false;
        }
        MonitorFeedback.Text = "正在检测显示器，不影响其他操作…";
        var detection = await monitorDetection.DetectAsync();
        if (!detection.Accepted || keyboard.IsRunning || shutdownPending) return false;
        var list = detection.Value ?? [];
        applyingMonitorResults = true;
        try
        {
        var desired = (MonitorList.SelectedItem as MonitorOption)?.Monitor.Id ?? current.MonitorId;
        var previousWindows = WindowsInputChoice.SelectedValue as uint?;
        var previousMac = MacInputChoice.SelectedValue as uint?;
        detectedMonitors = list;
        var options = list.Select(value => new MonitorOption(value)).ToArray();
        MonitorList.ItemsSource = options;
        var matches = options.Where(option => string.Equals(option.Monitor.Id, desired, StringComparison.Ordinal)).ToArray();
        MonitorList.SelectedItem = matches.Length == 1 ? matches[0] : options.Length == 1 ? options[0] : null;
        MonitorFeedback.Text = options.Length == 0
            ? DisplayInputSelection.CanUseSavedForStart(current, null, detectedMonitors)
                ? "暂时未检测到显示器，可继续使用上次保存的设置；切屏效果请确认。"
                : "没有检测到显示器，请检查连接。"
            : "请选择连接两台电脑的显示器。";
        if (MonitorList.SelectedItem is MonitorOption selected)
        {
            ShowMonitorInputs(selected.Monitor);
            if (string.Equals(selected.Monitor.Id, desired, StringComparison.Ordinal))
            {
                WindowsInputChoice.SelectedValue = previousWindows ?? (current.LinkDisplay ? current.LocalInput : null);
                MacInputChoice.SelectedValue = previousMac ?? (current.LinkDisplay ? current.RemoteInput : null);
            }
        }
        UpdateDisplayControls();
        return true;
        }
        finally { applyingMonitorResults = false; }
    }
    private async Task DetectWithFeedbackAsync()
    {
        try { await DetectMonitorsAsync(); }
        catch (Exception) { MonitorFeedback.Text = "暂时无法检测显示器，请检查连接后重试。"; }
    }
    private void OnDisplayEdited()
    {
        if (!applyingMonitorResults) monitorDetection.Invalidate();
        UpdateDisplayControls();
    }
    private void ShowMonitorInputs(DisplayMonitorInfo monitor)
    {
        var previousWindows = WindowsInputChoice.SelectedValue as uint?;
        var previousMac = MacInputChoice.SelectedValue as uint?;
        var sameMonitor = string.Equals(choiceMonitorId, monitor.Id, StringComparison.Ordinal);
        var choices = DisplayInputSelection.Choices(monitor);
        choiceMonitorId = monitor.Id;
        WindowsInputChoice.ItemsSource = choices;
        MacInputChoice.ItemsSource = choices;
        if (sameMonitor)
        {
            WindowsInputChoice.SelectedValue = previousWindows;
            MacInputChoice.SelectedValue = previousMac;
        }
        else if (string.Equals(current.MonitorId, monitor.Id, StringComparison.Ordinal) && current.LinkDisplay)
        {
            WindowsInputChoice.SelectedValue = current.LocalInput;
            MacInputChoice.SelectedValue = current.RemoteInput;
        }
        MonitorFeedback.Text = choices.Length > 0
            ? "已检测到可用接口。请根据实际连接，分别选择 Windows 和 Mac 使用的接口。"
            : DisplayInputSelection.CanUseSavedForStart(current, monitor, detectedMonitors)
                ? "暂时无法识别接口，可继续使用上次保存的设置；如需更改，请检查连接后重新检测。"
                : "无法识别可用的显示器接口。请检查显示器连接与 DDC/CI 设置后重新检测。";
        UpdateDisplayControls();
    }
    private void UpdateDisplayControls()
    {
        if (MonitorList is null || WindowsInputChoice is null || MacInputChoice is null || DisplayLink is null || SaveDisplayButton is null) return;
        var choicesReady = MonitorList.SelectedItem is MonitorOption option && DisplayInputSelection.Choices(option.Monitor).Length >= 2;
        WindowsInputChoice.IsEnabled = MacInputChoice.IsEnabled = DisplayLink.IsChecked == true && choicesReady;
        var windows = WindowsInputChoice.SelectedValue as uint?;
        var mac = MacInputChoice.SelectedValue as uint?;
        SaveDisplayButton.IsEnabled = DisplayLink.IsChecked != true || (choicesReady && windows is not null && mac is not null && windows != mac);
        if (DisplayLink.IsChecked == true && windows is not null && windows == mac)
            MonitorFeedback.Text = "两台电脑请选择不同的接口。";
        else if (MonitorFeedback.Text == "两台电脑请选择不同的接口。")
            MonitorFeedback.Text = "请选择 Windows 和 Mac 各自连接的接口。";
    }
    private async Task ImportDisplayAsync()
    {
        if (keyboard.IsRunning) throw new InvalidOperationException("请先停止共享，再导入显示器设置。 ");
        long? importTicket = null;
        try
        {
            if (!await DetectMonitorsAsync()) return;
            if (keyboard.IsRunning) throw new InvalidOperationException("请先停止共享，再导入显示器设置。 ");
            importTicket = monitorDetection.CaptureVersion();
            var store = new JsonFileStore(AppPaths.Default);
            var legacy = await store.LoadAsync<LegacyDisplaySettings>("desk-switch.json",
                () => throw new FileNotFoundException("没有找到旧工具箱的显示器设置，请在本页面手动设置。"));
            if (!monitorDetection.IsCurrent(importTicket.Value) || keyboard.IsRunning || shutdownPending) return;
            var prepared = DisplaySettingsImport.Prepare(current, legacy, detectedMonitors ?? []);
            if (prepared.LinkDisplay && DeskLinkDisplayBindingFactory.FromSettings(prepared) is null)
                throw new InvalidOperationException("旧设置无法识别 Mac 回切所需的显示器信息，请手动检测。 ");
            var options = ((IEnumerable<MonitorOption>)MonitorList.ItemsSource).ToArray();
            var match = options.Single(option => string.Equals(option.Monitor.Id, prepared.MonitorId, StringComparison.Ordinal));
            _ = DisplayInputSelection.Validate(match.Monitor, prepared.LocalInput, prepared.RemoteInput);
            applyingMonitorResults = true;
            try
            {
                MonitorList.SelectedItem = match;
                DisplayLink.IsChecked = prepared.LinkDisplay;
                WindowsInputChoice.SelectedValue = prepared.LocalInput; MacInputChoice.SelectedValue = prepared.RemoteInput;
                MonitorFeedback.Text = "已匹配旧显示器和接口；请核对后保存。";
            }
            finally { applyingMonitorResults = false; }
        }
        catch
        {
            if (importTicket is long ticket && !monitorDetection.IsCurrent(ticket)) return;
            MonitorFeedback.Text = "未导入设置，已保存的配置未改变，请检查提示。";
            throw;
        }
    }
    private void MonitorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowsInputChoice is null || MacInputChoice is null || MonitorFeedback is null) return;
        if (!applyingMonitorResults) monitorDetection.Invalidate();
        if (MonitorList.SelectedItem is MonitorOption selected) ShowMonitorInputs(selected.Monitor);
        else
        {
            choiceMonitorId = null;
            WindowsInputChoice.ItemsSource = MacInputChoice.ItemsSource = null;
            UpdateDisplayControls();
        }
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return; busy = true;
        try { await action(); }
        catch (Exception error) { System.Windows.MessageBox.Show(this, error.Message, "年年互联", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { busy = false; }
    }
    private async void Start_Click(object sender, RoutedEventArgs e) => await RunAsync(StartAsync);
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunAsync(() => SaveAsync());
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunAsync(() => InvokeAsync("deskSwitch.stop"));
    private async void Export_Click(object sender, RoutedEventArgs e) => await RunAsync(ExportPairingAsync);
    private async void ExportMac_Click(object sender, RoutedEventArgs e) => await RunAsync(ExportMacAsync);
    private async void RevealExport_Click(object sender, RoutedEventArgs e) => await RunAsync(RevealPairingAsync);
    private async void Confirm_Click(object sender, RoutedEventArgs e) => await RunAsync(ConfirmAsync);
    private async void OpenMini_Click(object sender, RoutedEventArgs e) => await RunAsync(showMini);
    private async void Monitors_Click(object sender, RoutedEventArgs e) => await DetectWithFeedbackAsync();
    private async void ImportDisplay_Click(object sender, RoutedEventArgs e)
    {
        try { await ImportDisplayAsync(); }
        catch (Exception error) { System.Windows.MessageBox.Show(this, error.Message, "年年互联", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async void SaveDisplay_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await SaveAsync(allowDisplayChange: true);
        MonitorFeedback.Text = "显示器设置已保存。请重新启动共享，并重新导出配对文件发送给 Mac。";
    });
    private async void Exit_Click(object sender, RoutedEventArgs e) => await RunAsync(exit);
    public void RefreshStatus()
    {
        var packageAvailable = MacPackageAvailability.Present(AppContext.BaseDirectory, File.Exists);
        MacExportButton.IsEnabled = packageAvailable;
        MacExportButton.Content = packageAvailable ? "导出 Mac 测试安装包" : "Mac 安装包准备中";
        var state = keyboard.Snapshot; var session = state.Session;
        StatusText.Text = session.ProcessState switch
        {
            DeskLinkProcessState.Starting => "正在启动共享",
            DeskLinkProcessState.Stopping => "正在停止共享",
            DeskLinkProcessState.Faulted => "共享遇到问题",
            DeskLinkProcessState.Running when session.ConnectionState == DeskLinkConnectionState.Connected => "Mac 已连接",
            DeskLinkProcessState.Running => "共享已开启 · 等待 Mac 连接",
            _ => "共享尚未开启"
        };
        StatusDetail.Text = state.LastError ?? session.LastError ?? (session.ConnectionState == DeskLinkConnectionState.Connected
            ? "两台设备已连接，可以打开互联小窗。" : "按下面的步骤，连接你自己的 Mac。 ");
        FingerprintText.Text = $"Windows 键鼠指纹：{session.LocalFingerprint ?? "启动共享后显示"}\n\nWindows 控制通道指纹：{state.ControlFingerprint ?? "启动共享后显示"}\n\n等待确认的 Mac 键鼠指纹：{session.PendingPeerFingerprint ?? "Mac 请求连接后显示"}";
    }
    public void PermitClose() { allowClose = true; timer.Stop(); monitorDetection.Dispose(); }
}
