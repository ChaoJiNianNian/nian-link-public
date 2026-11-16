using System.ComponentModel;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.App.Hosting;
using DesktopToolbox.Windows.DeskSwitch;
using Microsoft.Win32;
using NianLink.Windows;

namespace NianLink.MultiDevice;

public partial class MainWindow : Window
{
    private readonly string root, engine;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private MultiDeviceCredentialVault? vault;
    private MultiPeerRuntime? host;
    private MultiPeerSettingsStore? hostSettings;
    private MultiPeerPairingCoordinator? pairing;
    private MultiPeerTrustChangeCoordinator? changes;
    private MultiPeerPairingPreview? offer;
    private long pairingReviewGeneration;
    private MultiPeerInvitation? unsavedInvitation;
    private PeerInvitationStore? peerStore;
    private MultiPeerSettingsStore? peerSettings;
    private MultiPeerClientOnboarding? onboarding;
    private MultiPeerHostPreview? hostPreview;
    private WindowsPeerLink? peer;
    private CollaborationClient? peerData;
    private string? peerReceiveDirectory, sharedRoot;
    private string sharedRelative = "";
    private bool busy, permitClose;
    private bool statusRefreshing;
    private const int ControlPort = 24821, KeyboardPort = 24800, DataPort = 24822;

    public MainWindow(string root, string engine)
    {
        this.root = root; this.engine = engine;
        InitializeComponent();
        BrandLogo.Source = new BitmapImage(new Uri($"pack://application:,,,/{typeof(MainWindow).Assembly.GetName().Name};component/Assets/NianLinkBrand.png", UriKind.Absolute));
#if FILELESS_PAIRING_CANDIDATE
        InitializeFilelessPairingUi();
#endif
        InitializeSettingsPages();
        RefreshDeviceControls();
        RefreshPairingGuide();
        var macDirectory = Path.Combine(AppContext.BaseDirectory, "MacClient");
#if DISCOVERY_PORTABLE
        MacExportButton.IsEnabled = false;
#else
        MacExportButton.IsEnabled = File.Exists(Path.Combine(macDirectory, "NianDeskLink-macOS-arm64.zip")) && File.Exists(Path.Combine(macDirectory, "manifest.json"));
#endif
        MacExportStatus.Text = MacExportButton.IsEnabled ? "适用于 Apple 芯片 Mac；包内含 Deskflow。" : "当前测试包未附带 Mac 客户端。";
        InitializeClipboard();
        InitializePreview();
        RefreshNetworks();
        Loaded += async (_, _) => await WorkAsync(async () =>
        {
            var name = await LocalDeviceName.OpenAsync(Path.Combine(root, "identity"));
            localIdentity = name; HostName.Text = name; PeerName.Text = name;
            aliasStore = await DeviceAliasStore.OpenAsync(root);
            ((DeviceLabelConverter)Resources["DeviceLabelConverter"]).Resolve = DisplayDeviceName;
            RefreshDisplayNames();
        });
        timer.Tick += async (_, _) => { PresentHostNetworkStatus(host?.NetworkLossStatus ?? HostNetworkLossStatus.None); RefreshDiscoveryUi(); await RefreshStatusAsync(); };
        timer.Start();
        Closing += CloseAsync;
    }

    private void RefreshNetworks()
    {
        var old = HostAddress.SelectedItem as IPAddress;
        var available = VerifiedLanBinding.GetEligibleAddresses();
        HostAddress.ItemsSource = available;
        HostAddress.SelectedItem = available.FirstOrDefault(x => x.Equals(old)) ?? available.FirstOrDefault();
        var peerOld = PeerDiscoveryNetwork.SelectedItem as IPAddress;
        PeerDiscoveryNetwork.ItemsSource = available;
        PeerDiscoveryNetwork.SelectedItem = available.FirstOrDefault(x => x.Equals(peerOld));
    }

    private async Task WorkAsync(Func<Task> action)
    {
#if FILELESS_PAIRING_CANDIDATE
        if (filelessDeliveryFinished is not null) { Feedback.Text = "正在完成安全配对，请稍候。"; return; }
#endif
        if (busy) return;
        busy = true; ModeTabs.IsEnabled = false; ClearFailureDetails();
        try { await action(); }
        catch (Exception error) { ShowFailure(error); }
        finally { ModeTabs.IsEnabled = true; busy = false; RefreshDeviceControls(); RefreshPairingGuide(); }
    }

    private void RefreshNetworks_Click(object sender, RoutedEventArgs e) => RefreshNetworks();

#if !DISCOVERY_PORTABLE
    private async void ExportMac_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        MacExportStatus.Text = "正在核对 Mac 测试包…";
        try
        {
            var prompts = new NativePrompts(Dispatcher, () => this);
            var exporter = new MacInstallerExport(Path.Combine(AppContext.BaseDirectory, "MacClient"), prompts, "multi-device-test");
            var result = JsonSerializer.SerializeToElement(await exporter.ExportAsync(CancellationToken.None), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (result.GetProperty("canceled").GetBoolean()) { MacExportStatus.Text = "已取消导出。"; return; }
            var path = result.GetProperty("path").GetString();
            MacExportStatus.Text = $"已保存：{path}\n请私下发给自己的 Mac；此测试包尚未公证。";
        }
        catch { MacExportStatus.Text = "导出未完成，请查看下方提示。"; throw; }
    });

#else
    private void ExportMac_Click(object sender, RoutedEventArgs e) => MacExportStatus.Text = "此发现测试包未附带 Mac 客户端。";
#endif

    private async void PrepareHost_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (host is not null) throw new InvalidOperationException("主控已准备；需要更换网络时请退出测试版后重新打开。");
        var name = HostName.Text.Trim();
        DeskflowProfile.ValidateName(name);
        if (HostAddress.SelectedItem is not IPAddress address) throw new InvalidOperationException("请先选择活动局域网。");
        var binding = VerifiedLanBinding.VerifyCurrentNetwork(address, userApproved: true);
        var newVault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(root, "host", "trust"), new WindowsCredentialProtector());
        MultiPeerRuntime? newHost = null;
        MultiPeerSettingsStore? newSettings = null;
        try
        {
            newSettings = await MultiPeerSettingsStore.OpenAsync(Path.Combine(root, "host", "settings"), new WindowsCredentialProtector());
            newHost = MultiPeerRuntime.CreateForLan(newVault, Path.Combine(root, "host", "runtime"), name,
                binding, ControlPort, DataPort, engine, MultiPeerKeyboardSession.ExperimentalEngineSha256, newSettings);
            var fingerprint = await newHost.PrepareControlIdentityAsync(CancellationToken.None);
            var newPairing = new MultiPeerPairingCoordinator(newVault, name, address.ToString(), ControlPort, DataPort, KeyboardPort, fingerprint);
            vault = newVault; host = newHost; pairing = newPairing; hostSettings = newSettings; discoveryHostBinding = binding;
            changes = new MultiPeerTrustChangeCoordinator(vault, host);
            HostName.IsReadOnly = true; HostAddress.IsEnabled = false;
            HostIdentity.Text = "主控控制通道指纹：\n" + fingerprint;
            RefreshDevices();
            var hasDevices = HostDevices.Items.Count > 0;
            Feedback.Text = hasDevices ? "主控已准备。选择设备并启动共享。" : "主控已准备。导入另一台电脑的身份卡。";
            ShowSettingsPage(hasDevices ? "HostDevicesCard" : "HostPairingCard");
        }
        catch { if (newHost is not null) await newHost.DisposeAsync(); if (newSettings is not null) await newSettings.DisposeAsync(); await newVault.DisposeAsync(); throw; }
    });

    private async void ImportOffer_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (pairing is null)
        {
            HostPairingHelp.Text = "先在第 1 步点击「准备主控」，再回这里导入身份卡。";
            throw new InvalidOperationException(HostPairingHelp.Text);
        }
        HostPairingHelp.Text = "";
        var path = OpenJson(); if (path is null) return;
        await StopDiscoveryUiAsync();
        if (offer is { } oldReview) pairing.CancelReview(oldReview.Id);
        offer = null; OfferDetails.Text = ""; pairingFileSaved = false;
        var generation = ++pairingReviewGeneration;
        var json = await ReadJsonAsync(path);
        if (generation != pairingReviewGeneration) throw new InvalidOperationException("身份选择已变化，请重新导入。");
        offer = pairing.Review(json, DateTimeOffset.UtcNow);
        OfferDetails.Text = $"设备：{offer.Name} · {offer.Platform}\n键鼠指纹：\n{offer.KeyboardFingerprint}\n有效期至：{offer.ExpiresAtUtc.ToLocalTime():HH:mm:ss}";
        Feedback.Text = "请对照另一台电脑显示的完整键鼠指纹。";
    });

    private async void ConfirmOffer_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (changes is null || pairing is null)
        {
            HostPairingHelp.Text = "请先在第 1 步点击「准备主控」，再导入另一台电脑的身份卡。";
            throw new InvalidOperationException(HostPairingHelp.Text);
        }
        if (offer is null)
        {
            HostPairingHelp.Text = "请先在第 2 步导入另一台电脑的身份卡。";
            throw new InvalidOperationException(HostPairingHelp.Text);
        }
        HostPairingHelp.Text = "";
        var reviewed = offer;
        var generation = pairingReviewGeneration;
        var destination = PairingReviewGuard.ChooseDestination(reviewed, generation, () => offer, () => pairingReviewGeneration,
            () => DateTimeOffset.UtcNow,
            () => AskBlueConfirmation("核对设备", $"请与 {reviewed.Name} 上的完整键鼠指纹逐字核对：\n\n{reviewed.KeyboardFingerprint}\n\n一致后才能继续。", "一致，确认", CancellationToken.None),
            () => SaveJson("年年互联配对.json"));
        if (destination is null) return;
        offer = null; pairingReviewGeneration++; OfferDetails.Text = "";
        HostKeyboardSnapshot = null;
        await CloseHostMiniAsync();
        await CancelTransferAsync();
        try { unsavedInvitation = await changes.AddAsync(pairing, reviewed.Id, reviewed.KeyboardFingerprint, DateTimeOffset.UtcNow); }
        finally { await ReleaseDiscoveryReviewAsync(reviewed.Id); }
        RefreshDevices();
        await WriteInvitationAsync(destination);
    });

    private async void SaveInvitation_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (unsavedInvitation is null)
        {
            if (changes is null || pairing is null || HostDevices.SelectedItem is not TrustedDeviceIdentity device)
                throw new InvalidOperationException("请在设备列表选择需要重新导出的设备。");
            if (!AskBlueConfirmation("导出配对文件", $"重新导出 {DisplayDeviceName(device.Name)} 的配对文件？文件包含该设备的控制凭证，请只发给它本人。", "确认导出", CancellationToken.None)) return;
            var destination = SaveJson("年年互联配对.json"); if (destination is null) return;
            unsavedInvitation = await changes.ExportInvitationAsync(pairing, device.Name);
            await WriteInvitationAsync(destination);
            return;
        }
        var pendingDestination = SaveJson("年年互联配对.json");
        if (pendingDestination is not null) await WriteInvitationAsync(pendingDestination);
    });

    private async Task WriteInvitationAsync(string destination)
    {
        var value = unsavedInvitation ?? throw new InvalidOperationException("没有配对文件。");
        await File.WriteAllTextAsync(destination, value.ToJson());
        unsavedInvitation = null; pairingFileSaved = true;
        Feedback.Text = "配对文件已保存。请私下发送到对应电脑并导入。";
    }

    private void RefreshDevices() { if (vault is not null) HostDevices.ItemsSource = vault.GetTrustedDevices(); ResetDisplayGroup(); }

    private async void StartHost_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (host is null || vault is null) throw new InvalidOperationException("请先准备主控。");
        if (host.Devices.Count != 0)
        {
            var state = await host.GetKeyboardStateAsync(CancellationToken.None);
            Feedback.Text = state.ProcessState == DeskLinkProcessState.Running
                ? "共享已启动，可继续在其他电脑连接。"
                : "共享未正常运行，请先停止共享，再重新启动。";
            return;
        }
        HostKeyboardSnapshot = null;
        await host.StartAsync(vault.GetTrustedDevices(), CancellationToken.None);
        await RefreshHostClipboardToggleAsync();
        await RefreshHostRefreshChoiceAsync();
        Feedback.Text = "共享已启动，等待设备连接。";
    });

    private async void SwitchHost_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (host is null || HostDevices.SelectedItem is not TrustedDeviceIdentity device) throw new InvalidOperationException("请选择一台设备。");
        var result = await host.SwitchKeyboardAsync(device.Name, CancellationToken.None);
        if (!result.InputTransferred) throw new InvalidOperationException(result.Error ?? "设备未确认键鼠切换。");
        Feedback.Text = SwitchFeedback(result, "键鼠已切到 " + DisplayDeviceName(device.Name) + "。");
    });

    private async void ReturnHost_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (host is null) throw new InvalidOperationException("请先准备主控。");
        var result = await host.SwitchKeyboardAsync(HostName.Text.Trim(), CancellationToken.None);
        if (!result.InputTransferred) throw new InvalidOperationException(result.Error ?? "主控未确认键鼠归还。");
        Feedback.Text = SwitchFeedback(result, "键鼠已归还主控。");
    });

    private async void RevokeHost_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (changes is null || HostDevices.SelectedItem is not TrustedDeviceIdentity device) throw new InvalidOperationException("请选择一台设备。");
        if (!AskBlueConfirmation("移除设备", $"移除 {DisplayDeviceName(device.Name)}？这会短暂重启其他设备的共享。", "确认移除", CancellationToken.None)) return;
        HostKeyboardSnapshot = null;
        await CloseHostMiniAsync();
        await CancelTransferAsync();
        hostPreviewShares.Remove(device.Name); hostCaptureCadences.Remove(device.Name);
        await changes.RevokeAsync(device.Name); RefreshDevices(); Feedback.Text = "设备已移除。";
    });

    private async void StopHost_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    { await CloseHostMiniAsync(); await CancelTransferAsync(); await StopHostPreviewAsync(); if (host is not null) await host.StopAsync(CancellationToken.None); HostKeyboardSnapshot = new DeskLinkState { ProcessState = DeskLinkProcessState.Stopped }; await RefreshHostClipboardToggleAsync(); Feedback.Text = "主控共享已停止。"; });

    private async Task EnsurePeerStoreAsync()
    {
        if (peerStore is not null) return;
        var name = PeerName.Text.Trim(); DeskflowProfile.ValidateName(name);
        var opened = await PeerInvitationStore.OpenAsync(Path.Combine(root, "client", "trust"), new WindowsCredentialProtector());
        MultiPeerSettingsStore? openedSettings = null;
        try
        {
            MultiPeerInvitation? previous;
#if FILELESS_PAIRING_CANDIDATE
            // An unresolved promotion must remain available to the recovery UI;
            // no invitation may be used for an active connection in this state.
            try { previous = opened.Get(); }
            catch (PairingRecoveryRequiredException) { previous = null; }
#else
            previous = opened.Get();
#endif
            if (previous is not null && !string.Equals(previous.PeerName, name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("本机名称与已保存的配对不一致，请使用原名称。");
            openedSettings = await MultiPeerSettingsStore.OpenAsync(Path.Combine(root, "client", "settings"), new WindowsCredentialProtector());
            var savedReceive = previous is null ? null : openedSettings.Get(InvitationKey(previous)).ReceiveDirectory;
            peerStore = opened; peerSettings = openedSettings; onboarding = new MultiPeerClientOnboarding(opened, name); PeerName.IsReadOnly = true;
            peerReceiveDirectory = savedReceive;
            // Start with clipboard disabled until the user explicitly enables it.
            peerClipboardEnabled = false;
        }
        catch { if (openedSettings is not null) await openedSettings.DisposeAsync(); await opened.DisposeAsync(); throw; }
    }

    private async void CreateOffer_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        await EnsurePeerStoreAsync();
        var destination = SaveJson("年年互联设备身份.json"); if (destination is null) return;
        var now = DateTimeOffset.UtcNow;
        var card = await PeerIdentityOffer.CreateAsync(Path.Combine(root, "client", "keyboard"), PeerName.Text.Trim(), "windows", now);
        await File.WriteAllTextAsync(destination, card.ToJson(now));
        PeerIdentity.Text = $"本机键鼠指纹：\n{card.KeyboardFingerprint}\n有效期至：{card.ExpiresAtUtc.ToLocalTime():HH:mm:ss}";
        Feedback.Text = "身份卡已保存，请发给主控电脑。";
    });

    private async void ImportInvitation_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (peer is not null) throw new InvalidOperationException("请先停止连接。");
        await EnsurePeerStoreAsync();
#if FILELESS_PAIRING_CANDIDATE
        await RequireSettledPeerPairingAsync();
#endif
        var path = OpenJson(); if (path is null) return;
        hostPreview = null; InvitationDetails.Text = ""; peerPairingConfirmed = false;
        hostPreview = onboarding!.Review(await ReadJsonAsync(path));
        InvitationDetails.Text = $"主控：{hostPreview.HostName} · {hostPreview.Host}\n控制通道指纹：\n{hostPreview.ControlFingerprint}";
        Feedback.Text = "请与主控电脑核对完整控制通道指纹。";
    });

    private async void ConfirmInvitation_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (onboarding is null || hostPreview is null) throw new InvalidOperationException("请先导入配对文件。");
#if FILELESS_PAIRING_CANDIDATE
        await RequireSettledPeerPairingAsync();
#endif
        if (!AskBlueConfirmation("核对主控", $"请与主控电脑逐字核对：\n\n{hostPreview.ControlFingerprint}\n\n一致后才能继续。", "一致，确认", CancellationToken.None)) return;
        var selected = hostPreview; hostPreview = null;
        await onboarding.ConfirmAsync(selected.Id, selected.ControlFingerprint);
        peerReceiveDirectory = peerSettings!.Get(InvitationKey(peerStore!.Get()!)).ReceiveDirectory;
        peerClipboardEnabled = false;
        peerClipboardCursor = null;
        peerPairingConfirmed = true;
        Feedback.Text = "配对已保存。现在可以开始连接。";
    });

    private async void StartPeer_Click(object sender, RoutedEventArgs e) => await WorkAsync(StartPeerInsideAsync);

    private async Task StartPeerInsideAsync()
    {
        if (peer is not null) { Feedback.Text = "连接已启动，正在等待主控。"; return; }
        await EnsurePeerStoreAsync();
#if FILELESS_PAIRING_CANDIDATE
        await RequireSettledPeerPairingAsync();
#endif
        var invitation = peerStore!.Get() ?? throw new InvalidOperationException("请先导入并确认主控配对。");
        await using var image = new FileStream(engine, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(image));
        if (!string.Equals(hash, MultiPeerKeyboardSession.ExperimentalEngineSha256, StringComparison.Ordinal)) throw new InvalidDataException("测试核心校验失败。");
        var candidate = new WindowsPeerLink(Path.Combine(root, "client", "keyboard"), engine, invitation, userApproved: true);
#if NIAN_PEER_EMERGENCY_RESEARCH
        try { await candidate.StartAsync(); BindPeerControl(candidate);
            EnsurePeerEmergencyWindow(ReturnPeerEmergencyAsync);
            peerData = MultiPeerCollaborationClient.Open(invitation, true);
            var data = peerData;
            peerLiveUpload = new LivePreviewUploadController((demand, token) => data.LivePreview.UploadAsync(demand, livePreviewCapture, token));
            peerRefreshSeconds = peerSettings!.Get(InvitationKey(invitation)).PreviewRefreshSeconds;
            SetRefreshChoice(PeerRefreshChoice, peerRefreshSeconds); PeerRefreshChoice.IsEnabled = true;
            PeerClipboard.IsEnabled = true; PeerClipboard.IsChecked = peerClipboardEnabled; peerClipboardCursor = null;
            peer = candidate;
            peerEmergencyWindow?.ShowConnectionState(true, PeerControlUnresolved);
        }
        catch { RetirePeerControl(candidate);
            if (ReferenceEquals(peer, candidate)) peer = null;
            PresentPeerEmergencyStopped();
            try
            {
                if (peerLiveUpload is { } failedUpload) { peerLiveUpload = null; await failedUpload.DisposeAsync(); }
                await ClosePeerDataAsync();
            }
            finally { await candidate.DisposeAsync(); }
            throw; }
#else
        try { await candidate.StartAsync(); BindPeerControl(candidate); peerData = MultiPeerCollaborationClient.Open(invitation, true); peer = candidate;
            var data = peerData;
            peerLiveUpload = new LivePreviewUploadController((demand, token) => data.LivePreview.UploadAsync(demand, livePreviewCapture, token));
            peerRefreshSeconds = peerSettings!.Get(InvitationKey(invitation)).PreviewRefreshSeconds;
            SetRefreshChoice(PeerRefreshChoice, peerRefreshSeconds); PeerRefreshChoice.IsEnabled = true;
            PeerClipboard.IsEnabled = true; PeerClipboard.IsChecked = peerClipboardEnabled; peerClipboardCursor = null; }
        catch { RetirePeerControl(candidate); await candidate.DisposeAsync(); throw; }
#endif
        Feedback.Text = "正在连接。首次连接时请核对键鼠指纹。";
    }

    private async void ConfirmKeyboard_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (peer is null) throw new InvalidOperationException("请先开始连接。");
        var state = await peer.GetStateAsync();
        var fingerprint = state.PendingPeerFingerprint ?? throw new InvalidOperationException("当前没有等待确认的键鼠指纹。");
        if (!AskBlueConfirmation("核对键鼠", $"请与主控显示的键鼠指纹逐字核对：\n\n{fingerprint}\n\n一致后才能继续。", "一致，确认", CancellationToken.None)) return;
        await peer.ConfirmKeyboardPeerAsync(fingerprint); Feedback.Text = "键鼠身份已确认。";
    });

    private async void SwitchPeer_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (peer is null || peerStore?.Get() is not { } invitation) throw new InvalidOperationException("请先连接主控。");
        var result = await SwitchPeerAsync(peer, invitation, invitation.PeerName);
        if (!result.InputTransferred) throw new InvalidOperationException("主控未确认切换。");
        Feedback.Text = SwitchFeedback(result, "键鼠已切到本机。");
    });
    private async void ReturnPeer_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (peer is null || peerStore?.Get() is not { } invitation) throw new InvalidOperationException("请先连接主控。");
        var result = await SwitchPeerAsync(peer, invitation, invitation.HostName);
        if (!result.InputTransferred) throw new InvalidOperationException("主控未确认归还。");
        Feedback.Text = SwitchFeedback(result, "键鼠已归还主控。");
    });
    private static string SwitchFeedback(DeskLinkSwitchResult result, string keyboardMessage) => result.VideoStatus switch
    {
        "verified" => "键鼠和画面已切换。",
        "acceptedUnverified" => "键鼠已切换，显示器已收到命令，请确认画面。",
        "failed" => "键鼠已切换，显示器切换失败，请检查画面。",
        _ => keyboardMessage
    };
    private async void StopPeer_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    { if (peer is { } current) StopPeerControl(current); await ClosePeerMiniAsync(); await StopPeerInsideAsync(); });

    private async Task StopPeerInsideAsync()
    { if (peer is { } current) StopPeerControl(current); await CancelTransferAsync(); await StopPeerPreviewAsync(); if (peer is not null) { RetirePeerControl(peer); await peer.StopAsync(); await peer.DisposeAsync(); peer = null; }
#if NIAN_PEER_EMERGENCY_RESEARCH
      PresentPeerEmergencyStopped();
#endif
      peerKeyboardSnapshot = null; await ClosePeerDataAsync(); PeerClipboard.IsEnabled = false; peerClipboardCursor = null; PresentPeerStoppedStatus(); Feedback.Text = PeerControlUnresolved ? "连接已停止，键鼠归还尚未确认，请检查键鼠所在设备。" : "连接已停止。"; }

    private TrustedDeviceIdentity SelectedHostDevice() => HostDevices.SelectedItem as TrustedDeviceIdentity
        ?? throw new InvalidOperationException("请先在「设备」页选择一台电脑。");

    private async void HostReceive_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        var device = SelectedHostDevice();
        if (host is null) throw new InvalidOperationException("请先启动主控共享。");
        var folder = ChooseFolder(); if (folder is null) return;
        await host.SetReceiveDirectoryAsync(device.Name, folder, CancellationToken.None);
        HostFilesStatus.Text = DisplayDeviceName(device.Name) + " 的收件位置：" + folder;
    });

    private async void HostSend_Click(object sender, RoutedEventArgs e) => await WorkAsync(() =>
    {
        var device = SelectedHostDevice();
        var current = host ?? throw new InvalidOperationException("请先启动主控共享。");
        var files = ChooseFiles(); if (files.Length != 0) BeginHostTransfer(current, device.Name, files);
        return Task.CompletedTask;
    });

    private async void HostShare_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        var device = SelectedHostDevice();
        if (host is null) throw new InvalidOperationException("请先启动主控共享。");
        var folder = ChooseFolder(); if (folder is null) return;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (string.IsNullOrWhiteSpace(name)) name = "共享文件夹";
        await host.SetSharedDirectoriesAsync(device.Name, [new SharedDirectory("main", name, folder)], CancellationToken.None);
        HostFilesStatus.Text = "已向 " + DisplayDeviceName(device.Name) + " 共享：" + folder;
    });

    private async void PeerReceiveDirectory_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        await EnsurePeerStoreAsync();
        var invitation = peerStore!.Get() ?? throw new InvalidOperationException("请先确认主控配对。");
        if (transferTask is { IsCompleted: false }) throw new InvalidOperationException("文件正在传输，请先等待或取消。");
        var folder = ChooseFolder(); if (folder is null) return;
        var key = InvitationKey(invitation);
        await peerSettings!.SetAsync(key, peerSettings.Get(key) with { ReceiveDirectory = folder });
        peerReceiveDirectory = folder; PeerFilesStatus.Text = "收件位置：" + folder;
    });

    private async void PeerSend_Click(object sender, RoutedEventArgs e) => await WorkAsync(() =>
    {
        var data = peerData ?? throw new InvalidOperationException("请先连接主控。");
        var files = ChooseFiles(); if (files.Length != 0) BeginPeerTransfer(data, files);
        return Task.CompletedTask;
    });

    private async void PeerReceive_Click(object sender, RoutedEventArgs e) => await WorkAsync(() =>
    {
        var data = peerData ?? throw new InvalidOperationException("请先连接主控。");
        if (peerReceiveDirectory is null) throw new InvalidOperationException("请先选择本机收件文件夹。");
        BeginPeerReceive(data, peerReceiveDirectory);
        return Task.CompletedTask;
    });

    private async void PeerShare_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    { sharedRoot = null; sharedRelative = ""; await RefreshSharedAsync(); });

    private async void PeerShareBack_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (sharedRoot is null) return;
        if (sharedRelative.Length == 0) sharedRoot = null;
        else { var separator = sharedRelative.LastIndexOf('/'); sharedRelative = separator < 0 ? "" : sharedRelative[..separator]; }
        await RefreshSharedAsync();
    });

    private async void PeerDownloadShared_Click(object sender, RoutedEventArgs e) => await WorkAsync(() =>
    {
        var data = peerData ?? throw new InvalidOperationException("请先连接主控。");
        if (peerReceiveDirectory is null) throw new InvalidOperationException("请先选择本机收件文件夹。");
        if (SharedFiles.SelectedItem is not SharedItem item) throw new InvalidOperationException("请选择共享项目。");
        BeginPeerDownload(data, item, peerReceiveDirectory);
        return Task.CompletedTask;
    });

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var ready = ModeTabs.SelectedIndex == 0
            ? host is not null && HostDevices.SelectedItem is TrustedDeviceIdentity device && host.Devices.Any(x => x.Name == device.Name)
            : peerData is not null;
        e.Effects = !busy && transferTask is not { IsCompleted: false } && ready && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        var hostMode = ModeTabs.SelectedIndex == 0;
        var device = hostMode ? HostDevices.SelectedItem as TrustedDeviceIdentity : null;
        await WorkAsync(() =>
        {
            if (hostMode)
            {
                if (host is null || device is null) throw new InvalidOperationException("请先选择已连接的设备。");
                BeginHostTransfer(host, device.Name, paths);
            }
            else
            {
                var data = peerData ?? throw new InvalidOperationException("请先连接主控。");
                BeginPeerTransfer(data, paths);
            }
            return Task.CompletedTask;
        });
    }

    private async void SharedFiles_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => await WorkAsync(async () =>
    {
        if (SharedFiles.SelectedItem is not SharedItem item) return;
        var data = peerData ?? throw new InvalidOperationException("请先连接主控。");
        if (item.IsDirectory) { sharedRoot = item.RootId; sharedRelative = item.RelativePath; await RefreshSharedAsync(); return; }
        var preview = await data.GetSharedPreviewAsync(item.RootId, item.RelativePath);
        if (preview.ContentType == "text/plain")
        {
            var text = new System.Windows.Controls.TextBox { Text = System.Text.Encoding.UTF8.GetString(preview.Bytes), IsReadOnly = true,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Padding = new Thickness(16) };
            new Window { Owner = this, Title = preview.Name, Width = 700, Height = 500, Content = text }.ShowDialog();
        }
        else if (preview.ContentType.StartsWith("image/", StringComparison.Ordinal))
        {
            using var bytes = new MemoryStream(preview.Bytes);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = bytes; bitmap.EndInit(); bitmap.Freeze();
            new Window { Owner = this, Title = preview.Name, Width = 700, Height = 500,
                Content = new System.Windows.Controls.Image { Source = bitmap, Stretch = System.Windows.Media.Stretch.Uniform } }.ShowDialog();
        }
        else throw new NotSupportedException("此格式请先保存到本机，再用对应软件打开。");
    });

    private async Task RefreshSharedAsync()
    {
        var data = peerData ?? throw new InvalidOperationException("请先连接主控。");
        var listing = await data.ListSharedAsync(sharedRoot, sharedRelative);
        SharedFiles.ItemsSource = listing.Items;
        PeerFilesStatus.Text = listing.Items.Count == 0 ? "此位置没有可显示的项目。" : "双击文件夹打开，双击文件预览。";
    }

    private void PresentHostNetworkStatus(HostNetworkLossStatus status)
    {
        var message = status switch
        {
            HostNetworkLossStatus.Stopping => "主控局域网已中断，正在停止共享，请稍候。",
            HostNetworkLossStatus.AwaitingNetworkApproval => "主控局域网已中断，共享已停止。请关闭并重新打开测试版，再确认网络并准备主控。",
            HostNetworkLossStatus.CleanupUnconfirmed => "共享停止或键鼠归还未确认。请检查设备、重试停止，再重新打开测试版。",
            _ => null
        };
        HostNetworkNotice.Text = message ?? string.Empty;
        HostNetworkNotice.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RefreshStatusAsync()
    {
        PresentHostNetworkStatus(host?.NetworkLossStatus ?? HostNetworkLossStatus.None);
        if (busy || statusRefreshing) return;
        statusRefreshing = true;
        try
        {
            if (host is not null && host.NetworkLossStatus != HostNetworkLossStatus.Stopping)
            {
                var state = await host.GetKeyboardStateAsync(CancellationToken.None);
                ApplyHostKeyboardState(state);
                if (host.Devices.Count > 0) await PollTargetReturnAsync();
                await PollHostClipboardAsync(state);
                await PollHostPreviewAsync();
                if (hostMiniService is not null) await hostMiniService.RefreshAsync();
                var sharing = state.ProcessState switch { DeskLinkProcessState.Running => "运行中", DeskLinkProcessState.Starting => "正在启动", DeskLinkProcessState.Stopping => "正在停止", DeskLinkProcessState.Faulted => "出现错误", _ => "已停止" };
                HostStatus.Text = $"共享：{sharing} · 当前键鼠：{(state.ActiveComputer is { } activeName ? DisplayDeviceName(activeName) : "等待")}\n已连接：{string.Join("、", state.ConnectedComputers.Select(DisplayDeviceName))}\n主控键鼠指纹：{state.LocalFingerprint ?? "启动共享后显示"}";
                if (host.LastError is not null && host.NetworkLossStatus == HostNetworkLossStatus.None) HostStatus.Text += "\n" + host.LastError;
            }
            if (peer is not null)
            {
                var state = await peer.GetStateAsync();
                peerKeyboardSnapshot = state; RefreshPairingGuide();
                if ((host is null || host.Devices.Count == 0) && state.ConnectionState == DeskLinkConnectionState.Connected && state.PendingPeerFingerprint is null)
                    await PollTargetReturnAsync();
                await PollPeerClipboardAsync();
                await PollPeerPreviewAsync();
                if (peerMiniService is not null) await peerMiniService.RefreshAsync();
                var peerConnection = state.ConnectionState switch { DeskLinkConnectionState.Connected => "已连接", DeskLinkConnectionState.Connecting => "连接中", DeskLinkConnectionState.Listening => "等待连接", _ => "未连接" };
                PeerStatus.Text = $"连接：{peerConnection}\n" + (state.PendingPeerFingerprint is null ? "" : "待核对键鼠指纹：\n" + state.PendingPeerFingerprint + "\n") + (state.LastError ?? "");
                if (PeerControlUnconfirmed) PeerStatus.Text += "\n键鼠操作尚未确认，请检查键鼠所在设备。";
            }
        }
        catch (System.Runtime.InteropServices.ExternalException error) { ShowFailure(error, "剪贴板"); }
        catch (Exception error)
        {
            HostKeyboardSnapshot = null; peerKeyboardSnapshot = null; RefreshDeviceControls(); RefreshPairingGuide();
            // A peer status read may have failed while its control outcome is
            // unknown; keep an existing emergency-return entry available.
            Exception? returnCloseFailure = null;
            if (peer is null || !PeerControlUnresolved)
            {
                try { CloseTargetReturn(); }
                catch (Exception closeError) { returnCloseFailure = closeError; }
            }
            if (peer is not null && PeerControlUnconfirmed) PeerStatus.Text = "键鼠操作尚未确认，请检查键鼠所在设备。";
            // A disconnected peer must not receive clipboard content copied while offline.
            hostClipboardTarget = null; hostClipboardCursor = null; peerClipboardCursor = null;
            SetPreview(HostPreviewImage, HostPreviewStatus, PreviewFrame.Off("offline"));
            SetPreview(PeerPreviewImage, PeerPreviewStatus, PreviewFrame.Off("offline"));
            ShowFailure(error, "状态读取");
            if (returnCloseFailure is not null)
            {
                Feedback.Text += "；归还窗口未能关闭，请检查键鼠。";
                ErrorDetailsText.Text += "\n归还窗口关闭失败：" + returnCloseFailure;
            }
        }
        finally { statusRefreshing = false; }
    }

    private void ApplyHostKeyboardState(DeskLinkState state)
    {
        HostKeyboardSnapshot = state;
        RefreshDeviceControls();
        hostEngineProcessId = state.ProcessId;
    }

    private async void CloseAsync(object? sender, CancelEventArgs e)
    {
        if (permitClose) return;
        e.Cancel = true;
        if (busy) return;
        if (unsavedInvitation is not null)
        {
            var destination = SaveJson("年年互联配对.json");
            if (destination is null) { Feedback.Text = "请先保存新设备的配对文件，再退出测试版。"; return; }
            try { await WriteInvitationAsync(destination); }
            catch (Exception error) { ShowFailure(error, "配对文件保存"); return; }
        }
        busy = true; timer.Stop();
        try
        {
            await CloseDiscoveryUiAsync();
            if (displayBridge?.HasUnfinishedChild == true) throw new InvalidOperationException("显示器组件尚未确认退出，请稍后再试。");
            await CancelTransferAsync();
            await CloseHostMiniAsync();
            if (peer is { } currentPeer) StopPeerControl(currentPeer);
            await ClosePeerMiniAsync();
            await StopPeerPreviewAsync(); await StopHostPreviewAsync();
            if (peer is not null) { RetirePeerControl(peer); await peer.StopAsync(); await peer.DisposeAsync(); peer = null; }
            await ClosePeerDataAsync();
            if (host is not null) { await host.StopAsync(CancellationToken.None); await host.DisposeAsync(); host = null; }
            if (hostSettings is not null) { await hostSettings.DisposeAsync(); hostSettings = null; }
            if (peerSettings is not null) { await peerSettings.DisposeAsync(); peerSettings = null; }
            if (peerStore is not null) { await peerStore.DisposeAsync(); peerStore = null; }
            if (vault is not null) { await vault.DisposeAsync(); vault = null; }
            DisposePreviewObservation();
            // Cleanup can complete synchronously. Defer the second Close until WPF
            // has returned from the original, cancelled Closing event.
            permitClose = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        }
        catch (Exception error) { permitClose = false; ShowFailure(error, "退出前停止共享"); timer.Start(); }
        finally { busy = false; }
    }

    private static string? OpenJson()
    {
        var dialog = new OpenFileDialog { Filter = "配对文件 (*.json)|*.json", CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    private static string? SaveJson(string fileName)
    {
        var dialog = new SaveFileDialog { Filter = "配对文件 (*.json)|*.json", FileName = fileName, AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    private static string? ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择文件夹", Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
    private static string[] ChooseFiles()
    {
        var dialog = new OpenFileDialog { Title = "选择要发送的文件", Multiselect = true, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
    private static string InvitationKey(MultiPeerInvitation invitation)
    {
        var token = Convert.FromBase64String(invitation.Token);
        try { return Convert.ToHexString(SHA256.HashData(token)); }
        finally { CryptographicOperations.ZeroMemory(token); }
    }
    private static async Task<string> ReadJsonAsync(string path)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 4096) throw new InvalidDataException("文件无效或过大。");
        return await File.ReadAllTextAsync(path);
    }
}
