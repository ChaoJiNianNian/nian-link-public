using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.ViewModels;
using DesktopToolbox.Mac.Models;
using Nian.Input.Mac;

namespace DesktopToolbox.Mac.Views;

public sealed class MainWindow : Window
{
#if NATIVE_INPUT_CANDIDATE
    private const bool NativeCandidate = true;
#else
    private const bool NativeCandidate = false;
#endif
    private readonly IMacLinkService service;
    private readonly MacCollaborationService? collaboration;
    private readonly MacCollaborationControl? collaborationControl;
    private readonly NativeSurfaceSnapshot? nativeSurface;
    private bool explicitQuit;
    private readonly MacLinkViewModel model;
    private readonly MacWirelessViewModel wirelessModel;
    private readonly MacWirelessControl wirelessControl;
    private readonly DispatcherTimer wirelessTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly TextBlock connection = Text("", 17);
    private readonly TextBlock control = Text("");
    private readonly TextBlock active = Text("");
    private readonly TextBlock status = Text("");
    private readonly TextBlock displayRecovery = Text("", 13);
    private readonly TextBlock error = Text("");
    private readonly SelectableTextBlock localFingerprint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly SelectableTextBlock peerFingerprint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock pairingDetails = Text("", 13);
    private readonly TextBlock pairingPlaceholderText = Text("", 13);
    private readonly TextBlock pairingFeedback = Text("", 12);
    private readonly TextBlock importFeedback = Text("", 12);
    private string? revealedPairingId;
    private string revealedPairingError = "";
    private readonly Button start;
    private readonly Button stop;
    private readonly Button windows;
    private readonly Button mac;
    private readonly Button acceptPeer;
    private readonly Button rejectPeer;
    private readonly Button acceptPairing;
    private readonly Button rejectPairing;
    private readonly Border peerCard;
    private readonly Border pairingCard;
    private readonly Border pairingPlaceholder;
    private readonly StackPanel settingsFields = new() { Spacing = 10 };
    private bool polling;
    private bool closing;
    private bool allowClose;
    private MacRemotePreviewReturnWindow? remoteReturnWindow;

    private readonly Grid frame = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), RowSpacing = 14 };
    private readonly Grid header = new();
    private readonly StackPanel title = new() { Spacing = 6 };
    private readonly StackPanel brandHeader = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly TextBlock compactTarget = Text("", 14);
    private readonly PathIcon settingsIcon = new()
    {
        Width = 16, Height = 16,
        Data = Geometry.Parse("F0 M10,2 L14,2 L15,5 L17,6 L20,5 L22,8 L20,11 L20,13 L22,16 L20,19 L17,18 L15,19 L14,22 L10,22 L9,19 L7,18 L4,19 L2,16 L4,13 L4,11 L2,8 L4,5 L7,6 L9,5 Z M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8 Z")
    };
    private readonly TextBlock heading = Text("年年互联", 18, FontWeight.SemiBold);
    private readonly TextBlock tagline = Text("连接与设备设置", 12);
    private readonly TextBlock lockedNote = Text("共享运行时连接资料会锁定；需要修改时，请先停止共享。", 12);
    private readonly ScrollViewer details;
    private readonly Button settingsToggle;
    private bool settingsRequested;
    private bool? compactMode;
    private bool compactSizeCheckQueued;
    private Size compactSize = new(380, 208);
    public MainWindow(IMacLinkService service, IMacWirelessModeService wireless, MacCollaborationService? collaboration = null)
        :this(service,wireless,collaboration,null){}
    public MainWindow(IMacLinkService service,IMacWirelessModeService wireless,MacCollaborationService? collaboration,NativeSurfaceSnapshot? nativeSurface,Func<Window,Task>? showPreviewPairing=null)
    {
        this.service = service; this.collaboration = collaboration;this.nativeSurface=nativeSurface;
        model = new(service, action => Dispatcher.UIThread.Post(action));
        wirelessModel = new(wireless, action => Dispatcher.UIThread.Post(action));
        DataContext = model;
        Title = MacWindowPresentation.WindowTitle(NativeCandidate); Width = 640; Height = 680; MinWidth = 520; MinHeight = 520;
#if PREVIEW_CONTROL_CANDIDATE
        Title="年年互联·预览操作测试";
#endif
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        tagline.Classes.Add("muted"); lockedNote.Classes.Add("muted"); active.Classes.Add("accent-text");
        active.TextWrapping = TextWrapping.NoWrap; active.TextTrimming = TextTrimming.CharacterEllipsis;
        status.Classes.Add("muted"); displayRecovery.Classes.Add("muted");
        compactTarget.Name = "ControlTargetStatus";
        compactTarget.TextWrapping = TextWrapping.NoWrap;
        compactTarget.TextTrimming = TextTrimming.CharacterEllipsis;
        compactTarget.TextAlignment = TextAlignment.Center;
        compactTarget.HorizontalAlignment = HorizontalAlignment.Stretch;
        compactTarget.VerticalAlignment = VerticalAlignment.Center;
        title.Children.Add(heading); title.Children.Add(tagline); title.Children.Add(active);
        using var brandStream = AssetLoader.Open(new Uri("avares://NianDeskLink/Assets/NianLinkBrand.png"));
        var brand = new Image { Width = 42, Height = 42, Source = new Bitmap(brandStream),
            Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        brandHeader.Children.Add(brand); brandHeader.Children.Add(title);
        settingsToggle = ActionButton("设置", () => { nativeSurface?.Protect();settingsRequested = !settingsRequested; Render(); return Task.CompletedTask; }, "quiet");
        settingsToggle.Name = "SettingsToggle"; settingsToggle.FontSize = 12;
        settingsToggle.MinHeight = 30; settingsToggle.Padding = new Thickness(10, 5);
        settingsToggle.VerticalAlignment = VerticalAlignment.Top;
        header.ColumnDefinitions = new ColumnDefinitions("*,Auto"); header.ColumnSpacing = 12;
        header.Children.Add(brandHeader); Grid.SetColumn(compactTarget, 1); header.Children.Add(compactTarget);
        Grid.SetColumn(settingsToggle, 1); header.Children.Add(settingsToggle);
        frame.Children.Add(header);

        windows = ActionButton("切到 Windows", () => model.SwitchAsync(model.WindowsName));
        mac = ActionButton("切到 Mac", () => model.SwitchAsync(model.MacName));
        windows.Name = "SwitchToWindows"; mac.Name = "SwitchToMac";
        windows.HorizontalAlignment = mac.HorizontalAlignment = HorizontalAlignment.Stretch;
        windows.HorizontalContentAlignment = mac.HorizontalContentAlignment = HorizontalAlignment.Center;
        windows.VerticalContentAlignment = mac.VerticalContentAlignment = VerticalAlignment.Center;
        var switches = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        switches.Children.Add(windows); Grid.SetColumn(mac, 1); switches.Children.Add(mac);
        Grid.SetRow(switches, 1); frame.Children.Add(switches);

        wirelessControl = new MacWirelessControl(wirelessModel);
        Grid.SetRow(wirelessControl, 2); frame.Children.Add(wirelessControl);
        var body = new StackPanel { Spacing = 16 };
        var live = new StackPanel { Spacing = 10 };
        live.Children.Add(connection); live.Children.Add(control); live.Children.Add(status); live.Children.Add(displayRecovery);
        error.Foreground = new SolidColorBrush(Color.Parse("#B53C46")); live.Children.Add(error);
        start = ActionButton("开始共享", () => model.StartAsync(), "accent");
        stop = ActionButton("停止共享", () => model.StopAsync());
        live.Children.Add(Horizontal(start, stop, ActionButton("刷新状态", () => model.RefreshAsync(), "quiet"),
            ActionButton("退出软件", () => { RequestExit(); return Task.CompletedTask; }, "quiet")));
        settingsFields.Children.Add(TwoColumns(Field("Windows IP / 主机名", nameof(model.WindowsHost)), Field("Windows 设备名称", nameof(model.WindowsName))));
        settingsFields.Children.Add(Field("这台 Mac 的设备名称", nameof(model.MacName)));
        if(nativeSurface is null)
            settingsFields.Children.Add(new Expander { Header = "高级：键鼠核心位置", Content = Field("Deskflow 核心文件", nameof(model.EnginePath)) });
        else settingsFields.Children.Add(Text("自研输入核心随应用提供，无需选择外部核心文件。",12));
        var settingsPanel = new StackPanel { Spacing = 12 };
        settingsPanel.Children.Add(Text("导入 Windows 发来的文件，核对指纹后再开始共享。", 13));
        settingsPanel.Children.Add(ActionButton("导入 Windows 配对文件…", ImportPairingAsync, "accent"));
        settingsPanel.Children.Add(importFeedback);
        settingsPanel.Children.Add(lockedNote);
        settingsPanel.Children.Add(new Expander { Header = "设备名称与连接（可选）", Content = settingsFields });
        body.Children.Add(Card("1   导入配对文件", settingsPanel));
        var pairingPanel = new StackPanel { Spacing = 12 }; pairingPanel.Children.Add(pairingDetails);
        acceptPairing = ActionButton("指纹一致，确认配对", () => model.AcceptPairingAsync());
        rejectPairing = ActionButton("拒绝此次配对", () => { model.RejectPairing(); return Task.CompletedTask; });
        pairingPanel.Children.Add(pairingFeedback);
        pairingPanel.Children.Add(Horizontal(acceptPairing, rejectPairing));
        pairingPlaceholderText.Classes.Add("muted");
        pairingPlaceholder = Card("2   核对 Windows 指纹", pairingPlaceholderText); body.Children.Add(pairingPlaceholder);
        pairingCard = Card(nativeSurface is null?"2   核对 Windows 指纹":"2   核对 Windows 自研键鼠设备指纹", pairingPanel); body.Children.Add(pairingCard);
        body.Children.Add(Card("3   开启共享", live));
        var peerPanel = new StackPanel { Spacing = 12 };
        peerPanel.Children.Add(Text(nativeSurface is null?"有新的键鼠服务器请求连接。请与 Windows 工作台显示的指纹核对，一致后才允许。":"有新的自研键鼠设备请求连接。请与 Windows 工作台核对设备指纹，一致后才允许。", 13));
        peerPanel.Children.Add(peerFingerprint);
        acceptPeer = ActionButton("一致，允许连接", () => model.ConfirmPeerAsync(true));
        rejectPeer = ActionButton("拒绝连接", () => model.ConfirmPeerAsync(false));
        peerPanel.Children.Add(Horizontal(acceptPeer, rejectPeer));
        peerCard = Card("待核对的键鼠连接", peerPanel); body.Children.Add(peerCard);
        var identity = new StackPanel { Spacing = 10 };
        identity.Children.Add(Text("本机证书 SHA-256 · 可选择并复制", 12)); identity.Children.Add(localFingerprint);
        body.Children.Add(Card("这台 Mac 的安全指纹", identity));
        var permissions = new StackPanel { Spacing = 12 };
        permissions.Children.Add(Text($"请在 Mac 系统设置允许“{Title}”使用辅助功能，并按提示授权输入监控与本地网络。升级后旧授权若失效，需要重新添加当前应用。", 13));
        permissions.Children.Add(Horizontal(ActionButton("打开辅助功能设置", () => { MacPermissions.OpenAccessibility(); return Task.CompletedTask; }), ActionButton("打开输入监控设置", () => { MacPermissions.OpenInputMonitoring(); return Task.CompletedTask; })));
        permissions.Children.Add(ActionButton("打开屏幕录制设置", () => { MacPermissions.OpenScreenRecording(); return Task.CompletedTask; }));
        body.Children.Add(Card("Mac 系统权限", permissions));
        body.Children.Add(Text(collaboration is null
            ? nativeSurface is null?"基于开源 Deskflow · 关闭本窗口会停止此 Mac 客户端。Windows 需保持开机联网。":"自研键鼠共享 · 关闭本窗口会停止此 Mac 客户端。Windows 需保持开机联网。"
            : "关闭窗口会隐藏到后台；按 ⌘Q 或点“退出软件”可完全退出。Windows 需保持开机联网。", 12));
        details = new ScrollViewer { Name = "DetailsPane", Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(details, 3); frame.Children.Add(details);
        if (collaboration is not null)
        {
            collaborationControl = new MacCollaborationControl(this, collaboration, model, wirelessModel);
            collaborationControl.PreviewPairingRequested+=async actualOwner=>
            {
                if(closing)return;
                if(showPreviewPairing is null){await ShowErrorAsync("当前版本仅支持观看，预览操作尚未启用。");return;}
                try{await showPreviewPairing(actualOwner??this);}
                catch{if(!closing)await ShowErrorAsync("预览连接页暂未打开，请稍后重试。");}
            };
            collaborationControl.OpenSettings += () => { nativeSurface?.Protect();settingsRequested = true; Render(); };
            collaborationControl.LayoutRequested += UpdateCollaborationSize;
            collaboration.Changed += OnCollaborationChanged;
            var root = new Grid(); root.Children.Add(frame); root.Children.Add(collaborationControl); Content = root;
            var show = new NativeMenuItem("显示协同小窗"); show.Click += (_, _) => { Show(); Activate(); };
            var quit = new NativeMenuItem("退出年年协同"); quit.Click += (_, _) => RequestExit();
            var menu = new NativeMenu(); menu.Items.Add(show); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(quit); NativeMenu.SetMenu(this, menu);
            Closed += (_, _) => { if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(); };
        }
        else Content = frame;
        model.PropertyChanged += (_, _) => Render();
        refreshTimer.Tick += async (_, _) =>
        {
            if (polling || closing) return;
            polling = true;
            try { await service.RefreshAsync(); } catch { /* Explicit refresh surfaces errors; background refresh must not crash the desktop. */ }
            finally { polling = false; }
        };
        wirelessTimer.Tick += async (_, _) => { if (!closing) await wirelessModel.RefreshAsync(); };
        Opened += async (_, _) =>
        {
            await Task.WhenAll(model.InitializeAsync(), wirelessModel.RefreshAsync(), collaboration?.StartAsync() ?? Task.CompletedTask);
            if (!closing) { refreshTimer.Start(); wirelessTimer.Start();UpdateNativeSurface(); }
        };
        Activated += (_, _) => { collaborationControl?.RefreshViewerVisibility(); QueueCompactSizeCheck();UpdateNativeSurface(); };
        Resized += (_, args) =>
        {
            if (!closing && compactMode == true && WindowState == WindowState.Normal && args.Reason == WindowResizeReason.User)
                compactSize = args.ClientSize;
            else QueueCompactSizeCheck();
            UpdateNativeSurface();
        };
        PropertyChanged += (_, change) =>
        {
            if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty) collaborationControl?.RefreshViewerVisibility();
            if (change.Property == WindowStateProperty && WindowState == WindowState.Normal) QueueCompactSizeCheck();
            if(change.Property==WindowStateProperty||change.Property==IsVisibleProperty||change.Property==IsEnabledProperty)UpdateNativeSurface();
        };
        Closing += async (_, args) =>
        {
            if (allowClose) return;
            nativeSurface?.Protect();
            args.Cancel = true;
            var applicationExit = args.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;
            if (collaboration is not null && !explicitQuit && !applicationExit) { Hide(); collaborationControl?.RefreshViewerVisibility(); return; }
            if (closing) return;
            closing = true; IsEnabled = false; refreshTimer.Stop(); wirelessTimer.Stop();
            CloseRemoteReturnWindow();
            try
            {
                if (collaboration is not null) { collaboration.Changed -= OnCollaborationChanged; await collaboration.DisposeAsync(); }
                collaborationControl?.Dispose();
                await wirelessModel.CancelPendingAsync();
                await service.StopAsync(); await service.DisposeAsync(); model.Dispose(); await wirelessModel.DisposeAsync(); allowClose = true; Close();
            }
            catch (Exception failure) { closing = false; IsEnabled = true; refreshTimer.Start(); wirelessTimer.Start(); await ShowErrorAsync(failure.Message); }
        };
        Render();
    }

    private void Render()
    {
        UpdateRemoteReturnWindow();
        var canUseCompact=collaboration is null?model.CanUseCompactView:model.CanUseCollaborationView;
        if(!canUseCompact||settingsRequested||model.HasPairingPreview||model.PendingPeerFingerprint.Length>0||closing)nativeSurface?.Protect();
        connection.Text = model.ConnectionLabel; control.Text = model.ControlLabel; active.Text = compactTarget.Text = model.ActiveComputerLabel;
        status.Text = model.StatusMessage; displayRecovery.Text = model.DisplayRecoveryMessage; error.Text = model.ErrorMessage; error.IsVisible = model.ErrorMessage.Length > 0;
        localFingerprint.Text = model.LocalFingerprint; peerFingerprint.Text = model.PendingPeerFingerprint;
        peerCard.IsVisible = model.PendingPeerFingerprint.Length > 0; pairingCard.IsVisible = model.HasPairingPreview;
        pairingPlaceholder.IsVisible = !model.HasPairingPreview;
        pairingPlaceholderText.Text = model.HasApprovedPairing ? "已确认设备；更换配对时请重新核对指纹。" : "导入配对文件后，在这里核对并确认 Windows 指纹。";
        pairingDetails.Text = model.PairingDescription;
        var pairingMessage = model.PairingErrorMessage.Length > 0 ? model.PairingStatusMessage + "\n" + model.PairingErrorMessage : model.PairingStatusMessage;
        foreach (var feedback in new[] { pairingFeedback, importFeedback })
        {
            feedback.Text = pairingMessage; feedback.IsVisible = pairingMessage.Length > 0;
            feedback.Foreground = new SolidColorBrush(Color.Parse(model.PairingErrorMessage.Length > 0 ? "#B53C46" : "#43658E"));
        }
        start.IsEnabled = model.CanStart; stop.IsEnabled = model.CanStop;
        windows.IsEnabled = model.CanReturnToWindows; mac.IsEnabled = model.CanSwitch; settingsFields.IsEnabled = model.CanEditConnection;
        acceptPeer.IsEnabled = rejectPeer.IsEnabled = model.CanConfirmPeer;
        acceptPairing.IsEnabled = rejectPairing.IsEnabled = model.HasPairingPreview && !model.IsBusy && model.CanEditConnection;
        acceptPairing.Content = model.IsBusy ? "正在配对…" : "指纹一致，确认配对";
        lockedNote.IsVisible = !model.CanEditConnection;
        SetSelected(windows, model.IsWindowsActive); SetSelected(mac, model.IsMacActive);
        ApplyPresentation();
        var previewId = model.PendingPairing?.Id;
        if (previewId != revealedPairingId)
        {
            revealedPairingId = previewId;
            if (previewId is not null) Dispatcher.UIThread.Post(() =>
            {
                if (!closing && model.PendingPairing?.Id == previewId && pairingCard.IsVisible)
                    pairingCard.BringIntoView();
            }, DispatcherPriority.Loaded);
        }
        if (model.PairingErrorMessage != revealedPairingError)
        {
            revealedPairingError = model.PairingErrorMessage;
            var failure = revealedPairingError;
            if (failure.Length > 0) Dispatcher.UIThread.Post(() =>
            {
                if (!closing && model.PairingErrorMessage == failure)
                    (model.HasPairingPreview ? pairingFeedback : importFeedback).BringIntoView();
            }, DispatcherPriority.Loaded);
        }
    }
    private void UpdateRemoteReturnWindow()
    {
        var id=closing?null:model.RemotePreviewReturnId;
        if(remoteReturnWindow?.SessionId==id)return;
        CloseRemoteReturnWindow();
        if(id is null)return;
        remoteReturnWindow=new MacRemotePreviewReturnWindow(id,model.ReturnRemotePreviewAsync);
        // RealtimeCapture captures CGMainDisplayID; place the return control on
        // Avalonia's primary screen even when the toolbox window is elsewhere.
        var screen=Screens.Primary??Screens.ScreenFromWindow(this);
        if(screen is not null)
        {
            var area=screen.WorkingArea;
            remoteReturnWindow.Position=new PixelPoint(area.Right-(int)(204*screen.Scaling),area.Y+(int)(30*screen.Scaling));
        }
        remoteReturnWindow.Show();
    }
    private void CloseRemoteReturnWindow()
    {
        var window=remoteReturnWindow;remoteReturnWindow=null;window?.Close();
    }
    private void ApplyPresentation()
    {
        var canUseCompact = collaboration is null ? model.CanUseCompactView : model.CanUseCollaborationView;
        var compact = canUseCompact && !settingsRequested;
        if(!compact)nativeSurface?.Protect();
        if (collaborationControl is not null)
        {
            frame.IsVisible = !compact; collaborationControl.IsVisible = compact;
            // A fresh transparency hint collection changes native window material even
            // when its contents are equal. Only apply chrome on a real mode change.
            if (compactMode != compact)
            {
                WindowDecorations = compact ? Avalonia.Controls.WindowDecorations.None : Avalonia.Controls.WindowDecorations.Full;
                TransparencyLevelHint = compact ? [WindowTransparencyLevel.Transparent] : [WindowTransparencyLevel.None];
                Background = compact ? Brushes.Transparent : new SolidColorBrush(Color.Parse("#F1F5FB"));
                CanResize = !compact;
            }
            if (compact)
            {
                compactMode = true;
                UpdateCollaborationSize(); collaborationControl.RefreshViewerVisibility();UpdateNativeSurface(); return;
            }
            collaborationControl.RefreshViewerVisibility();
        }
        details.IsVisible = !compact; brandHeader.IsVisible = !compact; compactTarget.IsVisible = compact;
        settingsToggle.IsVisible = canUseCompact || settingsRequested;
        settingsToggle.IsEnabled = canUseCompact;
        settingsToggle.Content = compact ? settingsIcon : "返回小窗";
        AutomationProperties.SetName(settingsToggle, compact ? "打开设置" : "返回小窗");
        ToolTip.SetTip(settingsToggle, compact ? "打开连接设置与状态" : "返回双按钮小窗");
        ToolTip.SetTip(windows, model.StatusMessage);
        ToolTip.SetTip(mac, model.StatusMessage);
        if (compactMode == compact){UpdateNativeSurface();return;}
        compactMode = compact;
        Background = compact && collaborationControl is not null
            ? Brushes.Transparent
            : new SolidColorBrush(Color.Parse(compact ? "#202024" : "#F1F5FB"));
        MacSettingsPalette.Apply(frame, !compact);
        Foreground = new SolidColorBrush(Color.Parse(compact ? "#F2F2F5" : "#243A59"));
        wirelessControl.SetLightPalette(!compact);
        frame.RowDefinitions = new RowDefinitions(compact ? "Auto,Auto,Auto" : "Auto,Auto,Auto,*");
        frame.VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        frame.Margin = compact ? new Thickness(16, 12) : new Thickness(24);
        frame.RowSpacing = compact ? 8 : 16;
        header.ColumnDefinitions = new ColumnDefinitions(compact ? "28,*,28" : "*,Auto");
        header.ColumnSpacing = compact ? 8 : 12;
        Grid.SetColumn(settingsToggle, compact ? 2 : 1);
        settingsToggle.Width = compact ? 28 : double.NaN;
        settingsToggle.Height = compact ? 28 : double.NaN;
        settingsToggle.MinWidth = compact ? 28 : 0;
        settingsToggle.MinHeight = compact ? 28 : 30;
        settingsToggle.Padding = compact ? new Thickness(0) : new Thickness(10, 5);
        settingsToggle.HorizontalContentAlignment = HorizontalAlignment.Center;
        settingsToggle.VerticalContentAlignment = VerticalAlignment.Center;
        windows.FontSize = mac.FontSize = compact ? 15 : 16;
        windows.MinHeight = mac.MinHeight = compact ? 48 : 56;
        windows.Height = mac.Height = compact ? 48 : 56;
        windows.Padding = mac.Padding = compact ? new Thickness(0) : new Thickness(16, 11);
        MinWidth = compact ? 340 : 520; MinHeight = compact ? 208 : 520;
        if (compact) compactSize = new Size(380, 208);
        Width = compact ? compactSize.Width : 640; Height = compact ? compactSize.Height : 680;
        UpdateNativeSurface();
    }
    private void UpdateNativeSurface()
    {
        if(nativeSurface is null)return;
        Dispatcher.UIThread.VerifyAccess();
        var security=settingsRequested||model.HasPairingPreview||model.PendingPeerFingerprint.Length>0;
        var ordinary=compactMode==true&&IsVisible&&IsEnabled&&WindowState!=WindowState.Minimized&&!closing&&!allowClose;
        if(!ordinary||security){nativeSurface.Protect();return;}
        try
        {
            var handle=TryGetPlatformHandle();
            nativeSurface.Update(true,false,handle is null?null:MacDesktopLocalEventGuard.GetWindowNumber(handle.Handle,handle.HandleDescriptor));
        }
        catch{nativeSurface.Protect();}
    }
    private void OnCollaborationChanged(Models.MacCollaborationSnapshot state) => Dispatcher.UIThread.Post(() => { if (!closing) Render(); });
    public void RequestExit() { explicitQuit = true; Close(); }
    private void UpdateCollaborationSize()
    {
        if (collaborationControl is null || compactMode != true || !collaborationControl.IsVisible) return;
        var requested = collaborationControl.RequestedSize;
        compactSize = requested; MinWidth = requested.Width; MinHeight = requested.Height;
        if (Math.Abs(Width - requested.Width) > 1) Width = requested.Width;
        if (Math.Abs(Height - requested.Height) > 1) Height = requested.Height;
        collaborationControl.ApplySavedPlacement();
    }
    private void QueueCompactSizeCheck()
    {
        if (closing || compactSizeCheckQueued || compactMode != true || !IsVisible || WindowState != WindowState.Normal) return;
        compactSizeCheckQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            compactSizeCheckQueued = false;
            if (closing || compactMode != true || !(collaboration is null ? model.CanUseCompactView : model.CanUseCollaborationView) || settingsRequested || !IsVisible || WindowState != WindowState.Normal) return;
            if (Math.Abs(ClientSize.Width - compactSize.Width) <= 1 && Math.Abs(ClientSize.Height - compactSize.Height) <= 1) return;
            // Native wake/restore can change the actual frame without changing our cached presentation mode.
            Width = compactSize.Width; Height = compactSize.Height;
            InvalidateMeasure(); InvalidateArrange();
        }, DispatcherPriority.Loaded);
    }
    private static void SetSelected(Button button, bool selected)
    {
        button.Classes.Set("selected", selected);
        button.Classes.Set("accent", selected);
        button.Classes.Set("secondary", !selected);
    }
    private async Task ImportPairingAsync()
    {
        var protection=nativeSurface?.Block();
        try
        {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "选择 Windows 导出的配对文件", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("配对 JSON") { Patterns = ["*.json"] }, FilePickerFileTypes.All] });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        using var bytes = new MemoryStream(); var buffer = new byte[4096]; int length;
        while ((length = await stream.ReadAsync(buffer)) > 0)
        {
            if (bytes.Length + length > 65536) throw new IOException("配对文件超过 64 KB，请从 Windows 重新导出。");
            await bytes.WriteAsync(buffer.AsMemory(0, length));
        }
        await model.PreviewPairingAsync(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
        }
        finally{protection?.Dispose();UpdateNativeSurface();}
    }
    private Button ActionButton(string label, Func<Task> action, string style = "secondary")
    {
        var button = new Button { Content = label }; button.Classes.Add(style);
        button.Click += async (_, _) => { try { await action(); } catch (Exception failure) { await ShowErrorAsync(failure.Message); } };
        return button;
    }
    private async Task ShowErrorAsync(string message)
    {
        var protection=nativeSurface?.Block();
        try
        {
        var close = new Button { Content = "知道了", HorizontalAlignment = HorizontalAlignment.Right };
        var content = new StackPanel { Spacing = 18, Margin = new Thickness(22) }; content.Children.Add(Text(message)); content.Children.Add(close);
        var dialog = new Window { Title = "请检查", Width = 480, Height = 210, Content = content, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        close.Click += (_, _) => dialog.Close(); await dialog.ShowDialog(this);
        }
        finally{protection?.Dispose();UpdateNativeSurface();}
    }
    private static Control Field(string label, string property)
    {
        var panel = new StackPanel { Spacing = 7 }; panel.Children.Add(Text(label, 12));
        var input = new TextBox(); input.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay }); panel.Children.Add(input); return panel;
    }
    private static Grid TwoColumns(Control first, Control second)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") }; first.Margin = new Thickness(0, 0, 10, 0); second.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(first, 0); Grid.SetColumn(second, 1); grid.Children.Add(first); grid.Children.Add(second); return grid;
    }
    private static WrapPanel Horizontal(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var item in controls) { item.Margin = new Thickness(0, 0, 10, 8); panel.Children.Add(item); }
        return panel;
    }
    private static Border Card(string title, Control content)
    {
        var panel = new StackPanel { Spacing = 14 }; panel.Children.Add(Text(title, 17, FontWeight.SemiBold)); panel.Children.Add(content);
        var border = new Border { Child = panel }; border.Classes.Add("card"); return border;
    }
    private static TextBlock Text(string value, double size = 14, FontWeight? weight = null) => new() { Text = value, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
}

