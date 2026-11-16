using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Views;

public sealed partial class MacMultiDeviceWindow
{
    // Keep each existing control and event handler alive while changing only its visual page.
    private Control BuildBlueLayout()
    {
        var pages = new Dictionary<string, Control>();
        var device = new StackPanel { Spacing = 10 };
        device.Children.Add(Label("在此管理主控电脑连接。"));
        device.Children.Add(state);
        device.Children.Add(WrapActions(Action("开始连接", StartAsync, true), Action("停止连接", StopAsync), Action("归还主控", () => SwitchAsync(false))));
        var keyboardCard = new StackPanel { Spacing = 10 };
        keyboardCard.Children.Add(Label("首次连接需核对主控键鼠指纹。"));
        keyboardCard.Children.Add(Action("核对指纹", ConfirmKeyboardAsync));
        var permissionsCard = new StackPanel { Spacing = 10 };
        permissionsCard.Children.Add(Label("管理辅助功能和屏幕录制权限。"));
        var settingsLink = new Button { Content = "查看设置" };
        permissionsCard.Children.Add(settingsLink);
        var quickCards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var keyboardQuick = Card("键鼠共享", keyboardCard); keyboardQuick.Margin = new Thickness(0, 0, 6, 0);
        var permissionsQuick = Card("本机权限", permissionsCard); permissionsQuick.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(permissionsQuick, 1); quickCards.Children.Add(keyboardQuick); quickCards.Children.Add(permissionsQuick);
        pages.Add("设备", Page("本机与主控", "查看连接状态并管理本机操作。", Card("笔记本主控", device), quickCards));

        var localIdentity = new StackPanel { Spacing = 10 };
        localIdentity.Children.Add(Label("身份卡不包含控制密码；也可使用局域网免文件配对。"));
        localIdentity.Children.Add(identity);
        localIdentity.Children.Add(Action("生成身份卡", CreateIdentityAsync, true));
#if DISCOVERY_CANDIDATE
        localIdentity.Children.Add(DiscoveryPanel());
#endif
        var steps = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var text in new[] { "01  允许被发现", "02  核对完整指纹", "03  连接主控" })
            steps.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#EDF4FF")), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10), Margin = new Thickness(0, 0, 8, 8), Child = Label(text) });
        localIdentity.Children.Add(steps);
        var pairingPanel = new StackPanel { Spacing = 10 };
        pairingPanel.Children.Add(Label("配对前，请在两台设备上核对完整指纹。"));
        pairingPanel.Children.Add(pairing);
        pairingPanel.Children.Add(WrapActions(Action("导入配对文件", ImportAsync, true), Action("确认主控", ConfirmAsync)));
#if FILELESS_PAIRING_CANDIDATE
        pairingPanel.Children.Add(Action("查看未完成的配对", ReviewMacPairingRecoveryAsync));
#endif
        pages.Add("发现与配对", Page("发现与配对", "允许主控发现本机，并核对双方身份。", Card("本机发现", localIdentity), Card("安全配对", pairingPanel)));

        var screenPermissions = new StackPanel { Spacing = 10 };
        screenPermissions.Children.Add(Label("分别控制查看对方画面和共享本机画面。"));
        previewChoice.Click += async (_, _) => await SetScreenChoiceAsync(false, previewChoice.IsChecked == true);
        shareScreenChoice.Click += async (_, _) => await SetScreenChoiceAsync(true, shareScreenChoice.IsChecked == true);
        screenPermissions.Children.Add(previewChoice);
        screenPermissions.Children.Add(shareScreenChoice);
        screenPermissions.Children.Add(Label("共享 Mac 画面需要系统屏幕录制权限。"));
        screenPermissions.Children.Add(Label("每次重新启动本机多设备连接后，需要重新允许共享 Mac 画面。"));
        refreshChoice.SelectionChanged += async (_, _) =>
        {
            if (updatingRefreshChoice || !refreshChoice.IsEnabled || refreshChoice.SelectedIndex is < 0 or >= 5) return;
            await RunAsync(async () =>
            {
                var current = collaboration ?? throw new InvalidOperationException("请先连接主控。");
                await current.SetPreviewRefreshSecondsAsync(RefreshIntervals[refreshChoice.SelectedIndex]);
                feedback.Text = "监看刷新已保存；操作时自动使用实时画面。";
            });
        };
        screenPermissions.Children.Add(refreshChoice);
        var screenPreview = new StackPanel { Spacing = 10 };
        var previewSurface = new Grid(); previewSurface.Children.Add(previewImage); previewSurface.Children.Add(previewPlaceholder);
        screenPreview.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#DDE9F8")), CornerRadius = new CornerRadius(12), Child = previewSurface });
        screenPreview.Children.Add(WrapActions(ScreenAction("刷新画面", RefreshPreviewAsync), ScreenAction("开始操作", StartPreviewControlAsync, true), ScreenAction("放大预览", OpenPreviewAsync)));
        var screenColumns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var screenCard = Card("对方画面预览", screenPreview); screenCard.Margin = new Thickness(0, 0, 6, 0);
        var permissionCard = Card("画面权限", screenPermissions); permissionCard.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(permissionCard, 1); screenColumns.Children.Add(screenCard); screenColumns.Children.Add(permissionCard);
        pages.Add("画面", Page("设备画面", "分别控制你能看到什么，以及对方能看到什么。", screenColumns));

        var keyboard = new StackPanel { Spacing = 10 };
        keyboard.Children.Add(Label("首次连接需要单独核对主控键鼠指纹。"));
        keyboard.Children.Add(WrapActions(Action("核对键鼠指纹", ConfirmKeyboardAsync), Action("切到 Mac", () => SwitchAsync(true)), Action("归还主控", () => SwitchAsync(false))));
        keyboard.Children.Add(Action("打开互联小窗", OpenMiniAsync));
        clipboardChoice.Click += async (_, _) => await RunAsync(async () =>
        {
            var current = collaboration ?? throw new InvalidOperationException("请先开始连接。");
            await current.SetClipboardEnabledAsync(clipboardChoice.IsChecked == true);
        });
        keyboard.Children.Add(clipboardChoice);
        pages.Add("键鼠", Page("键鼠共享", "用主控的键盘和鼠标操作这台 Mac。", Card("键鼠控制", keyboard)));

        var sending = new StackPanel { Spacing = 10 };
        sending.Children.Add(Label("文件只在已配对的主控和这台 Mac 之间传送。"));
        sending.Children.Add(WrapActions(Action("发送文件", ChooseFilesAsync, true), Action("暂停传输", () => { collaboration?.PauseSending(); sharedDownloadCancellation?.Cancel(); return Task.CompletedTask; }), Action("继续发送", RetrySendAsync)));
        sending.Children.Add(fileStatus);
        var receiving = new StackPanel { Spacing = 10 };
        receiving.Children.Add(Label("选择本机接收文件的文件夹。"));
        receiving.Children.Add(Action("选择收件文件夹", ChooseReceiveAsync));
        var shared = new StackPanel { Spacing = 10 };
        shared.Children.Add(Label("在线查看主控授权共享的文件夹。"));
        shared.Children.Add(WrapActions(Action("查看共享文件夹", ShowSharedAsync), Action("返回上一级", SharedBackAsync), Action("保存选中项目", DownloadSharedAsync)));
        sharedFiles.DoubleTapped += async (_, _) => await RunAsync(OpenSharedAsync);
        shared.Children.Add(sharedFiles);
        var sharedStatus = Label(""); sharedStatus.Bind(TextBlock.TextProperty, new Binding("Text") { Source = fileStatus }); shared.Children.Add(sharedStatus);
        pages.Add("文件", Page("文件与文字", "发送文件或访问主控共享的文件夹。",
            Card("发送文件", sending), Card("接收文件", receiving), Card("主控共享文件夹", shared)));

        var permissions = new StackPanel { Spacing = 10 };
        permissions.Children.Add(Label("请为当前测试版开启辅助功能和屏幕录制权限。"));
        permissions.Children.Add(WrapActions(Action("打开辅助功能", () => { MacPermissions.OpenAccessibility(); return Task.CompletedTask; }),
            Action("打开屏幕录制", () => { MacPermissions.OpenScreenRecording(); return Task.CompletedTask; })));
        var network = new StackPanel { Spacing = 10 };
        network.Children.Add(Label("在发现与配对页面选择本机正在使用的局域网。"));
        var networkLink = new Button { Content = "查看网络" };
        network.Children.Add(networkLink);
        pages.Add("设置与权限", Page("设置与权限", "管理本机网络和连接所需的系统权限。", Card("局域网", network), Card("系统权限", permissions)));

        var pageContent = new ContentControl { Content = pages["设备"] };
        var content = new ScrollViewer { Name = "MacCandidatePageScroll", Content = pageContent, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var navigation = new StackPanel { Spacing = 4, Margin = new Thickness(12, 20) };
        navigation.Children.Add(Label("设备与连接"));
        var buttons = new Dictionary<string, Button>();
        var icons = new Dictionary<string, string>
        {
            ["设备"] = "M 2,4 L 12,4 12,12 2,12 Z M 5,15 L 9,15 M 7,12 L 7,15 M 14,7 L 17,7 17,15 14,15 Z",
            ["发现与配对"] = "M 7,6 L 5,6 A 3,3 0 0 0 5,12 L 7,12 M 11,6 L 13,6 A 3,3 0 0 1 13,12 L 11,12 M 6,9 L 12,9",
            ["画面"] = "M 2,5 L 16,5 16,14 2,14 Z M 8,7 L 12,9.5 8,12 Z",
            ["键鼠"] = "M 9,2 A 5,5 0 0 0 4,7 L 4,11 A 5,5 0 0 0 14,11 L 14,7 A 5,5 0 0 0 9,2 Z M 9,2 L 9,7",
            ["文件"] = "M 2,5 L 7,5 9,7 16,7 16,14 2,14 Z",
            ["设置与权限"] = "M 3,5 L 15,5 15,13 3,13 Z M 7,16 L 11,16 M 9,13 L 9,16"
        };
        foreach (var (name, page) in pages)
        {
            var icon = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(icons[name]), Width = 16, Height = 16,
                Stretch = Stretch.Uniform, Stroke = new SolidColorBrush(Color.Parse("#526D8E")), StrokeThickness = 1.4 };
            var caption = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            var contents = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            contents.Children.Add(icon); contents.Children.Add(caption);
            var button = new Button { Content = contents, Tag = name, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 10),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(9),
                Foreground = new SolidColorBrush(Color.Parse("#526D8E")), Background = Brushes.Transparent };
            AutomationProperties.SetName(button, name);
            buttons.Add(name, button);
            button.Click += (_, _) =>
            {
                pageContent.Content = page;
                content.Offset = new Vector(0, 0);
                selectedPage = name;
                UpdateViewerVisibility();
                foreach (var (key, item) in buttons)
                {
                    item.Background = new SolidColorBrush(Color.Parse(key == name ? "#FFFFFF" : "#EDF4FF"));
                    item.Foreground = new SolidColorBrush(Color.Parse(key == name ? "#356FEB" : "#526D8E"));
                }
            };
            navigation.Children.Add(button);
        }
        settingsLink.Click += (_, _) => buttons["设置与权限"].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        networkLink.Click += (_, _) => buttons["发现与配对"].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        buttons["设备"].Background = Brushes.White;
        buttons["设备"].Foreground = new SolidColorBrush(Color.Parse("#356FEB"));
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(22, 12), VerticalAlignment = VerticalAlignment.Center };
        using (var stream = AssetLoader.Open(new Uri("avares://NianDeskLink/Assets/NianLinkBrand.png")))
            brand.Children.Add(new Image { Width = 36, Height = 36, Source = new Bitmap(stream), Stretch = Stretch.Uniform });
        var brandText = new StackPanel { Spacing = 2 }; brandText.Children.Add(TitleText("年年互联")); brandText.Children.Add(Label("多设备测试 · 这台 Mac")); brand.Children.Add(brandText);
        var layout = new Grid { RowDefinitions = new RowDefinitions("72,*,Auto"), ColumnDefinitions = new ColumnDefinitions("174,*") };
        var header = new Border { Background = Brushes.White, Child = brand }; Grid.SetColumnSpan(header, 2); layout.Children.Add(header);
        var sidebar = new Border { Background = new SolidColorBrush(Color.Parse("#EDF4FF")), Child = navigation };
        Grid.SetRow(sidebar, 1); layout.Children.Add(sidebar);
        Grid.SetRow(content, 1); Grid.SetColumn(content, 1); layout.Children.Add(content);
        feedback.Foreground = new SolidColorBrush(Color.Parse("#55779D")); feedback.Margin = new Thickness(22, 8);
        Grid.SetRow(feedback, 2); Grid.SetColumnSpan(feedback, 2); layout.Children.Add(feedback);
        return layout;
    }

    private static WrapPanel WrapActions(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls) { control.Margin = new Thickness(0, 0, 8, 8); panel.Children.Add(control); }
        return panel;
    }

    private async Task SetScreenChoiceAsync(bool sharing, bool enabled)
    {
        var current = collaboration;
        if (current is null || closing)
        {
            previewChoice.IsChecked = false; shareScreenChoice.IsChecked = false;
            if (!closing) feedback.Text = "请先开始连接。";
            return;
        }
        // Do not disable the window while an OS permission prompt is pending:
        // the user must be able to turn sharing off immediately.
        var version = sharing ? ++shareChoiceVersion : ++viewChoiceVersion;
        bool Current() => !closing && ReferenceEquals(current, collaboration) &&
            version == (sharing ? shareChoiceVersion : viewChoiceVersion);
        try
        {
            if (sharing) await current.SetShareScreenEnabledAsync(enabled);
            else await current.SetViewPreviewEnabledAsync(enabled);
        }
        catch (OperationCanceledException) when (!Current()) { }
        catch (Exception error) { if (Current()) feedback.Text = "操作未完成：" + error.Message; }
        finally
        {
            if (Current())
            {
                previewChoice.IsChecked = current.Snapshot.Settings.PreviewEnabled;
                shareScreenChoice.IsChecked = current.Snapshot.Settings.ShareScreenEnabled;
            }
        }
    }

    private async Task RefreshPreviewAsync()
    {
        var current = collaboration ?? throw new InvalidOperationException("请先开始连接。");
        if (!current.Snapshot.Settings.PreviewEnabled) throw new InvalidOperationException("请先开启查看对方画面。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await current.RefreshOnceAsync(timeout.Token);
        feedback.Text = "已刷新画面状态；画面更新取决于对方共享设置。";
    }

    private Button ScreenAction(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = text }; button.Classes.Add(primary ? "accent" : "secondary");
        button.Click += async (_, _) =>
        {
            if (closing || !button.IsEnabled) return;
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception error) { if (!closing) feedback.Text = "操作未完成：" + error.Message; }
            finally { if (!closing) button.IsEnabled = true; }
        };
        return button;
    }

    private async Task StartPreviewControlAsync()
    {
        await OpenPreviewAsync();
        var window = detachedPreview ?? throw new InvalidOperationException("预览窗口未打开。");
        await window.StartControlFromUserAsync();
    }

    private static Control Page(string title, string subtitle, params Control[] sections)
    {
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(30, 27) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1B2C43")), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Label(subtitle));
        foreach (var section in sections) body.Children.Add(section);
        return body;
    }
}
