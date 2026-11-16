using System.IO;
using System.Windows;
using DesktopToolbox.App.DeskLink;
using DesktopToolbox.Core;
using DesktopToolbox.DeskLink;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    private MultiDeviceMiniService? hostMiniService;
    private MiniCollaborationWindow? hostMiniWindow;
    private int? hostEngineProcessId;
    private bool syncingMiniSelection;

    private async void OpenHostMini_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (hostMiniWindow is { } existing) { await existing.ShowMiniAsync(); return; }
        var current = host ?? throw new InvalidOperationException("请先准备并启动主控共享。");
        var credentials = vault ?? throw new InvalidOperationException("请先准备主控。");
        var state = await current.GetKeyboardStateAsync(CancellationToken.None);
        if (state.ProcessState != DeskLinkProcessState.Running) throw new InvalidOperationException("请先启动共享。");
        var selected = HostDevices.SelectedItem as TrustedDeviceIdentity
            ?? throw new InvalidOperationException("请先选择一台已配对设备。");
        if (hostDetached is not null) throw new InvalidOperationException("请先关闭设置页中的放大预览，再打开互联小窗。");
        hostEngineProcessId = state.ProcessId;
        hostViewEnabled = false; HostViewPreview.IsChecked = false; previewEpoch++;
        await ClearHostViewAsync();

        var service = new MultiDeviceMiniService(current, credentials, HostName.Text.Trim(),
            () => PreviewForeground.IsOwnedCaptureWindow(hostEngineProcessId, current.OwnsLiveKeyboardProcess));
        MiniCollaborationWindow? window = null;
        try
        {
            service.DeviceSelected += OnMiniDeviceSelected;
            await service.SelectDeviceAsync(selected.Name);
            await service.RefreshAsync();
            var miniRoot = Path.Combine(root, "mini");
            window = new MiniCollaborationWindow(service, new JsonFileStore(new AppPaths(miniRoot)),
                error => Dispatcher.BeginInvoke(() => ShowFailure(error, "互联小窗")),
                Path.Combine(miniRoot, "webview"), DisplayDeviceName);
            window.ConnectionSettingsRequested += () => { Show(); WindowState = WindowState.Normal; Activate(); };
            await window.StartAsync();
            hostMiniService = service; hostMiniWindow = window;
            await window.ShowMiniAsync();
        }
        catch
        {
            service.DeviceSelected -= OnMiniDeviceSelected;
            if (window is not null) await window.StopAsync();
            await service.DisposeAsync();
            throw;
        }
    });

    private void OnMiniDeviceSelected(string name)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnMiniDeviceSelected(name)); return; }
        var selected = HostDevices.Items.OfType<TrustedDeviceIdentity>().FirstOrDefault(device => device.Name == name);
        if (selected is null) return;
        syncingMiniSelection = true;
        try { HostDevices.SelectedItem = selected; hostClipboardTarget = null; hostClipboardCursor = null; }
        finally { syncingMiniSelection = false; }
    }

    private async Task<bool> SynchronizeMiniSelectionAsync()
    {
        if (syncingMiniSelection || hostMiniService is not { } mini || HostDevices.SelectedItem is not TrustedDeviceIdentity selected) return true;
        if (mini.Snapshot.SelectedDevice == selected.Name) return true;
        try
        {
            if (hostMiniWindow is not null) await hostMiniWindow.ClosePreviewForDeviceChangeAsync();
            await mini.SelectDeviceAsync(selected.Name);
            await mini.RefreshAsync();
            return true;
        }
        catch (Exception error)
        {
            ShowFailure(error, "切换设备");
            if (mini.Snapshot.SelectedDevice is { } previous) OnMiniDeviceSelected(previous);
            return false;
        }
    }

    private async Task CloseHostMiniAsync()
    {
        var window = hostMiniWindow;
        var service = hostMiniService;
        if (window is not null) await window.ClosePreviewForDeviceChangeAsync();
        if (window is not null) await window.StopAsync();
        if (service is not null)
        {
            service.DeviceSelected -= OnMiniDeviceSelected;
            await service.DisposeAsync();
        }
        hostMiniWindow = null; hostMiniService = null;
    }
}
