using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DesktopToolbox.Mac.Views;

public sealed partial class MacMultiDeviceWindow
{
    private sealed record CandidateDialog(Window Window, Button Accept, Button Cancel);

    // The confirmation text remains selectable and scrollable so full fingerprints stay visible.
    private static CandidateDialog CreateCandidateDialog(string title, string message, string acceptLabel)
    {
        var accept = new Button { Content = acceptLabel }; accept.Classes.Add("accent");
        var cancel = new Button { Content = "取消" }; cancel.Classes.Add("secondary");
        var heading = new StackPanel { Spacing = 4, Margin = new Thickness(24, 20, 24, 14) };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1B2C43")), TextWrapping = TextWrapping.Wrap });
        var guidance = Label("请仔细阅读后选择。"); heading.Children.Add(guidance);
        var text = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#243A59")), Margin = new Thickness(18) };
        var scroll = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var card = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.Parse("#D9E5F4")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Margin = new Thickness(24, 0) , Child = scroll };
        var footer = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 14, 24, 20) };
        accept.Margin = new Thickness(0, 0, 8, 0); footer.Children.Add(accept); footer.Children.Add(cancel);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(heading); Grid.SetRow(card, 1); layout.Children.Add(card); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        var dialog = new Window { Title = title, Width = 520, Height = 400, MinWidth = 440, MinHeight = 320,
            Content = layout, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#F6F9FF")) };
        accept.IsEnabled = false;
        void RefreshReadState()
        {
            var ready = scroll.Viewport.Height > 0 && scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 1;
            accept.IsEnabled = ready;
            guidance.Text = ready ? "请仔细阅读后选择。" : "请向下滚动查看完整信息后再确认。";
        }
        scroll.ScrollChanged += (_, _) => RefreshReadState();
        scroll.LayoutUpdated += (_, _) => RefreshReadState();
        MacSettingsPalette.Apply(dialog, true);
        ApplyBlueAccent(dialog);
        return new CandidateDialog(dialog, accept, cancel);
    }

    private static void ApplyBlueAccent(Window window)
    {
        foreach (var (key, color) in new[]
        {
            ("AccentButtonBackground", "#356FEB"), ("AccentButtonBackgroundPointerOver", "#2E63D5"),
            ("AccentButtonBackgroundPressed", "#2855B9"), ("AccentButtonForeground", "#FFFFFF"),
            ("AccentButtonForegroundPointerOver", "#FFFFFF"), ("AccentButtonForegroundPressed", "#FFFFFF"),
            ("AccentButtonBorderBrush", "#356FEB"), ("AccentButtonBorderBrushPointerOver", "#2E63D5"),
            ("AccentButtonBorderBrushPressed", "#2855B9")
        }) window.Resources[key] = new SolidColorBrush(Color.Parse(color));
    }
}
