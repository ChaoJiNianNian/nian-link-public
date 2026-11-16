using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace NianLink.MultiDevice;

/// <summary>Target-side return action; it never sends a display-switch command.</summary>
internal sealed class PreviewReturnWindow : Window
{
    private readonly Func<string, Task> returnInput;
    private readonly Button button;
    private bool pending, closed;
    public string SessionId { get; }

    public PreviewReturnWindow(string sessionId, Func<string, Task> returnInput)
    {
        SessionId = sessionId; this.returnInput = returnInput;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        ShowActivated = false; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight; WindowStartupLocation = WindowStartupLocation.Manual;
        button = new Button { Content = "↩  归还键鼠", Padding = new Thickness(19, 11, 19, 11),
            Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, Foreground = Brushes.White,
            FontSize = 14, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0) };
        button.Click += async (_, _) => await ReturnAsync();
        Closed += (_, _) => closed = true;
        Content = new Border { CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Color.FromArgb(242, 31, 45, 68)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(100, 172, 202, 242)), BorderThickness = new Thickness(1),
            Margin = new Thickness(12), Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = .28 }, Child = button };
        Loaded += (_, _) => { var area = SystemParameters.WorkArea; Left = area.Left + (area.Width - ActualWidth) / 2; Top = area.Top + 12; };
    }

    private async Task ReturnAsync()
    {
        if (pending) return;
        pending = true; button.IsEnabled = false; button.Content = "正在归还…";
        try
        {
            await returnInput(SessionId);
            Close();
            if (!closed)
            {
                button.Content = "归还未确认 · 检查键鼠";
                button.ToolTip = "归还窗口未能关闭，请检查键鼠所在设备。";
            }
        }
        catch (Exception error)
        {
#if DISCOVERY_PORTABLE
            button.Content = "归还未确认 · 检查键鼠";
#else
            button.Content = "归还失败 · 重试";
#endif
            button.ToolTip = error.Message;
        }
        finally { pending = false; button.IsEnabled = true; }
    }
}
