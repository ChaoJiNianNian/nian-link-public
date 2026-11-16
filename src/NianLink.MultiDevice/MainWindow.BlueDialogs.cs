using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    private bool AskBlueConfirmation(string title, string message, string acceptLabel, CancellationToken cancellation)
        => ShowBlueDialog(title, message, acceptLabel, true, cancellation);

    private void ShowBlueNotice(string title, string message)
        => ShowBlueDialog(title, message, "确定", false, CancellationToken.None);

    private bool ShowBlueDialog(string title, string message, string acceptLabel, bool allowCancel, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested) return false;
        var accept = new Button { Content = acceptLabel, MinWidth = 100, Margin = new Thickness(0, 16, 8, 0), Style = (Style)FindResource("PrimaryButton") };
        var cancel = new Button { Content = "取消", MinWidth = 80, Margin = new Thickness(0, 16, 0, 0), IsCancel = true,
            Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed, Style = (Style)FindResource(typeof(Button)) };
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(27, 44, 67)), Margin = new Thickness(0, 0, 0, 14) });
        body.Children.Add(new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(228, 235, 244)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Padding = new Thickness(18),
            Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(27, 44, 67)) } });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(accept); actions.Children.Add(cancel); body.Children.Add(actions);
        var dialog = new Window { Title = title, Width = 560, Height = 460, MinWidth = 460, MinHeight = 360, Owner = this,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"), Background = new SolidColorBrush(Color.FromRgb(246, 249, 255)),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        dialog.Loaded += (_, _) =>
        {
            var safeFocus = allowCancel ? cancel : accept;
            System.Windows.Input.FocusManager.SetFocusedElement(dialog, safeFocus);
            safeFocus.Focus();
        };
        accept.Click += (_, _) => dialog.DialogResult = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        using var registration = cancellation.Register(() => dialog.Dispatcher.BeginInvoke(new Action(() =>
        { if (dialog.IsVisible) dialog.DialogResult = false; })));
        if (cancellation.IsCancellationRequested) return false;
        return dialog.ShowDialog() == true && !cancellation.IsCancellationRequested;
    }
}
