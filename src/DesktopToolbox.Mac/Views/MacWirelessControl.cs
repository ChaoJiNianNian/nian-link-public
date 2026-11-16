using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DesktopToolbox.Mac.ViewModels;

namespace DesktopToolbox.Mac.Views;

/// <summary>A visible manual switch shared by the compact and expanded window layouts.</summary>
public sealed class MacWirelessControl : Border
{
    private readonly MacWirelessViewModel model;
    private readonly ToggleSwitch toggle = new()
    {
        Name = "WirelessSmoothToggle", OnContent = "", OffContent = "", MinWidth = 0,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        Padding = new Thickness(0)
    };
    private readonly TextBlock status = new() { Name = "WirelessModeStatus", FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Button retryRestore = new()
    {
        Name = "WirelessRestoreRetry", Content = "重试恢复", FontSize = 11,
        MinWidth = 0, MinHeight = 0, Height = 15, Padding = new Thickness(0),
        BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        Foreground = new SolidColorBrush(Color.Parse("#A9C7F5")),
        VerticalAlignment = VerticalAlignment.Center
    };
    private bool rendering;
    private bool lightPalette;

    public MacWirelessControl(MacWirelessViewModel model)
    {
        this.model = model; Name = "WirelessModeControl";
        Background = new SolidColorBrush(Color.Parse("#29292E"));
        BorderBrush = new SolidColorBrush(Color.Parse("#3E3E46")); BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12); Padding = new Thickness(12, 8);
        var title = new StackPanel { Spacing = 1 };
        title.Children.Add(new TextBlock { Text = "无线顺畅模式", FontSize = 14, FontWeight = FontWeight.SemiBold });
        var statusRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        statusRow.Children.Add(status); Grid.SetColumn(retryRestore, 1); statusRow.Children.Add(retryRestore);
        title.Children.Add(statusRow);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        header.Children.Add(title); Grid.SetColumn(toggle, 1); header.Children.Add(toggle);
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(header);
        var hint = new TextBlock
        {
            Name = "WirelessModeHint", FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Text = "鼠标卡顿时开启，可能影响隔空投送和无线投屏。"
        };
        hint.Classes.Add("muted"); panel.Children.Add(hint); Child = panel;
        AutomationProperties.SetName(toggle, "无线顺畅模式");
        AutomationProperties.SetName(retryRestore, "重试恢复苹果设备互联");
        ToolTip.SetTip(retryRestore, "保持已关闭。再次尝试恢复隔空投送和无线投屏，可能需要 Mac 系统授权。");
        retryRestore.Click += async (_, _) => await model.RetryRestoreAsync();
        ToolTip.SetTip(toggle, "记住你的选择，重启或唤醒后自动保持。开启：暂停 AWDL；关闭：停止保持并恢复苹果设备互联。更改时需要 Mac 系统授权。");
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (rendering) return;
            var requested = toggle.IsChecked == true;
            toggle.IsEnabled = false;
            // Finish the framework's knob update before resetting to the confirmed state.
            Dispatcher.UIThread.Post(async () =>
            {
                Render();
                await model.SetSmoothModeAsync(requested);
            });
        };
        model.PropertyChanged += (_, _) => Render();
        Render();
    }
    internal void SetLightPalette(bool value)
    {
        lightPalette = value;
        Background = new SolidColorBrush(Color.Parse(value ? "#FBFDFF" : "#29292E"));
        BorderBrush = new SolidColorBrush(Color.Parse(value ? "#D7E3F2" : "#3E3E46"));
        retryRestore.Foreground = new SolidColorBrush(Color.Parse(value ? "#397DF1" : "#A9C7F5"));
        Render();
    }
    private void Render()
    {
        rendering = true;
        try
        {
            toggle.IsChecked = model.IsSmoothEnabled; toggle.IsEnabled = model.CanChange;
            retryRestore.IsVisible = model.NeedsRestoreRetry; retryRestore.IsEnabled = model.CanRetryRestore;
            status.Text = model.NeedsRestoreRetry || model.ErrorMessage.Length == 0 ? model.StatusMessage : model.ErrorMessage;
            status.Foreground = new SolidColorBrush(Color.Parse(model.ErrorMessage.Length > 0
                ? lightPalette ? "#B53C46" : "#FFB4AB"
                : lightPalette ? "#7186A3" : "#A9A9B4"));
            ToolTip.SetTip(status, model.ErrorMessage.Length > 0 ? model.ErrorMessage : status.Text);
        }
        finally { rendering = false; }
    }
}
