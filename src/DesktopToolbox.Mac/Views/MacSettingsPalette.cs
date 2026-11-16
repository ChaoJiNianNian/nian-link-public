using Avalonia.Controls;
using Avalonia.Media;

namespace DesktopToolbox.Mac.Views;

/// <summary>Settings-only palette. The embedded collaboration window has its own appearance.</summary>
internal static class MacSettingsPalette
{
    private static readonly (string Key, string Color)[] Colors =
    [
        ("DeskForeground", "#243A59"), ("DeskMuted", "#7186A3"), ("DeskAccent", "#397DF1"),
        ("DeskSurface", "#FBFDFF"), ("DeskBorder", "#D7E3F2"),
        ("ButtonBackground", "#EDF3FB"), ("ButtonBackgroundPointerOver", "#E1EDFC"),
        ("ButtonBackgroundPressed", "#D4E5FA"), ("ButtonBackgroundDisabled", "#EFF3F8"),
        ("ButtonForeground", "#3D587B"), ("ButtonForegroundPointerOver", "#243A59"),
        ("ButtonForegroundPressed", "#243A59"), ("ButtonForegroundDisabled", "#A3AFC0"),
        ("ButtonBorderBrush", "#D7E3F2"), ("ButtonBorderBrushPointerOver", "#ADC9EF"),
        ("ButtonBorderBrushPressed", "#83ABEA"), ("ButtonBorderBrushDisabled", "#E3EAF3"),
        ("AccentButtonBackground", "#D9E8FF"), ("AccentButtonBackgroundPointerOver", "#C8DFFE"),
        ("AccentButtonBackgroundPressed", "#BBD5F9"), ("AccentButtonBackgroundDisabled", "#EFF3F8"),
        ("AccentButtonForeground", "#2866CF"), ("AccentButtonForegroundPointerOver", "#1F59BD"),
        ("AccentButtonForegroundPressed", "#1F59BD"), ("AccentButtonForegroundDisabled", "#A3AFC0"),
        ("AccentButtonBorderBrush", "#C4DAF9"), ("AccentButtonBorderBrushPointerOver", "#AACAF4"),
        ("AccentButtonBorderBrushPressed", "#8CB8EC"), ("AccentButtonBorderBrushDisabled", "#E3EAF3"),
        ("TextControlBackground", "#F5F8FD"), ("TextControlBackgroundPointerOver", "#F9FBFF"),
        ("TextControlBackgroundFocused", "#FFFFFF"), ("TextControlBackgroundDisabled", "#EFF3F8"),
        ("TextControlForeground", "#243A59"), ("TextControlForegroundPointerOver", "#243A59"),
        ("TextControlForegroundFocused", "#243A59"), ("TextControlForegroundDisabled", "#8B9AB0"),
        ("TextControlBorderBrush", "#D4E0EE"), ("TextControlBorderBrushPointerOver", "#AFC9E8"),
        ("TextControlBorderBrushFocused", "#6E9EEB"), ("TextControlBorderBrushDisabled", "#E3EAF3"),
        ("TextControlPlaceholderForeground", "#879BB4"), ("TextControlSelectionHighlightColor", "#C9DFFF"),
        ("ExpanderHeaderBackground", "#F5F8FD"), ("ExpanderHeaderBackgroundPointerOver", "#ECF3FD"),
        ("ExpanderHeaderBackgroundPressed", "#E2EDFC"), ("ExpanderHeaderBackgroundDisabled", "#EFF3F8"),
        ("ExpanderHeaderBorderBrush", "#D7E3F2"), ("ExpanderHeaderBorderBrushPointerOver", "#B9D2F1"),
        ("ExpanderHeaderBorderBrushPressed", "#9BBDED"), ("ExpanderHeaderBorderBrushDisabled", "#E3EAF3"),
        ("ExpanderHeaderForeground", "#3D587B"), ("ExpanderHeaderForegroundPointerOver", "#243A59"),
        ("ExpanderHeaderForegroundPressed", "#243A59"), ("ExpanderHeaderForegroundDisabled", "#A3AFC0"),
        ("ExpanderContentBackground", "#F9FBFF"), ("ExpanderContentBorderBrush", "#D7E3F2"),
        ("ExpanderChevronBackground", "#00000000"), ("ExpanderChevronBackgroundPointerOver", "#E1EDFC"),
        ("ExpanderChevronBackgroundPressed", "#D4E5FA"), ("ExpanderChevronBackgroundDisabled", "#00000000"),
        ("ExpanderChevronForeground", "#7186A3"), ("ExpanderChevronForegroundPointerOver", "#3D587B"),
        ("ExpanderChevronForegroundPressed", "#2866CF"), ("ExpanderChevronForegroundDisabled", "#A3AFC0")
    ];

    internal static void Apply(Control root, bool light)
    {
        foreach (var (key, color) in Colors)
        {
            if (light) root.Resources[key] = new SolidColorBrush(Color.Parse(color));
            else root.Resources.Remove(key);
        }
    }
}
