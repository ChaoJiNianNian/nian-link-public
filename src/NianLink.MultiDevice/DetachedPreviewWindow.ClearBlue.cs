using System.Windows.Media;
using Nian.Input.Control;

namespace DesktopToolbox.App.DeskLink;

// Compiled only by the multi-device projects; the single-device viewer keeps its existing theme.
internal sealed partial class DetachedPreviewWindow
{
    public void SetClearBlueAppearance()
    {
        appearanceDark = null;
        palette = new PreviewControlPalette("#F6F9FF", "#FFFFFF", "#EAF1FF", "#E4EBF4", "#1B2C43", "#65768B",
            "#356FEB", "#356FEB", "#FFFFFF", "#35527B");
        Background = Brush(palette.Glass); Foreground = Brush(palette.Text);
        toolbar.Background = Brush(palette.Surface); toolbar.BorderBrush = Brush(palette.Line); toolbar.Effect = null;
        frameBorder.Background = stage.Background = Brush(palette.Soft);
        deviceName.Foreground = Brush(palette.Text);
        modeLabel.Foreground = placeholder.Foreground = caption.Foreground = controlStatus.Foreground = Brush(palette.Muted);
        pairingButton.Foreground = shortcutsButton.Foreground = Brush(palette.Accent);
        pairingButton.Background = shortcutsButton.Background = Brushes.Transparent;
        pairingButton.BorderBrush = shortcutsButton.BorderBrush = Brushes.Transparent;
        actionButton.Background = Brush(palette.Button); actionButton.Foreground = Brush(palette.ButtonText); actionButton.BorderBrush = Brushes.Transparent;
        if (shortcutsButton.ContextMenu is { } menu) { menu.Background = Brush(palette.Surface); menu.Foreground = Brush(palette.Text); menu.BorderBrush = Brush(palette.Line); }
        RefreshControl();
    }
}
