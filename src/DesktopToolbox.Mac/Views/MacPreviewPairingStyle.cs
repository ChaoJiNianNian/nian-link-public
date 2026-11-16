using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nian.Input.Control;

namespace DesktopToolbox.Mac.Views;

internal static class MacPreviewPairingStyle
{
    internal static SolidColorBrush Brush(string color)=>new(Color.Parse(color));
    internal static TextBlock Text(string value,int size=13)=>new(){Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap};
    internal static SelectableTextBlock Fingerprint(string name)=>new(){Name=name,FontSize=12,TextWrapping=TextWrapping.Wrap,FontFamily=new("Menlo, Consolas, monospace")};
    internal static Button Button(string label,string name)=>new(){Content=label,Name=name,MinHeight=38,Padding=new(14,8),CornerRadius=new(10),FontSize=13,HorizontalContentAlignment=HorizontalAlignment.Center};
    internal static Border Card(Control content)=>new(){Child=content,Padding=new(18),CornerRadius=new(16),BorderThickness=new(1)};
    internal static StackPanel Stack(params Control[] controls)
    {var result=new StackPanel{Spacing=10};foreach(var control in controls)result.Children.Add(control);return result;}
    internal static void ApplyButton(Button button,PreviewControlPalette palette,bool accent=false)
    {
        var background=accent?palette.Button:palette.Soft;var foreground=accent?palette.ButtonText:palette.Text;
        button.Background=Brush(background);button.Foreground=Brush(foreground);button.BorderBrush=Brushes.Transparent;
        foreach(var suffix in new[]{"","PointerOver","Pressed","Disabled"})
        {button.Resources["ButtonBackground"+suffix]=Brush(background);button.Resources["ButtonForeground"+suffix]=Brush(foreground);button.Resources["ButtonBorderBrush"+suffix]=Brushes.Transparent;}
    }
    internal static void ApplyWindow(Window window,PreviewControlPalette palette,params Border[] cards)
    {
        window.Background=Brush(palette.Glass);window.Foreground=Brush(palette.Text);
        foreach(var card in cards){card.Background=Brush(palette.Surface);card.BorderBrush=Brush(palette.Line);}
    }
}
