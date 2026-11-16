using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.ViewModels;
using Nian.Input.Security;
#if DISCOVERY_CANDIDATE
using NianLink.MacDiscoveryCandidate;
using System.Net;
#endif

namespace DesktopToolbox.Mac.Views;

/// <summary>Separate candidate window; it does not load the installed one-to-one profile.</summary>
public sealed partial class MacMultiDeviceWindow : Window
{
    private readonly string root, engine;
    private readonly bool startupCheck;
    private readonly TextBlock identity = Label("正在准备本机身份…"), pairing = Label("还没有导入配对文件。"),
        state = Label("尚未连接。"), feedback = Label("测试版使用独立资料，不会更改日常版配对。 ");
    private readonly TextBlock fileStatus = Label("连接后可选择文件和收件文件夹。");
    // These controls sit on a fixed light card even when macOS uses dark appearance.
    private readonly CheckBox clipboardChoice = new() { Content = "同步文字剪贴板", Foreground = new SolidColorBrush(Color.Parse("#243A59")) };
    private readonly CheckBox previewChoice = new() { Content = "查看对方画面", Foreground = new SolidColorBrush(Color.Parse("#243A59")) };
    private readonly CheckBox shareScreenChoice = new() { Content = "允许对方看 Mac 画面", Foreground = new SolidColorBrush(Color.Parse("#243A59")) };
    private readonly ComboBox refreshChoice = new() { ItemsSource = new[] { "30 秒（默认）", "1 秒", "5 秒", "10 秒", "60 秒" }, SelectedIndex = 0, IsEnabled = false };
    private readonly Image previewImage = new() { Height = 190, Stretch = Stretch.Uniform };
    private readonly TextBlock previewPlaceholder = new() { Text = "等待对方画面", Foreground = new SolidColorBrush(Color.Parse("#65768B")),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox sharedFiles = new() { MinHeight = 55 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private MacMultiPeerClient? client;
    private MacCollaborationService? collaboration;
#if MULTI_DEVICE_CANDIDATE
    // Internal factory seam supports isolated lifecycle verification; production uses the existing factory.
    private Func<string,Func<CancellationToken,Task<DeskLinkPairingBundle?>>,IDeskflowPreviewControl?,MacCollaborationService>? collaborationFactory=null;
    private readonly SemaphoreSlim collaborationLifecycle=new(1,1);
    private MacCollaborationService? pendingCollaboration;
#endif
    private MacRemotePreviewWindow? detachedPreview;
    private MacRemotePreviewReturnWindow? remoteReturn;
#if !NIAN_MAC_RETURN_RESEARCH
    private bool remoteReturnClosing;
#endif
#if NIAN_MAC_RETURN_RESEARCH
    private Func<MacRemotePreviewReturnWindow>? researchWindowFactory=null;
    private bool researchCreating, researchEntryReady, researchStopInProgress, researchReleasing, researchEntryStopped;
    private Func<Task<bool>>? researchExitConfirmation=null;
    private long researchGeneration;
    private bool researchConnectionActive;
    private string? researchSessionId;
    private IDeskflowPreviewControl? researchBackend;
#endif
    private Action<DesktopToolbox.Mac.Models.MacCollaborationSnapshot>? collaborationObserver;
    private long collaborationEpoch;
    private long viewChoiceVersion, shareChoiceVersion;
    private string selectedPage = "设备";
    private bool miniViewerVisible, miniDetachedViewerVisible;
    private DateTimeOffset lastPreviewTimestamp;
    private string? sharedRoot;
    private string sharedRelative = "";
    private CancellationTokenSource? sharedDownloadCancellation;
    private Task? sharedDownloadTask;
    private MultiPeerHostPreview? preview;
    private MacMultiMiniWindow? miniWindow;
    private MacMultiMiniLink? miniLink;
    private bool busy, polling, closing, allowClose, updatingRefreshChoice;
    private static readonly int[] RefreshIntervals = [30, 1, 5, 10, 60];
#if DISCOVERY_CANDIDATE
    private readonly DiscoveryUiController discovery = new(new LiveMacDiscoveryBackend());
    private readonly ComboBox discoveryNetworks = new();
    private readonly TextBlock discoveryStatus = Label("发现功能准备中，仍可使用身份卡配对。");
    private Button discoveryStart = null!;
    private bool updatingDiscoveryNetworks;
#if FILELESS_PAIRING_CANDIDATE
    private CancellationTokenSource? filelessCancellation;
    private readonly FilelessWorkSlot filelessWork = new();
    private IMacDiscoveryUiSession? filelessOwner;
    private long filelessGeneration;
    private Guid lastFilelessRequest;
    private bool preserveFilelessStatus;
    private bool filelessDeliveryUnknown, filelessDeliveryCompleted, filelessPeerAbsent;
    private readonly Button filelessCancel = new() { Content = "取消安全配对", IsVisible = false };
#endif
#endif

    public MacMultiDeviceWindow(string root, bool startupCheck, IIdentityVault identityVault)
    {
        ArgumentNullException.ThrowIfNull(identityVault);
        this.root = root; this.startupCheck = startupCheck;
        var userCore = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Deskflow.app", "Contents", "MacOS", "deskflow-core");
        engine = File.Exists(userCore) ? userCore : "/Applications/Deskflow.app/Contents/MacOS/deskflow-core";
#if FILELESS_PAIRING_CANDIDATE
        Title = "年年互联·免文件测试";
#elif DISCOVERY_CANDIDATE
        Title = "年年互联·发现测试";
#else
        Title = "年年互联·多设备测试";
#endif
        Width = 980; Height = 760; MinWidth = 700; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MacSettingsPalette.Apply(this, true);
        // This candidate owns its accent resources; the installed Mac window keeps its own palette.
        ApplyBlueAccent(this);
        Background = new SolidColorBrush(Color.Parse("#F6F9FF"));
        Content = BuildBlueLayout();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty) UpdateViewerVisibility();
        };

        Opened += async (_, _) =>
        {
            if (startupCheck) { await Task.Delay(500); allowClose = true; Close(); return; }
            await RunAsync(async () =>
            {
                client = await MacMultiPeerClient.OpenAsync(root, engine, identityVault);
                identity.Text = "本机名称：" + client.DeviceName;
                feedback.Text = "已准备。生成身份卡后发给主控电脑。";
                timer.Start();
            });
        };
        timer.Tick += async (_, _) =>
        {
#if DISCOVERY_CANDIDATE
            try
            {
                await discovery.ObserveCompletionAsync(); UpdateDiscoveryUi();
#if FILELESS_PAIRING_CANDIDATE
                if (filelessOwner is not null && !discovery.IsCurrentFilelessSession(filelessOwner))
                    await StopMacFilelessPairingAsync();
                else ObserveMacFilelessPairingRequest();
#endif
            }
            catch { discoveryStatus.Text = "无法停止发现，请关闭测试版。"; }
#endif
            await PollAsync();
        };
        Closing += async (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            if (busy || closing) return;
            closing = true; IsEnabled = false; timer.Stop();
#if MULTI_DEVICE_CANDIDATE
            await collaborationLifecycle.WaitAsync();
#endif
            try {
#if NIAN_MAC_RETURN_RESEARCH
                if(remoteReturn is not null && !await ConfirmResearchExitAsync())
                { closing=false;IsEnabled=true;timer.Start();return; }
#endif
#if DISCOVERY_CANDIDATE
#if FILELESS_PAIRING_CANDIDATE
                await StopMacFilelessPairingAsync();
#endif
#if MULTI_DEVICE_CANDIDATE
                await discovery.StopAsync();
#else
                await discovery.DisposeAsync();
#endif
#endif
#if MULTI_DEVICE_CANDIDATE
                client?.BeginControlStop();
#endif
                await StopCollaborationAsync();
#if NIAN_MAC_RETURN_RESEARCH
                InvalidateResearchConnection();
                if(client is not null) { await client.StopAsync(); await client.DisposeAsync(); client=null; }
                if(!TryReleaseResearchReturn(false)) throw new InvalidOperationException("研究入口未能关闭。");
#else
                if (client is not null) { await client.StopAsync(); await client.DisposeAsync(); client = null; }
#endif
#if DISCOVERY_CANDIDATE && MULTI_DEVICE_CANDIDATE
                await discovery.DisposeAsync();
#endif
                allowClose = true; Close(); }
            catch (Exception error) { closing = false; IsEnabled = true; timer.Start(); feedback.Text = "退出前无法安全停止连接：" + error.Message;
#if NIAN_MAC_RETURN_RESEARCH
                if(researchEntryStopped)remoteReturn?.SetResearchDismissAvailable(true);
#endif
            }
#if MULTI_DEVICE_CANDIDATE
            finally { collaborationLifecycle.Release(); }
#endif
        };
    }

#if DISCOVERY_CANDIDATE
    private Control DiscoveryPanel()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(TitleText("局域网发现（测试）"));
        panel.Children.Add(Label("同意后，局域网内设备可在最多 30 秒内看到本机名称、系统和完整键鼠指纹。发现不等于配对或允许控制。"));
        panel.Children.Add(discoveryNetworks);
        discoveryNetworks.SelectionChanged += async (_, _) =>
        {
            if (updatingDiscoveryNetworks) return;
            try {
#if FILELESS_PAIRING_CANDIDATE
                await StopMacFilelessPairingAsync();
#endif
                await discovery.StopAsync(); UpdateDiscoveryUi(); }
            catch { discoveryStatus.Text = "无法停止发现，请关闭测试版。"; }
        };
        var refresh = new Button { Content = "刷新网络" };
        refresh.Click += async (_, _) =>
        {
            updatingDiscoveryNetworks = true;
            try { discoveryNetworks.ItemsSource = null; discoveryNetworks.SelectedItem = null; discoveryStart.IsEnabled = false; }
            finally { updatingDiscoveryNetworks = false; }
            try
            {
#if FILELESS_PAIRING_CANDIDATE
                await StopMacFilelessPairingAsync();
#endif
                await discovery.RefreshAsync();
                updatingDiscoveryNetworks = true;
                try { discoveryNetworks.ItemsSource = discovery.Networks; discoveryNetworks.SelectedItem = null; }
                finally { updatingDiscoveryNetworks = false; }
                UpdateDiscoveryUi();
            }
            catch { discoveryStatus.Text = "无法读取网络，请稍后重试。"; }
        };
        discoveryStart = new Button { Content = "允许被发现 30 秒", IsEnabled = false };
        discoveryStart.Click += async (_, _) =>
        {
            discoveryStart.IsEnabled = false;
            try
            {
#if FILELESS_PAIRING_CANDIDATE
                await StopMacFilelessPairingAsync();
#endif
                var selected = discoveryNetworks.SelectedItem as IPAddress;
                PeerIdentityOffer? advertised = null;
                var started = await discovery.StartAsync(selected, async () =>
                {
                    advertised = await (client ?? throw new InvalidOperationException("请等待本机身份准备完成。"))
                        .CreateIdentityAsync(DateTimeOffset.UtcNow);
                    return advertised;
                }, ConfirmDiscoveryAsync);
                if (started && advertised is not null)
                    identity.Text = $"本机名称：{advertised.Name}\n键鼠指纹：\n{advertised.KeyboardFingerprint}\n有效期至：{advertised.ExpiresAtUtc.ToLocalTime():HH:mm:ss}";
                UpdateDiscoveryUi();
            }
            catch (Exception error) { discoveryStatus.Text = "无法开启发现：" + error.Message; }
            finally { UpdateDiscoveryUi(); }
        };
        var stop = new Button { Content = "停止被发现" };
        stop.Click += async (_, _) =>
        {
            try {
#if FILELESS_PAIRING_CANDIDATE
                await StopMacFilelessPairingAsync();
#endif
                await discovery.StopAsync(); UpdateDiscoveryUi(); }
            catch { discoveryStatus.Text = "无法停止发现，请关闭测试版。"; }
        };
#if FILELESS_PAIRING_CANDIDATE
        filelessCancel.Click += async (_, _) =>
        {
            try { await StopMacFilelessPairingAsync(); discoveryStatus.Text = filelessDeliveryUnknown
                    ? "配对结果待确认，请查看未完成的配对。" : filelessDeliveryCompleted
                    ? "设备已配对，请开始连接。" : filelessPeerAbsent
                    ? "本机未收到上次配对，请在主控查看最终状态。" : "安全配对已取消。"; preserveFilelessStatus = true; }
            catch { discoveryStatus.Text = "安全配对无法完整停止，请关闭测试版。"; }
        };
        panel.Children.Add(WrapActions(refresh, discoveryStart, stop, filelessCancel));
#else
        panel.Children.Add(WrapActions(refresh, discoveryStart, stop));
#endif
        panel.Children.Add(discoveryStatus);
        return panel;
    }

    private void UpdateDiscoveryUi()
    {
#if FILELESS_PAIRING_CANDIDATE
        if (filelessWork.Current is null && !preserveFilelessStatus && !filelessWork.IsPoisoned) discoveryStatus.Text = discovery.Status;
#else
        discoveryStatus.Text = discovery.Status;
#endif
        discoveryStart.IsEnabled = discoveryNetworks.SelectedItem is IPAddress && discovery.CanStart
#if FILELESS_PAIRING_CANDIDATE
            && filelessWork.Current is null && !filelessWork.IsPoisoned
#endif
            ;
    }

    private async Task<bool> ConfirmDiscoveryAsync(IPAddress network, PeerIdentityOffer offer)
    {
        var candidate = CreateCandidateDialog("公开身份确认", $"允许局域网设备在最多 30 秒内看到以下信息？\n网络：{network}\n设备：{offer.Name} · Mac\n完整键鼠指纹：\n{offer.KeyboardFingerprint}\n发现不等于配对，也不会自动授权控制。", "同意，开启 30 秒");
        candidate.Accept.Click += (_, _) => candidate.Window.Close(true);
        candidate.Cancel.Click += (_, _) => candidate.Window.Close(false);
        return await candidate.Window.ShowDialog<bool>(this);
    }

#if FILELESS_PAIRING_CANDIDATE
    private void ObserveMacFilelessPairingRequest()
    {
        if (busy || filelessWork.Current is not null || filelessWork.IsPoisoned || closing) return;
        var owner = discovery.CurrentFilelessSession;
        var request = owner?.GetPendingPairingRequest();
        if (owner is null || request is null || request.Id == lastFilelessRequest) return;
        lastFilelessRequest = request.Id;
        var generation = ++filelessGeneration;
        filelessDeliveryUnknown = filelessDeliveryCompleted = filelessPeerAbsent = false;
        var cancellation = new CancellationTokenSource();
        filelessCancellation = cancellation; filelessOwner = owner;
        if (!filelessWork.TryStart(() => HandleMacFilelessPairingAsync(owner, request, generation, cancellation)))
        { filelessCancellation = null; filelessOwner = null; cancellation.Dispose(); }
    }

    private async Task ReviewMacPairingRecoveryAsync()
    {
        if (filelessWork.Current is not null || filelessWork.IsPoisoned)
            throw new InvalidOperationException("请先停止正在进行的安全配对；如无法停止，请关闭测试版。");
        var current = client ?? throw new InvalidOperationException("本机身份尚未准备好。");
        var reviewed = await current.GetPairingRecoveryStatusAsync();
        if (reviewed.Phase == PairingRecoveryPhase.Unsent)
        {
            var confirmed = await AskFilelessAsync("未完成的配对",
                $"与 {reviewed.PeerName} 的配对尚未发送确认。放弃这次未发送的配对？\n\n已有配对不会被移除。",
                "放弃这次配对", CancellationToken.None);
            if (!confirmed) { feedback.Text = "保留未发送的配对。"; return; }
            feedback.Text = await current.AbandonUnsentPairingAsync(reviewed)
                ? "已放弃这次未发送的配对。"
                : "配对状态已变化，请重新查看。";
            await ShowMacRecoveryInfoAsync(feedback.Text);
            return;
        }
        feedback.Text = reviewed.Phase switch
        {
            PairingRecoveryPhase.None => "当前没有未完成的配对。",
            PairingRecoveryPhase.AwaitingPeer or PairingRecoveryPhase.CommittedAwaitingPeer =>
                "上次配对的结果待确认。恢复开放后，请在主控重新发现并选择本机，双方再次核对指纹。",
            PairingRecoveryPhase.CompletedAwaitingHost =>
                "本机已保存配对记录，正在等待主控完成确认。恢复开放后，请在主控重新发现并选择本机，双方再次核对指纹。",
            PairingRecoveryPhase.EvidenceProblem => "上次配对资料需要检查。请保留现有资料。",
            _ => "配对状态需要重新检查。"
        };
        await ShowMacRecoveryInfoAsync(feedback.Text);
    }

    private async Task ShowMacRecoveryInfoAsync(string message)
    {
        var candidate = CreateCandidateDialog("未完成的配对", message, "知道了");
        candidate.Cancel.IsVisible = false;
        candidate.Accept.Click += (_, _) => candidate.Window.Close();
        await candidate.Window.ShowDialog(this);
    }

    private async Task HandleMacFilelessPairingAsync(IMacDiscoveryUiSession owner, PairingIncomingRequest request,
        long generation, CancellationTokenSource cancellation)
    {
        SecurePairingPeer? session = null;
        Task? remote = null;
        try
        {
            var remaining = request.ExpiresAtUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) { owner.RejectPairingRequest(request.Id); return; }
            using var requestPrompt = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            requestPrompt.CancelAfter(remaining);
            var proceed = await AskFilelessAsync("安全配对请求",
                $"收到来自 {request.HostName} 的安全配对请求。该请求尚未验证。\n\n" +
                $"对方声明的控制通道指纹：\n{request.HostFingerprint}\n\n" +
                "允许继续后仍需双方核对本次连接的指纹；此时不会授权控制。", "继续核对", requestPrompt.Token);
            if (!proceed || generation != filelessGeneration || !discovery.IsCurrentFilelessSession(owner))
            { owner.RejectPairingRequest(request.Id); return; }

            // The accepted request takes an independent network observation. Natural discovery expiry is not cancellation.
            filelessOwner = null;
            filelessCancel.IsVisible = true;
            discoveryStatus.Text = "正在建立安全连接…";
            session = await SecurePairingPeer.AcceptAsync(request, Path.Combine(root, "keyboard"), cancellation.Token);
            if (generation != filelessGeneration || cancellation.IsCancellationRequested) return;
            discoveryStatus.Text = "连接已建立，请核对双方指纹。";
            using var confirmation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, session.LifetimeToken);
            remote = session.WaitForRemoteConfirmationAsync(confirmation.Token);
            var confirmed = await AskFilelessAsync("核对本次安全连接",
                $"请逐字核对主控上显示的控制通道指纹：\n\n{session.RemoteFingerprint}\n\n" +
                $"请让主控核对本机键鼠指纹：\n\n{session.LocalFingerprint}\n\n" +
                "两边一致后再确认。双方确认后将进行新配对或恢复未完成的配对；成功配对后允许主控连接并控制本机。", "一致，确认", confirmation.Token);
            if (!confirmed || generation != filelessGeneration || confirmation.IsCancellationRequested) return;
            await session.ConfirmAsync(confirmation.Token);
            discoveryStatus.Text = "本机已确认，正在等待主控确认…";
            await remote;
            if (!session.BothConfirmed) throw new InvalidOperationException("尚未收到双方确认。");
            if (generation != filelessGeneration || cancellation.IsCancellationRequested) return;
            FilelessPairingAvailability.EnsureCompletionAvailable();
            var currentClient = client ?? throw new InvalidOperationException("请等待本机身份准备完成。");
            try
            {
                discoveryStatus.Text = "双方已确认，正在完成配对…";
                preview = null;
                pairing.Text = "先前的配对文件审核已清除。";
                var completed = await currentClient.AcceptFilelessPairingAsync(session, confirmation.Token);
                if (!completed)
                {
                    filelessPeerAbsent = true;
                    feedback.Text = "本机未收到上次配对，请在主控查看最终状态。";
                    if (generation == filelessGeneration) discoveryStatus.Text = feedback.Text;
                    preserveFilelessStatus = true;
                    return;
                }
                filelessDeliveryCompleted = true;
                feedback.Text = "配对已保存。现在可以开始连接。";
                if (generation == filelessGeneration) discoveryStatus.Text = feedback.Text;
                preserveFilelessStatus = true;
            }
            catch
            {
                filelessDeliveryUnknown = true;
                feedback.Text = "配对结果待确认，请查看未完成的配对。";
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == filelessGeneration && !cancellation.IsCancellationRequested)
            {
                discoveryStatus.Text = filelessDeliveryUnknown ? feedback.Text : error is NotSupportedException
                    ? "免文件配对尚未开放。" : error is PairingStartupCleanupException
                    ? "安全配对无法完整停止，请关闭测试版。"
                    : "安全配对未完成：" + error.Message;
                preserveFilelessStatus = true;
            }
            FilelessWorkSlot.ThrowIfStartupCleanupFailed(error);
        }
        finally
        {
            Exception? cleanup = null;
            try { if (session is not null) await session.DisposeAsync(); }
            catch (Exception error) { cleanup = error; }
            if (remote is not null) try { await remote; } catch { /* The session has already stopped. */ }
            if (generation == filelessGeneration)
            {
                filelessOwner = null; filelessCancellation = null;
                filelessCancel.IsVisible = false;
                if (cleanup is not null) discoveryStatus.Text = "安全配对无法完整停止，请关闭测试版。";
            }
            cancellation.Dispose();
            if (cleanup is not null)
            {
                try { await discovery.StopAsync(); } catch { /* Keep the cleanup failure visible. */ }
                discoveryStart.IsEnabled = false;
                throw new InvalidOperationException("安全配对清理失败。", cleanup);
            }
        }
    }

    private async Task StopMacFilelessPairingAsync()
    {
        var generation = ++filelessGeneration;
        var cancellation = filelessCancellation;
        var task = filelessWork.Current;
        cancellation?.Cancel();
        if (task is not null) await task;
        if (generation == filelessGeneration)
        { filelessOwner = null; filelessCancellation = null; lastFilelessRequest = Guid.Empty; preserveFilelessStatus = false; filelessCancel.IsVisible = false; }
    }

    private async Task<bool> AskFilelessAsync(string title, string message, string acceptLabel, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var candidate = CreateCandidateDialog(title, message, acceptLabel);
        var dialog = candidate.Window;
        candidate.Accept.Click += (_, _) => dialog.Close(true); candidate.Cancel.Click += (_, _) => dialog.Close(false);
        using var registration = cancellation.Register(() => Dispatcher.UIThread.Post(() =>
        { if (dialog.IsVisible) dialog.Close(false); }));
        var result = dialog.ShowDialog<bool>(this);
        if (cancellation.IsCancellationRequested && dialog.IsVisible) dialog.Close(false);
        return await result;
    }
#endif
#endif

    private async Task CreateIdentityAsync()
    {
        var current = client ?? throw new InvalidOperationException("本机身份尚未准备好。");
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "保存身份卡", SuggestedFileName = "年年互联设备身份.json", DefaultExtension = "json" });
        if (destination is null) return;
        var now = DateTimeOffset.UtcNow;
        var offer = await current.CreateIdentityAsync(now);
        var bytes = Encoding.UTF8.GetBytes(offer.ToJson(now));
        await using var output = await destination.OpenWriteAsync();
        if (output.CanSeek) output.SetLength(0);
        await output.WriteAsync(bytes);
        identity.Text = $"本机名称：{offer.Name}\n键鼠指纹：\n{offer.KeyboardFingerprint}\n有效期至：{offer.ExpiresAtUtc.ToLocalTime():HH:mm:ss}";
        feedback.Text = "身份卡已保存，请私下发给主控电脑。";
    }

    private async Task ImportAsync()
    {
        var current = client ?? throw new InvalidOperationException("本机身份尚未准备好。");
        preview = null; pairing.Text = "还没有导入配对文件。";
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择主控配对文件", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("配对文件") { Patterns = ["*.json"] }, FilePickerFileTypes.All] });
        if (files.Count == 0) return;
        await using var input = await files[0].OpenReadAsync(); using var buffer = new MemoryStream();
        var block = new byte[1024]; int count;
        while ((count = await input.ReadAsync(block)) > 0) { if (buffer.Length + count > 4096) throw new InvalidDataException("配对文件过大。"); await buffer.WriteAsync(block.AsMemory(0, count)); }
        preview = await current.ReviewInvitationAsync(new UTF8Encoding(false, true).GetString(buffer.ToArray()));
        pairing.Text = $"主控：{preview.HostName} · {preview.Host}\n控制通道指纹：\n{preview.ControlFingerprint}";
        feedback.Text = "请与主控逐字核对完整控制通道指纹。";
    }

    private async Task ConfirmAsync()
    {
        var current = client ?? throw new InvalidOperationException("本机身份尚未准备好。");
        var candidate = preview ?? throw new InvalidOperationException("请先导入主控配对文件。");
        if (!await AskAsync("核对主控", "请与主控逐字核对：\n\n" + candidate.ControlFingerprint + "\n\n完全一致后再确认。")) return;
        preview = null; await current.ConfirmInvitationAsync(candidate.Id, candidate.ControlFingerprint);
        feedback.Text = "主控配对已保存。";
    }

    private async Task StartAsync()
    {
#if MULTI_DEVICE_CANDIDATE
        await collaborationLifecycle.WaitAsync();
        try{await StartInsideAsync();}finally{collaborationLifecycle.Release();}
#else
        await StartInsideAsync();
#endif
    }
    private async Task StartInsideAsync()
    {
#if MULTI_DEVICE_CANDIDATE
        await DisposePendingCollaborationAsync();
#endif
        if (!File.Exists(engine)) throw new FileNotFoundException("没有找到 Deskflow 键鼠核心，请先安装 Mac 端 Deskflow。", engine);
        var current = client ?? throw new InvalidOperationException("本机身份尚未准备好。");
#if NIAN_MAC_RETURN_RESEARCH
        if(current.PreviewControlUnresolved)throw new InvalidOperationException("旧键鼠操作尚未确认，请检查设备。");
        if(researchConnectionActive)throw new InvalidOperationException("当前连接尚未停止。");
        if(researchEntryStopped && remoteReturn is not null)throw new InvalidOperationException("键鼠状态仍未确认，请检查设备后自行关闭提示，再重新连接。");
        EnsureResearchReturnWindow();
        BeginResearchConnection();
        try { await current.StartAsync(); }
        catch { InvalidateResearchConnection(); throw; }
#else
        await current.StartAsync();
#endif
        MacCollaborationService? next = null;
        try
        {
            var scope = await current.GetCollaborationScopeAsync();
            var control=await current.GetPreviewControlAsync();
#if MULTI_DEVICE_CANDIDATE
            next=(collaborationFactory??MacCollaborationService.CreateForMultiDevice)(Path.Combine(root,"collaboration",scope),current.GetApprovedCollaborationPairingAsync,control);
            pendingCollaboration=next;
#else
            next=MacCollaborationService.CreateForMultiDevice(Path.Combine(root,"collaboration",scope),current.GetApprovedCollaborationPairingAsync,control);
#endif
            var epoch = ++collaborationEpoch;
            collaborationObserver = snapshot => OnCollaborationChanged(epoch, snapshot);
            next.Changed += collaborationObserver;
            await next.StartAsync(); collaboration = next;
#if MULTI_DEVICE_CANDIDATE
            pendingCollaboration=null;
#endif
            OnCollaborationChanged(epoch, next.Snapshot);
            UpdateViewerVisibility();
            clipboardChoice.IsChecked = next.Snapshot.Settings.ClipboardEnabled;
            previewChoice.IsChecked = next.Snapshot.Settings.PreviewEnabled;
            shareScreenChoice.IsChecked = next.Snapshot.Settings.ShareScreenEnabled;
            updatingRefreshChoice = true;
            try { refreshChoice.SelectedIndex = Math.Max(0, Array.IndexOf(RefreshIntervals, next.Snapshot.Settings.PreviewRefreshSeconds)); }
            finally { updatingRefreshChoice = false; }
            refreshChoice.IsEnabled = true;
        }
#if MULTI_DEVICE_CANDIDATE
        catch(Exception startFailure)
#else
        catch
#endif
        {
            collaborationEpoch++;
#if MULTI_DEVICE_CANDIDATE
            try
            {
                if(next is not null)
                {
                    if(collaborationObserver is not null)next.Changed-=collaborationObserver;
                    await next.DisposeAsync();
                    if(ReferenceEquals(pendingCollaboration,next))pendingCollaboration=null;
                    if(ReferenceEquals(collaboration,next))collaboration=null;
                }
                collaborationObserver=null;
            }
            catch(Exception cleanupFailure)
            {
                if(next is not null&&!ReferenceEquals(collaboration,next))pendingCollaboration=next;
                throw new InvalidOperationException("连接启动失败，协作清理尚未确认，请检查主控键鼠。",new AggregateException(startFailure,cleanupFailure));
            }
            try { await current.StopAsync(); }
            catch(Exception stopFailure)
            {
                throw new InvalidOperationException("连接启动失败，键鼠停止尚未确认，请检查主控键鼠并重试停止。",new AggregateException(startFailure,stopFailure));
            }
#if NIAN_MAC_RETURN_RESEARCH
            finally { InvalidateResearchConnection(); }
#endif
#else
            if(next is not null)
            {
                if(collaborationObserver is not null)next.Changed-=collaborationObserver;
                await next.DisposeAsync();
            }
            collaborationObserver=null;await current.StopAsync();
#endif
            throw;
        }
        feedback.Text = "正在连接；首次连接需要核对键鼠指纹。";
    }
    private async Task ConfirmKeyboardAsync()
    {
        var current = client ?? throw new InvalidOperationException("本机身份尚未准备好。");
        var currentState = await current.GetStateAsync();
        var fingerprint = currentState.PendingPeerFingerprint ?? throw new InvalidOperationException("当前没有待确认的键鼠指纹。");
        if (await AskAsync("核对键鼠", "请与主控显示的键鼠指纹逐字核对：\n\n" + fingerprint + "\n\n一致后才能连接。"))
        { await current.ConfirmKeyboardAsync(fingerprint); feedback.Text = "键鼠身份已确认。"; }
    }
    private async Task SwitchAsync(bool mac)
    {
        var result = await (client ?? throw new InvalidOperationException("请先连接。")).SwitchDetailedAsync(mac);
        if (!result.InputTransferred) throw new InvalidOperationException("主控未确认切换，请检查设备连接。");
        feedback.Text = result.VideoStatus switch { "verified" => "键鼠和画面已切换。", "acceptedUnverified" => "键鼠已切换，显示器已收到命令，请确认画面。", "failed" => "键鼠已切换，显示器切换失败，请检查画面。", _ => mac ? "键鼠已切到 Mac。" : "键鼠已归还主控。" };
    }
    private async Task StopAsync()
    {
#if MULTI_DEVICE_CANDIDATE
        await collaborationLifecycle.WaitAsync();
        try{await StopInsideAsync();}finally{collaborationLifecycle.Release();}
#else
        await StopInsideAsync();
#endif
    }
    private async Task StopInsideAsync()
    {
#if NIAN_MAC_RETURN_RESEARCH
        researchStopInProgress=true;
        try
        {
#endif
#if MULTI_DEVICE_CANDIDATE
        client?.BeginControlStop();
#endif
        await StopCollaborationAsync();
#if NIAN_MAC_RETURN_RESEARCH
        InvalidateResearchConnection();
        if (client is not null) await client.StopAsync();
#else
        if (client is not null) await client.StopAsync();
#endif
#if NIAN_MAC_RETURN_RESEARCH
        feedback.Text = "连接已停止，键鼠归还尚未确认，请检查键鼠所在设备。";
#elif MULTI_DEVICE_CANDIDATE
        feedback.Text = client?.PreviewControlUnconfirmed == true ? "连接已停止，键鼠归还尚未确认，请检查键鼠所在设备。" : "连接已停止。";
#else
        feedback.Text = "连接已停止。";
#endif
#if NIAN_MAC_RETURN_RESEARCH
        }
        finally { researchStopInProgress=false;if(researchEntryStopped)remoteReturn?.SetResearchDismissAvailable(!closing); }
#endif
    }

    private async Task StopCollaborationAsync()
    {
#if MULTI_DEVICE_CANDIDATE
        await DisposePendingCollaborationAsync();
#endif
        var current = collaboration;
        if (current is null) return;
        var mini = miniWindow; miniWindow = null; miniLink = null; mini?.Close();
        miniViewerVisible = miniDetachedViewerVisible = false;
        CloseRemoteReturn();
        detachedPreview?.Close(); detachedPreview = null;
        current.SetViewerVisibility(false, false);
        sharedDownloadCancellation?.Cancel();
        if (sharedDownloadTask is not null) await sharedDownloadTask;
        collaborationEpoch++;
        if (collaborationObserver is not null) current.Changed -= collaborationObserver;
        await current.DisposeAsync(); collaboration = null;
        collaborationObserver = null; clipboardChoice.IsChecked = false; previewChoice.IsChecked = false; shareScreenChoice.IsChecked = false; refreshChoice.IsEnabled = false;
        ClearPreview();
    }

#if MULTI_DEVICE_CANDIDATE
    private async Task DisposePendingCollaborationAsync()
    {
        var pending=pendingCollaboration;if(pending is null)return;
        try{await pending.DisposeAsync();}
        catch(Exception error){throw new InvalidOperationException("协作清理尚未确认，不能重新连接或停止键鼠；请检查主控。",error);}
        if(ReferenceEquals(pendingCollaboration,pending))pendingCollaboration=null;
    }
#endif

    private void OnCollaborationChanged(long epoch, DesktopToolbox.Mac.Models.MacCollaborationSnapshot snapshot) => Dispatcher.UIThread.Post(() =>
    {
        if (closing || epoch != collaborationEpoch || collaboration is not { } current) return;
        // Queued notifications can arrive late; use one current snapshot for every displayed value.
        var latest = current.Snapshot;
        previewChoice.IsChecked = latest.Settings.PreviewEnabled;
        shareScreenChoice.IsChecked = latest.Settings.ShareScreenEnabled;
        if (detachedPreview is { } detached)
        {
            detached.SetPassiveContext(latest.PassivePreviewGenerationSupported, latest.PassivePreviewRequestId, latest.RemotePreviewSourceStatus);
            detached.SetConnectionState(current.PreviewControlReady, current.PreviewControlHint);
            detached.ShowFrame(latest.Settings.PreviewEnabled ? latest.Preview : DesktopToolbox.DeskLink.Collaboration.PreviewFrame.Off(), latest.Settings.PreviewRefreshSeconds);
        }
        fileStatus.Text = latest.Error ?? latest.ReceiveError ?? latest.ClipboardError ?? latest.Status;
        if (latest.Transfer is { } transfer) fileStatus.Text += $"\n{transfer.Name} · {transfer.State}";
        var frame = latest.Preview;
        if (!latest.Settings.PreviewEnabled || frame.State != "live" || frame.JpegBytes.Length == 0) { ClearPreview(); return; }
        if (frame.CapturedAtUtc == lastPreviewTimestamp) return;
        try
        {
            using var bytes = new MemoryStream(frame.JpegBytes);
            var next = new Bitmap(bytes);
            var old = previewImage.Source as IDisposable;
            previewImage.Source = next; previewPlaceholder.IsVisible = false; lastPreviewTimestamp = frame.CapturedAtUtc; old?.Dispose();
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or NotSupportedException) { ClearPreview(); }
    });

    private async Task OpenMiniAsync()
    {
        if (miniWindow is { } existing) { existing.Show(); existing.Activate(); return; }
        var current = collaboration ?? throw new InvalidOperationException("请先开始连接。");
        var peer = client ?? throw new InvalidOperationException("本机身份尚未准备好。");
        var pairing = await peer.GetApprovedCollaborationPairingAsync() ?? throw new InvalidOperationException("请先确认主控配对。");
        detachedPreview?.Close(); detachedPreview = null;
        var link = new MacMultiMiniLink(pairing.WindowsName, pairing.MacName,
            _ => RunMiniAsync(StopAsync), mac => RunMiniAsync(() => SwitchAsync(mac)));
        link.Update(await peer.GetStateAsync(), current.Snapshot.IsOnline, IsControlUnconfirmed());
        MacMultiMiniWindow? window = null;
        window = new MacMultiMiniWindow(current, link, () => { Show(); Activate(); }, (visible, detached) =>
        {
            if (!ReferenceEquals(miniWindow, window) || !ReferenceEquals(collaboration, current)) return;
            UpdateMiniViewerVisibility(visible, detached);
        });
        miniLink = link; miniWindow = window;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(miniWindow, window)) return;
            miniWindow = null; miniLink = null;
            if (!closing && ReferenceEquals(collaboration, current)) UpdateMiniViewerVisibility(false, false);
        };
        window.Show();
    }

    private Task OpenPreviewAsync()
    {
        var current = collaboration ?? throw new InvalidOperationException("请先连接主控。");
        if (!current.Snapshot.Settings.PreviewEnabled) throw new InvalidOperationException("请先开启画面预览。");
        if (miniWindow is not null) throw new InvalidOperationException("请使用互联小窗打开预览，或先关闭小窗。");
        if (detachedPreview is not null) { detachedPreview.Activate(); return Task.CompletedTask; }
        var session = current.CreateDeskflowPreviewSession() ?? throw new InvalidOperationException("预览操作尚未就绪。");
        var window = new MacRemotePreviewWindow(session, "Mac", "Windows", candidateBlue: true);
        detachedPreview = window;
        var snapshot = current.Snapshot;
        window.SetAppearance(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
        window.SetPassiveContext(snapshot.PassivePreviewGenerationSupported, snapshot.PassivePreviewRequestId, snapshot.RemotePreviewSourceStatus);
        window.SetConnectionState(current.PreviewControlReady, current.PreviewControlHint);
        window.Closed += (_, _) => { if (ReferenceEquals(detachedPreview, window)) { detachedPreview = null; UpdateViewerVisibility(); } };
        window.PropertyChanged += (_, e) =>
        { if (ReferenceEquals(detachedPreview, window) && (e.Property == IsVisibleProperty || e.Property == WindowStateProperty)) UpdateViewerVisibility(); };
        window.Show(); window.ShowFrame(snapshot.Preview, snapshot.Settings.PreviewRefreshSeconds);
        UpdateViewerVisibility();
        return Task.CompletedTask;
    }

    private void UpdateMiniViewerVisibility(bool visible, bool detached)
    {
        miniViewerVisible = visible; miniDetachedViewerVisible = detached;
        UpdateViewerVisibility();
    }

    private void UpdateViewerVisibility()
    {
        if (collaboration is not { } current || closing) return;
        var main = selectedPage == "画面" && IsVisible && WindowState != WindowState.Minimized;
        var detached = detachedPreview?.IsVisible == true && detachedPreview.WindowState != WindowState.Minimized;
        current.SetViewerVisibility(main || miniViewerVisible, detached || miniDetachedViewerVisible);
    }

    private void ClearPreview()
    {
        var old = previewImage.Source as IDisposable; previewImage.Source = null; previewPlaceholder.IsVisible = true;
        lastPreviewTimestamp = default; old?.Dispose();
    }

    private async Task ChooseReceiveAsync()
    {
        var current = collaboration ?? throw new InvalidOperationException("请先开始连接。");
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择 Mac 收件文件夹", AllowMultiple = false });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath() ?? throw new IOException("请选择本机文件夹。");
        await current.SetReceiveDirectoryAsync(path); fileStatus.Text = "收件位置：" + path;
    }
    private async Task ChooseFilesAsync()
    {
        var current = collaboration ?? throw new InvalidOperationException("请先开始连接。");
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择要发送的文件", AllowMultiple = true });
        var paths = files.Select(x => x.TryGetLocalPath()).Where(x => x is not null).Cast<string>().ToArray();
        if (paths.Length == 0) return;
        _ = SendInBackgroundAsync(current, paths);
    }
    private async Task SendInBackgroundAsync(MacCollaborationService current, string[] paths)
    {
        try { await current.SendFilesAsync(paths); }
        catch (Exception error) { Dispatcher.UIThread.Post(() => { if (!closing) fileStatus.Text = "发送未完成：" + error.Message; }); }
    }
    private Task RetrySendAsync()
    {
        var current = collaboration ?? throw new InvalidOperationException("请先开始连接。");
        if (!current.CanRetrySending) throw new InvalidOperationException("没有可继续的发送任务。");
        _ = RetryInBackgroundAsync(current);
        return Task.CompletedTask;
    }
    private async Task RetryInBackgroundAsync(MacCollaborationService current)
    {
        try { await current.RetrySendAsync(); }
        catch (Exception error) { Dispatcher.UIThread.Post(() => { if (!closing) fileStatus.Text = "发送未完成：" + error.Message; }); }
    }
    private async Task ShowSharedAsync() { sharedRoot = null; sharedRelative = ""; await RefreshSharedAsync(); }
    private async Task SharedBackAsync()
    {
        if (sharedRoot is null) return;
        if (sharedRelative.Length == 0) sharedRoot = null;
        else { var position = sharedRelative.LastIndexOf('/'); sharedRelative = position < 0 ? "" : sharedRelative[..position]; }
        await RefreshSharedAsync();
    }
    private async Task RefreshSharedAsync()
    {
        var current = collaboration ?? throw new InvalidOperationException("请先开始连接。");
        var listing = await current.ListSharedAsync(sharedRoot, sharedRelative);
        sharedFiles.Items.Clear();
        foreach (var item in listing.Items) sharedFiles.Items.Add(new ListBoxItem { Content = item.Name, Tag = item });
        fileStatus.Text = listing.Items.Count == 0 ? "此位置没有可显示的项目。" : "双击文件夹打开，双击文件预览。";
    }
    private async Task OpenSharedAsync()
    {
        if (sharedFiles.SelectedItem is not ListBoxItem { Tag: SharedItem item }) return;
        var current = collaboration ?? throw new InvalidOperationException("请先开始连接。");
        if (item.IsDirectory) { sharedRoot = item.RootId; sharedRelative = item.RelativePath; await RefreshSharedAsync(); return; }
        var result = await current.PreviewSharedAsync(item);
        Control content;
        if (result.ContentType == "text/plain") content = new ScrollViewer { Content = new SelectableTextBlock { Text = Encoding.UTF8.GetString(result.Bytes), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18) } };
        else if (result.ContentType.StartsWith("image/", StringComparison.Ordinal))
        { using var stream = new MemoryStream(result.Bytes); content = new Image { Source = new Bitmap(stream), Stretch = Stretch.Uniform }; }
        else throw new NotSupportedException("此格式请先保存到 Mac，再用对应软件打开。");
        var dialog = new Window { Title = result.Name, Width = 700, Height = 500, Content = content, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        MacSettingsPalette.Apply(dialog, true); await dialog.ShowDialog(this);
    }
    private Task DownloadSharedAsync()
    {
        if (sharedFiles.SelectedItem is not ListBoxItem { Tag: SharedItem item }) throw new InvalidOperationException("请选择共享项目。");
        var current = collaboration ?? throw new InvalidOperationException("请先开始连接。");
        if (sharedDownloadTask is { IsCompleted: false }) throw new InvalidOperationException("已有共享项目正在保存。");
        var cancellation = new CancellationTokenSource(); sharedDownloadCancellation = cancellation;
        sharedDownloadTask = DownloadInBackgroundAsync(current, item, cancellation);
        return Task.CompletedTask;
    }
    private async Task DownloadInBackgroundAsync(MacCollaborationService current, SharedItem item, CancellationTokenSource cancellation)
    {
        try { await current.DownloadSharedAsync(item, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Dispatcher.UIThread.Post(() => { if (!closing) fileStatus.Text = "保存已取消。"; }); }
        catch (Exception error) { Dispatcher.UIThread.Post(() => { if (!closing) fileStatus.Text = "保存未完成：" + error.Message; }); }
        finally { if (ReferenceEquals(sharedDownloadCancellation, cancellation)) sharedDownloadCancellation = null; cancellation.Dispose(); }
    }

    private async Task PollAsync()
    {
        if (polling || busy || closing || client is null) return;
        polling = true;
        try
        {
#if NIAN_MAC_RETURN_RESEARCH
            var observedResearchGeneration=researchGeneration;
#endif
            var value = await client.GetStateAsync();
            state.Text = $"连接：{value.ConnectionState}\n" + (value.PendingPeerFingerprint is null ? "" : "待核对键鼠指纹：\n" + value.PendingPeerFingerprint + "\n") + (value.LastError ?? "");
            var unconfirmed = IsControlUnconfirmed();
            if (unconfirmed) state.Text += "\n键鼠归还尚未确认，请检查键鼠所在设备；归还尝试不能确认旧请求已经停止。";
            miniLink?.Update(value, collaboration?.Snapshot.IsOnline == true, unconfirmed);
            if (value.ProcessState == DeskLinkProcessState.Running && value.ConnectionState == DeskLinkConnectionState.Connected && value.PendingPeerFingerprint is null)
            {
                if (unconfirmed) return; // Keep any emergency return window; a read cannot confirm the old request stopped.
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var backend = await client.GetPreviewControlAsync(timeout.Token);
                    var handoff = await backend.ReadPreviewAsync(timeout.Token);
                    if (KeepUnconfirmedReturnWindow()) return;
                    var id = TargetReturnId(handoff, client.DeviceName);
#if NIAN_MAC_RETURN_RESEARCH
                    ApplyResearchObservation(observedResearchGeneration,id,backend);
#endif
                    UpdateRemoteReturn(id, backend);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
                { if (!KeepUnconfirmedReturnWindow()) CloseRemoteReturn(); }
            }
            else if (!KeepUnconfirmedReturnWindow()) CloseRemoteReturn();
        }
        catch (Exception error) { feedback.Text = "状态读取失败：" + error.Message; }
        finally { polling = false; }
    }

    private bool KeepUnconfirmedReturnWindow()
    {
#if MULTI_DEVICE_CANDIDATE
        return client?.PreviewControlUnresolved == true;
#else
        return false;
#endif
    }

    private bool IsControlUnconfirmed()
    {
#if MULTI_DEVICE_CANDIDATE
        return client?.PreviewControlUnconfirmed == true;
#else
        return false;
#endif
    }

    private static string? TargetReturnId(DeskflowPreviewState state, string local) =>
        state.Phase is DeskflowPreviewPhase.Active or DeskflowPreviewPhase.Recovery &&
        !string.IsNullOrWhiteSpace(state.Id) && !string.IsNullOrWhiteSpace(local) && state.TargetComputer == local ? state.Id : null;

    private void UpdateRemoteReturn(string? id, IDeskflowPreviewControl backend)
    {
#if NIAN_MAC_RETURN_RESEARCH
        // A stale poll may never replace or dismiss the connection's research entry.
        return;
#else
        if (remoteReturnClosing) throw new InvalidOperationException("归还窗口正在关闭，请稍候。");
        if (remoteReturn?.SessionId == id)
        {
            // A canceled close may have hidden a still-owned window. A later
            // observation of that same session can make the entry visible again.
            if (remoteReturn is { IsVisible: false } hidden) hidden.Show();
            return;
        }
        CloseRemoteReturn();
        if (remoteReturn is not null) throw new InvalidOperationException("归还窗口尚未关闭，请检查键鼠。");
        if (id is null) return;
        var window = new MacRemotePreviewReturnWindow(id, async session =>
        {
            var state = await backend.EndPreviewAsync(session);
            if (state.HasSession) throw new InvalidOperationException("键鼠归还尚未确认，请重试。");
        }, candidateBlue: true, isUnconfirmed: IsControlUnconfirmed);
        remoteReturn = window;
        var screen = Screens.Primary ?? Screens.ScreenFromWindow(this);
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            window.Position = new PixelPoint(area.Right - (int)(204 * screen.Scaling), area.Y + (int)(30 * screen.Scaling));
        }
        window.Closed += (_, _) => { if (ReferenceEquals(remoteReturn, window)) remoteReturn = null; };
        window.Show();
#endif
    }

#if NIAN_MAC_RETURN_RESEARCH
    private long BeginResearchConnection()
    {
        researchGeneration++;researchConnectionActive=true;researchEntryStopped=false;researchSessionId=null;researchBackend=null;
        remoteReturn?.ResearchBindingChanged(false);
        return researchGeneration;
    }
    private void InvalidateResearchConnection()
    {
        researchGeneration++;researchConnectionActive=false;researchSessionId=null;researchBackend=null;
        PresentResearchStopped();
    }
    private void PresentResearchStopped()
    {
        researchEntryStopped=true;remoteReturn?.PresentResearchStopped();
        remoteReturn?.SetResearchDismissAvailable(!researchStopInProgress && !closing);
    }
    private void DismissResearchReturn(MacRemotePreviewReturnWindow? source)
    {
        if(!ReferenceEquals(remoteReturn,source) || source is null || researchConnectionActive || researchStopInProgress || researchCreating || researchReleasing || busy || closing)return;
        try { if(TryReleaseResearchReturn(false))return; }
        catch { /* Keep ownership and attempt to make the entry reachable again. */ }
        if(!ReferenceEquals(remoteReturn,source))return;
        try { if(!source.IsVisible)source.Show(); }
        catch { feedback.Text="提示窗口无法显示，请检查两台设备上的键鼠；可以确认退出程序后重新打开。"; }
    }
    private async Task<bool> ConfirmResearchExitAsync()
    {
        if(researchExitConfirmation is not null)return await researchExitConfirmation();
        var dialog=CreateCandidateDialog("退出前请检查键鼠","键鼠归还尚未确认。退出后屏幕上的提示会消失；请先检查两台设备。确认退出吗？","确认退出");
        dialog.Accept.Click+=(_,_)=>dialog.Window.Close(true);
        dialog.Cancel.Click+=(_,_)=>dialog.Window.Close(false);
        return await dialog.Window.ShowDialog<bool>(this);
    }
    private void ApplyResearchObservation(long generation,string? id,IDeskflowPreviewControl backend)
    {
        if(!researchConnectionActive || generation!=researchGeneration || string.IsNullOrEmpty(id))return;
        researchSessionId=id;researchBackend=backend;
        remoteReturn?.ResearchBindingChanged(true);
    }
    private async Task<bool> AttemptResearchReturnAsync(MacRemotePreviewReturnWindow? source)
    {
        var id=researchSessionId;var backend=researchBackend;
        if(!ReferenceEquals(remoteReturn,source) || !researchEntryReady || source?.IsVisible!=true ||
           !researchConnectionActive || id is null || backend is null)return false;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        // This attempt cannot prove the remote queue or operating system has drained.
        _=await backend.EndPreviewAsync(id,deadline.Token).WaitAsync(deadline.Token);
        return true;
    }
    private void EnsureResearchReturnWindow()
    {
        if(researchCreating)throw new InvalidOperationException("研究入口正在创建。");
        if(remoteReturn is not null)
        {
            if(!researchEntryReady || !remoteReturn.IsVisible)throw new InvalidOperationException("研究入口不可用，请先安全清理。");
            return;
        }
        researchCreating=true;
        MacRemotePreviewReturnWindow? window=null;
        try
        {
            window=researchWindowFactory?.Invoke() ?? new MacRemotePreviewReturnWindow("", _=>throw new InvalidOperationException("研究入口不发送旧回调。"), candidateBlue:true, persistentResearch:true,researchReturnAttempt:()=>AttemptResearchReturnAsync(window),researchDismissAction:()=>DismissResearchReturn(window));
            if(!window.IsResearchEntry)throw new InvalidOperationException("无效的研究入口。");
            window.Closed+=(_,_)=>{if(ReferenceEquals(remoteReturn,window)){remoteReturn=null;researchEntryReady=false;}};
            var screen=Screens.Primary ?? Screens.ScreenFromWindow(this);
            if(screen is not null) { var area=screen.WorkingArea; window.Position=new PixelPoint(area.Right-(int)Math.Ceiling(window.Width*screen.Scaling),area.Y+(int)(30*screen.Scaling)); }
            window.Show();
            if(!window.IsVisible)throw new InvalidOperationException("研究入口不可见。");
            remoteReturn=window;
            researchEntryReady=true;
        }
        catch
        {
            if(window is not null)
            {
                var closed=false;
                try { closed=window.ReleaseResearch(); } catch { }
                if(!closed && !window.IsResearchClosed) { remoteReturn=window; researchEntryReady=false; PresentResearchStopped(); }
            }
            throw;
        }
        finally { researchCreating=false; }
    }
    private bool TryReleaseResearchReturn(bool unresolved)
    {
        if(unresolved || researchCreating || researchReleasing)return false;
        var window=remoteReturn;if(window is null)return true;
        researchReleasing=true;
        try
        {
            var closed=window.ReleaseResearch();
            if(closed && ReferenceEquals(remoteReturn,window)){remoteReturn=null;researchEntryReady=false;}
            return closed;
        }
        finally { researchReleasing=false; }
    }
    private void CloseRemoteReturn() { /* Connection-scoped research entry is retained until safe release. */ }
#else
    private void CloseRemoteReturn()
    {
        var window = remoteReturn;
        if (window is null) return;
        if (remoteReturnClosing) throw new InvalidOperationException("归还窗口正在关闭，请稍候。");
        // Closing can be vetoed or throw. Keep ownership until the window has
        // actually closed so that a second same-title capture window is not made.
        remoteReturnClosing = true;
        try
        {
            window.Close();
            // The Closed handler, not visibility, is the ownership signal. A
            // canceled close may hide the window without actually closing it.
            if (ReferenceEquals(remoteReturn, window)) throw new InvalidOperationException("归还窗口尚未关闭，请检查键鼠。");
        }
        finally { remoteReturnClosing = false; }
    }
#endif

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || closing) return; busy = true; IsEnabled = false;
        try { await action(); }
        catch (Exception error) { feedback.Text = "操作未完成：" + error.Message; }
        finally { busy = false; IsEnabled = true; }
    }
    private async Task RunMiniAsync(Func<Task> action)
    {
        if (busy || closing) throw new InvalidOperationException("正在处理其他操作，请稍候。");
        busy = true; IsEnabled = false;
        try { await action(); }
        finally { busy = false; if (!closing) IsEnabled = true; }
    }
    private Button Action(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = text }; button.Classes.Add(primary ? "accent" : "secondary");
        button.Click += async (_, _) => await RunAsync(action); return button;
    }
    private async Task<bool> AskAsync(string title, string message)
    {
        var candidate = CreateCandidateDialog(title, message, "一致，确认");
        candidate.Accept.Click += (_, _) => candidate.Window.Close(true);
        candidate.Cancel.Click += (_, _) => candidate.Window.Close(false);
        return await candidate.Window.ShowDialog<bool>(this);
    }
    private static StackPanel Row(params Control[] controls)
    { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var control in controls) row.Children.Add(control); return row; }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#627D9E")) };
    private static TextBlock TitleText(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#243A59")) };
    private static Border Card(string title, Control content)
    {
        var stack = new StackPanel { Spacing = 12 }; stack.Children.Add(TitleText(title)); stack.Children.Add(content);
        return new Border { Background = new SolidColorBrush(Color.Parse("#FBFDFF")), BorderBrush = new SolidColorBrush(Color.Parse("#D9E5F4")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(20), Child = stack };
    }
}
