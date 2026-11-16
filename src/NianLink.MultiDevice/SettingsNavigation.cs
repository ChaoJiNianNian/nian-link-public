using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    private static readonly string[] HostPages = ["HostConnectionCard", "HostPairingCard", "HostDevicesCard", "HostKeyboardCard", "HostDisplayCard", "HostPreviewCard", "HostFilesCard"];
    private static readonly string[] PeerPages = ["PeerSummaryCard", "PeerConnectionCard", "PeerPairingCard", "PeerDevicesCard", "PeerPreviewCard", "PeerFilesCard", "PeerSettingsCard"];

    private void InitializeSettingsPages()
    {
        ShowSettingsPage("HostDevicesCard");
        ShowSettingsPage("PeerSummaryCard");
    }

    private void SelectWorkspaceRole_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string role } || !ModeTabs.IsEnabled) return;
        if (role == "0") ModeTabs.SelectedIndex = 0;
        else if (role == "1") ModeTabs.SelectedIndex = 1;
    }

    private async void NavigateSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        try { await StopDiscoveryUiAsync(); ShowSettingsPage(name); }
        catch (System.Exception error) { ShowFailure(error, "停止局域网发现"); }
    }

    private void ShowSettingsPage(string name)
    {
        bool isHost = System.Array.IndexOf(HostPages, name) >= 0;
        var pages = isHost ? HostPages : PeerPages;
        if (System.Array.IndexOf(pages, name) < 0) return;
        foreach (var page in pages)
            if (FindName(page) is FrameworkElement element)
                element.Visibility = page == name ? Visibility.Visible : Visibility.Collapsed;
        if (isHost) HostIntroCard.Visibility = name == "HostConnectionCard" ? Visibility.Visible : Visibility.Collapsed;
        var buttons = isHost ? HostSections : PeerSections;
        foreach (var child in buttons.Children)
        {
            if (child is not Button button) continue;
            bool selected = Equals(button.Tag, name);
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected ? "#FFFFFF" : "#00FFFFFF"));
            button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFFFFF"));
            button.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected ? "#356FEB" : "#65768B"));
        }
        (isHost ? HostScroll : PeerScroll).ScrollToTop();
    }
}
