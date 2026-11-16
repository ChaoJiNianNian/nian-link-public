using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DesktopToolbox.DeskLink;

namespace NianLink.MultiDevice;

public sealed class DeviceLabelConverter : IValueConverter
{
    public Func<string, string>? Resolve { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string name ? Resolve?.Invoke(name) ?? name : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public partial class MainWindow
{
    private DeviceAliasStore? aliasStore;
    private string localIdentity = "";
    private static string DeviceKey(TrustedDeviceIdentity device) => "peer:" + DeskLinkFingerprint.Normalize(device.Fingerprint);

    private Dictionary<string, string> KnownDeviceNames()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal) { ["local"] = localIdentity };
        if (vault is not null)
            foreach (var device in vault.GetTrustedDevices()) result[DeviceKey(device)] = device.Name;
        return result;
    }

    private string DisplayDeviceName(string identity)
    {
        var names = aliasStore?.Names;
        if (names is null) return identity;
        if (string.Equals(identity, localIdentity, StringComparison.OrdinalIgnoreCase)) return names.Resolve("local", identity);
        var device = vault?.GetTrustedDevices().FirstOrDefault(item => string.Equals(item.Name, identity, StringComparison.OrdinalIgnoreCase));
        return device is null ? identity : names.Resolve(DeviceKey(device), identity);
    }

    private void RefreshDisplayNames()
    {
        HostDisplayName.Text = DisplayDeviceName(HostName.Text);
        PeerDisplayName.Text = DisplayDeviceName(PeerName.Text);
        HostDevices.Items.Refresh();
        RefreshSelectedTargetLabels();
    }

    private async void RenameLocal_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        await RenameAsync("local", HostName.Text);
    });

    private async void RenameSelected_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (HostDevices.SelectedItem is not TrustedDeviceIdentity device)
            throw new InvalidOperationException("请先在设备列表选择一台电脑。");
        await RenameAsync(DeviceKey(device), device.Name);
    });

    private async System.Threading.Tasks.Task RenameAsync(string key, string fallback)
    {
        var store = aliasStore ?? throw new InvalidOperationException("名称设置尚未准备好，请稍后重试。");
        var known = KnownDeviceNames();
        var dialog = new RenameDeviceDialog(store.Names.Resolve(key, fallback), value => store.Names.Rename(key, value, known)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await store.RenameAsync(key, dialog.DeviceName, known);
        RefreshDisplayNames();
        if (hostMiniService is not null) await hostMiniService.RefreshAsync();
        Feedback.Text = "名称已保存，仅在这台电脑显示；已有配对保持不变。";
    }
}

internal sealed class RenameDeviceDialog : Window
{
    private readonly TextBox input;
    private readonly TextBlock error;
    private readonly Func<string, DeviceAliases> validate;
    public string DeviceName => input.Text;

    public RenameDeviceDialog(string current, Func<string, DeviceAliases> validate)
    {
        this.validate = validate;
        Title = "修改电脑名称"; Width = 430; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(247, 250, 255));
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        var content = new StackPanel { Margin = new Thickness(26) };
        content.Children.Add(new TextBlock { Text = "给电脑起个好记的名字", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(30, 50, 82)) });
        content.Children.Add(new TextBlock { Text = "例如：台式机、笔记本、工作 Mac。名称仅在这台电脑显示，不影响配对。", Margin = new Thickness(0, 8, 0, 16), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(93, 114, 145)) });
        input = new TextBox { Text = current, MaxLength = 32, Padding = new Thickness(10), FontSize = 15 };
        content.Children.Add(input);
        error = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        content.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "保存名称", IsDefault = true, Padding = new Thickness(18, 8, 18, 8), Background = new SolidColorBrush(Color.FromRgb(54, 113, 229)), Foreground = Brushes.White };
        save.Click += (_, _) =>
        {
            try { _ = this.validate(input.Text); DialogResult = true; }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); content.Children.Add(buttons);
        Content = content;
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
