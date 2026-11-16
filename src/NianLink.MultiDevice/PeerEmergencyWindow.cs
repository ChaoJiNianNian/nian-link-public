#if NIAN_PEER_EMERGENCY_RESEARCH
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;

namespace NianLink.MultiDevice;

// Candidate-only, independent of the preview-session window. Never captures a
// connection: its callback resolves the current connection when clicked.
internal sealed class PeerEmergencyWindow : Window
{
    private readonly Func<Task> returnInput;
    private readonly Button toggle, returnButton;
    private readonly TextBlock status, warning;
    private readonly StackPanel details;
    private bool pending, allowClose;

    internal PeerEmergencyWindow(Func<Task> returnInput)
    {
        this.returnInput = returnInput;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        ShowActivated = false; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual; Background = Brushes.Transparent;
        AllowsTransparency = true;
        toggle = new Button { Content = "↩", ToolTip = "展开归还主控", MinWidth = 34, MinHeight = 34 };
        returnButton = new Button { Content = "归还主控", Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        warning = new TextBlock { Text = "研究测试：尝试归还可能同时切换显示器；超时后请求仍可能执行。", Foreground = Brushes.White, MaxWidth = 240, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        status = new TextBlock { Text = "需要时点击归还主控。", Foreground = Brushes.White, MaxWidth = 240, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        details = new StackPanel { Visibility = Visibility.Collapsed, Children = { returnButton, warning, status } };
        var panel = new StackPanel { Children = { toggle, details } };
        var border = new Border { Background = new SolidColorBrush(Color.FromRgb(31, 45, 68)), CornerRadius = new CornerRadius(9), Padding = new Thickness(5), Child = panel };
        border.MouseLeftButtonDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, border)) DragMove(); };
        Content = border;
        toggle.Click += (_, _) => ToggleExpanded();
        returnButton.Click += async (_, _) => await ReturnAsync();
        Loaded += (_, _) => { var area = SystemParameters.WorkArea; Left = area.Right - ActualWidth - 12; Top = area.Top + 12; };
    }

    private void ToggleExpanded()
    {
        details.Visibility = details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        toggle.Content = details.Visibility == Visibility.Visible ? "收起" : "↩";
        toggle.ToolTip = details.Visibility == Visibility.Visible ? "收起归还入口" : "展开归还主控";
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!allowClose) e.Cancel = true;
    }

    internal void Release() { allowClose = true; Close(); }

    internal void ShowConnectionState(bool connected, bool unconfirmed)
    {
        status.Text = !connected ? unconfirmed ? "连接已停止，键鼠归还尚未确认。请检查键鼠所在设备。" : "未连接主控。" :
            unconfirmed ? "键鼠归还尚未确认；尝试归还可能切换显示器，超时后请求仍可能执行。" : "研究测试：尝试归还可能同时切换显示器；超时后请求仍可能执行。";
    }

    private async Task ReturnAsync()
    {
        if (pending) return;
        pending = true; returnButton.IsEnabled = false; status.Text = "正在尝试归还…";
        try { await returnInput(); status.Text = "上次归还已确认；当前状态请查看连接页面。"; }
        catch (Exception error) { status.Text = error.Message; }
        finally { pending = false; returnButton.IsEnabled = true; }
    }
}
#endif
