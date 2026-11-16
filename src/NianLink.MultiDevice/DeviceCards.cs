using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DesktopToolbox.DeskLink;

namespace NianLink.MultiDevice;

public sealed class DevicePlatformConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string platform
        ? platform.ToLowerInvariant() switch { "mac" or "macos" => "Mac", "windows" => "Windows", _ => "电脑" } : "电脑";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DevicePlatformIconConverter : IValueConverter
{
    private static readonly Geometry Mac = Geometry.Parse("M 2,3 L 16,3 16,12 2,12 Z M 9,12 L 9,15 M 6,15 L 12,15");
    private static readonly Geometry Windows = Geometry.Parse("M 3,3 L 15,3 15,12 3,12 Z M 1,14 L 17,14 16,16 2,16 Z");
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string platform && (platform.Equals("mac", StringComparison.OrdinalIgnoreCase) || platform.Equals("macos", StringComparison.OrdinalIgnoreCase)) ? Mac : Windows;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DeviceConnectionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string name || values[1] is not DeskLinkState state) return "状态未知";
        if (state.ProcessState == DeskLinkProcessState.Stopped) return "未连接";
        if (state.ProcessState is DeskLinkProcessState.Starting or DeskLinkProcessState.Stopping) return "连接状态更新中";
        if (state.ProcessState != DeskLinkProcessState.Running || !state.NativeControlAvailable) return "状态未知";
        return state.ConnectedComputers.Contains(name, StringComparer.Ordinal) ? "● 已连接" : "未连接";
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DeviceSwitchAvailableConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 3 && values[0] is string name && values[1] is DeskLinkState state &&
        values[2] is true && state.ProcessState == DeskLinkProcessState.Running && state.NativeControlAvailable &&
        state.ConnectedComputers.Contains(name, StringComparer.Ordinal);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class MainWindow
{
    public static readonly DependencyProperty HostKeyboardSnapshotProperty = DependencyProperty.Register(
        nameof(HostKeyboardSnapshot), typeof(DeskLinkState), typeof(MainWindow));
    public DeskLinkState? HostKeyboardSnapshot { get => (DeskLinkState?)GetValue(HostKeyboardSnapshotProperty); set => SetValue(HostKeyboardSnapshotProperty, value); }

    public static readonly DependencyProperty HostSharedRunningProperty = DependencyProperty.Register(
        nameof(HostSharedRunning), typeof(bool), typeof(MainWindow));
    public bool HostSharedRunning { get => (bool)GetValue(HostSharedRunningProperty); private set => SetValue(HostSharedRunningProperty, value); }

    private void RefreshDeviceControls()
    {
        if (HostStartButton is null || HostDevices is null) return;
        bool active = host is not null && host.Devices.Count > 0;
        HostSharedRunning = active;
        bool selected = HostDevices.SelectedItem is TrustedDeviceIdentity;
        RefreshSelectedTargetLabels();
        HostEmptyState.Visibility = HostDevices.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HostEmptyTitle.Text = host is null ? "准备后查看设备" : "还没有设备";
        HostEmptyHint.Text = host is null ? "先准备主控，已有配对也会显示在这里。" : "先添加另一台电脑，完成安全配对。";
        HostEmptyButton.Content = host is null ? "准备主控" : "添加设备";
        HostEmptyButton.Tag = host is null ? "HostConnectionCard" : "HostPairingCard";
        HostStartButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        HostStartButton.IsEnabled = host is not null && HostDevices.Items.Count > 0;
        HostStopButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        HostSelectedActions.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        HostClipboard.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        HostMiniButton.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        HostMiniButton.IsEnabled = selected && active;
        HostConnectionDetails.Visibility = host is not null ? Visibility.Visible : Visibility.Collapsed;
        HostPrepareButton.Content = host is null ? "准备主控" : "主控已准备";
        HostPrepareButton.IsEnabled = host is null;
        HostRefreshNetworkButton.IsEnabled = host is null;
        var device = HostDevices.SelectedItem as TrustedDeviceIdentity;
        HostSwitchButton.IsEnabled = active && device is not null && HostKeyboardSnapshot is { ProcessState: DeskLinkProcessState.Running, NativeControlAvailable: true } snapshot && snapshot.ConnectedComputers.Contains(device.Name, StringComparer.Ordinal);
        HostQuickSend.IsEnabled = active;
        HostReturnButton.IsEnabled = active;
    }

    private void RefreshSelectedTargetLabels()
    {
        if (HostPreviewTarget is null || HostFilesTarget is null || HostDevices is null) return;
        var label = HostDevices.SelectedItem is TrustedDeviceIdentity device ? DisplayDeviceName(device.Name) : "未选择";
        HostPreviewTarget.Text = HostFilesTarget.Text = "当前设备：" + label;
    }

    private void HostMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; }
    }

    private void HostCardSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TrustedDeviceIdentity device } || !HostDevices.Items.Contains(device)) return;
        HostDevices.SelectedItem = device;
        if (!HostSwitchButton.IsEnabled) { Feedback.Text = "该设备尚未连接，无法切换键鼠。"; return; }
        SwitchHost_Click(sender, e);
    }

    private void HostCardView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TrustedDeviceIdentity device } || !HostDevices.Items.Contains(device)) return;
        HostDevices.SelectedItem = device;
        NavigateSettings_Click(sender, e);
    }
}
