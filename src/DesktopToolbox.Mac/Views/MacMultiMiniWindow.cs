using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.ViewModels;

namespace DesktopToolbox.Mac.Views;

/// <summary>Candidate-only mini window using the already connected peer service.</summary>
internal sealed class MacMultiMiniWindow : Window
{
    private readonly MacCollaborationControl mini;
    private readonly MacWirelessViewModel wireless;
    private readonly Action showSettings;

    public MacMultiMiniWindow(MacCollaborationService collaboration, MacMultiMiniLink link, Action showSettings, Action<bool, bool> reportViewerVisibility)
    {
        this.showSettings = showSettings;
        wireless = new MacWirelessViewModel(new StandaloneWirelessUnavailable());
        Title = "年年互联·多设备测试";
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        mini = new MacCollaborationControl(this, collaboration, link, wireless, reportViewerVisibility);
        mini.OpenSettings += OpenSettings;
        mini.LayoutRequested += UpdateSize;
        Content = mini;
        UpdateSize();
        Opened += async (_, _) => { await wireless.RefreshAsync(); mini.ApplySavedPlacement(); mini.RefreshViewerVisibility(); };
        Closed += async (_, _) => { mini.Dispose(); await wireless.DisposeAsync(); };
    }

    private void OpenSettings() => showSettings();
    private void UpdateSize()
    {
        var size = mini.RequestedSize;
        MinWidth = size.Width; Width = size.Width;
        MinHeight = size.Height; Height = size.Height;
        if (IsVisible) mini.ApplySavedPlacement();
    }
}
