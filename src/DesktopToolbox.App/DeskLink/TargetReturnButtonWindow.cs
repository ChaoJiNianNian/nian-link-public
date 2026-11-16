using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DesktopToolbox.App.Hosting;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace DesktopToolbox.App.DeskLink;

/// <summary>Independent target-side surface; it never switches the display input.</summary>
internal sealed class TargetReturnButtonWindow : Window
{
    private readonly HostController host;
    private readonly Button button;
    private readonly TextBlock label;
    private string? sessionId;
    private bool returning;

    public TargetReturnButtonWindow(HostController host)
    {
        this.host = host;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        label = new TextBlock { Text = "↩  归还键鼠", FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        button = new Button { Content = label, Padding = new Thickness(19, 11, 19, 11), Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            ToolTip = "结束临时操作，键鼠回到另一台电脑" };
        button.Click += OnReturn;
        Content = new Border { CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Color.FromArgb(242, 31, 45, 68)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(100, 172, 202, 242)), BorderThickness = new Thickness(1),
            Margin = new Thickness(12), Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = .28 }, Child = button };
        Loaded += (_, _) => { var area = SystemParameters.WorkArea; Left = area.Left + (area.Width - ActualWidth) / 2; Top = area.Top + 12; };
    }

    public void UpdateSession(string id)
    {
        if (sessionId != id) { sessionId = id; returning = false; button.IsEnabled = true; label.Text = "↩  归还键鼠"; button.ToolTip = "结束临时操作，键鼠回到另一台电脑"; }
        if (!IsVisible) Show();
    }

    private async void OnReturn(object sender, RoutedEventArgs e)
    {
        var id = sessionId;
        if (returning || id is null || TargetReturnButtonPolicy.SessionId(host.CurrentDeskSwitch, host.DeskLocalName) != id) return;
        returning = true; button.IsEnabled = false;
        try
        {
            var result = await host.ReturnPreviewAsync(id);
            if (sessionId != id) return;
            if (result.HasSession)
            {
                label.Text = "归还失败 · 重试";
                button.ToolTip = "尚未确认归还，请重试或使用返回快捷键。";
            }
        }
        catch (Exception error)
        {
            host.Log(error);
            if (sessionId == id) { label.Text = "归还失败 · 重试"; button.ToolTip = "归还未完成，请重试或使用返回快捷键。"; }
        }
        finally
        {
            if (sessionId == id) { returning = false; button.IsEnabled = true; }
        }
    }
}
