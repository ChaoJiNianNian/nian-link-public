using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.DeskLink.Preview;
using DesktopToolbox.App.DeskLink;
using DesktopToolbox.Windows.DeskSwitch.Collaboration;
using DesktopToolbox.Windows.DeskSwitch.Collaboration.LivePreview;
using Microsoft.Win32;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    private readonly WindowsPreviewCaptureProvider previewCapture = new();
    private readonly WindowsLivePreviewCaptureProvider livePreviewCapture = new();
    private readonly HashSet<string> hostPreviewShares = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreviewCaptureIdentity> hostPreviewSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreviewCaptureCadence> hostCaptureCadences = new(StringComparer.Ordinal);
    private readonly PreviewCaptureIdentity peerPreviewSource = new();
    private readonly PreviewCaptureCadence peerCaptureCadence = new();
    private string? hostViewedPeer;
    private Guid? hostPreviewRequest, peerPreviewRequest;
    private long hostPreviewRenewed, peerPreviewRenewed, previewEpoch;
    private bool hostViewEnabled, peerViewEnabled, peerShareEnabled, previewSessionLocked, observingSession;
    private bool updatingRefreshChoice;
    private int peerRefreshSeconds = PreviewCaptureCadence.DefaultRefreshSeconds;
    private PreviewReturnWindow? targetReturn;
    private bool targetReturnClosing;
    private LivePreviewUploadController? peerLiveUpload;
    private DetachedPreviewWindow? hostDetached;
    private DeskflowPreviewSession? hostDetachedSession;
    private string? hostDetachedPeer;
    private DetachedPreviewWindow? peerDetached;
    private DeskflowPreviewSession? peerDetachedSession;
    private PreviewSourceStatus? peerRemoteSource;
    private bool peerRemoteLiveSupported, peerRemoteGenerationSupported;
    private PreviewFrame lastPeerFrame = PreviewFrame.Off();
    private bool peerMiniPreviewVisible, peerMiniHighResolution;

    private async void RefreshHostPreview_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (host is null || !hostViewEnabled || HostDevices.SelectedItem is not TrustedDeviceIdentity)
            throw new InvalidOperationException("请先选择设备并开启对方画面。");
        hostPreviewRenewed = 0;
        await PollHostPreviewAsync();
    });

    private async void RefreshPeerPreview_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (peerData is null || !peerViewEnabled) throw new InvalidOperationException("请先开启主控画面。");
        peerPreviewRenewed = 0;
        await PollPeerPreviewAsync();
    });

    private async void OpenPeerPreview_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        var current = peer ?? throw new InvalidOperationException("请先连接主控。");
        var data = peerData ?? throw new InvalidOperationException("画面连接尚未准备好。");
        var invitation = peerStore?.Get() ?? throw new InvalidOperationException("请先确认配对。");
        if (peerMiniService is not null) throw new InvalidOperationException("请在互联小窗中打开主控画面。");
        if (!peerViewEnabled) throw new InvalidOperationException("请先开启主控画面。");
        if (peerDetached is not null) { peerDetached.Activate(); return; }
        var keyboard = await current.GetStateAsync();
        if (keyboard.ProcessId is not > 0) throw new InvalidOperationException("键鼠共享尚未准备好。");
        var source = new RemoteSubscriptionControlLivePreviewSource(() => ReferenceEquals(peerData, data) ? data : null,
            () => peerViewEnabled && peerRemoteGenerationSupported && peerRemoteLiveSupported && peerRemoteSource?.Availability == "enabled");
        var session = new DeskflowPreviewSession(PreviewPeer(current, invitation), source);
        var window = new DetachedPreviewWindow(session, invitation.PeerName, invitation.HostName,
            () => PreviewForeground.IsOwnedCaptureWindow(keyboard.ProcessId, current.OwnsLiveProcess));
        window.SetClearBlueAppearance();
        peerDetached = window; peerDetachedSession = session;
        window.Closed += (_, _) => { if (ReferenceEquals(peerDetached, window)) { peerDetached = null; peerDetachedSession = null; } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var frame = await data.GetPreviewAsync(timeout.Token);
        UpdatePeerDetached(frame);
        window.Show();
    });

    private void UpdatePeerDetached(PreviewFrame frame)
    {
        if (peerDetached is not { } window || peerDetachedSession is not { } session) return;
        window.SetPassivePreviewContext(peerPreviewRequest, peerRemoteSource);
        var available = peerViewEnabled && peerRemoteSource?.Availability == "enabled";
        window.SetConnectionState(available && session.Supported, available ? "等待主控实时画面" : "请在主控开启画面共享", available);
        window.UpdateFrame(PeerPreviewImage.Source as BitmapSource, PeerPreviewStatus.Text, frame);
    }

    private void ClosePeerDetached() { var window = peerDetached; peerDetached = null; peerDetachedSession = null; window?.Close(); }

    private async void OpenHostPreview_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        var current = host ?? throw new InvalidOperationException("请先启动主控共享。");
        var selected = SelectedHostDevice();
        if (!hostViewEnabled || hostViewedPeer != selected.Name) throw new InvalidOperationException("请先开启并等待对方画面。");
        if (hostDetached is not null) { hostDetached.Activate(); return; }
        var viewEpoch = previewEpoch;
        var keyboard = await current.GetKeyboardStateAsync(CancellationToken.None);
        if (!HostPreviewStillCurrent(selected.Name, viewEpoch, () => ReferenceEquals(current, host))) return;
        if (keyboard.ProcessId is not > 0) throw new InvalidOperationException("键鼠共享尚未准备好。");
        var session = await current.CreateHostPreviewSessionAsync(selected.Name, CancellationToken.None);
        if (!HostPreviewStillCurrent(selected.Name, viewEpoch, () => ReferenceEquals(current, host))) { await session.DisposeAsync(); return; }
        var window = new DetachedPreviewWindow(session, "Windows", selected.Name,
            () => PreviewForeground.IsOwnedCaptureWindow(keyboard.ProcessId, current.OwnsLiveKeyboardProcess));
        window.SetClearBlueAppearance();
        hostDetached = window; hostDetachedSession = session; hostDetachedPeer = selected.Name;
        window.Closed += (_, _) => { if (ReferenceEquals(hostDetached, window)) { hostDetached = null; hostDetachedSession = null; hostDetachedPeer = null; } };
        var frame = await current.GetPreviewAsync(selected.Name, CancellationToken.None);
        if (!ReferenceEquals(hostDetached, window) || !HostPreviewStillCurrent(selected.Name, viewEpoch, () => ReferenceEquals(current, host))) { if (ReferenceEquals(hostDetached, window)) CloseHostDetached(); return; }
        var source = await current.GetRemotePreviewSourceAsync(selected.Name, CancellationToken.None);
        if (!ReferenceEquals(hostDetached, window) || !HostPreviewStillCurrent(selected.Name, viewEpoch, () => ReferenceEquals(current, host))) { if (ReferenceEquals(hostDetached, window)) CloseHostDetached(); return; }
        UpdateHostDetached(frame, source);
        window.Show();
    });

    private void UpdateHostDetached(PreviewFrame frame, PreviewSourceStatus? source)
    {
        if (hostDetached is not { } window || hostDetachedSession is not { } session) return;
        window.SetPassivePreviewContext(hostPreviewRequest, source);
        var available = hostViewEnabled && source?.Availability == "enabled";
        window.SetConnectionState(available && session.Supported, available ? "等待对方实时画面" : "请在对方开启画面共享", available);
        window.UpdateFrame(HostPreviewImage.Source as BitmapSource, HostPreviewStatus.Text, frame);
    }

    private void CloseHostDetached() { var window = hostDetached; hostDetached = null; hostDetachedSession = null; hostDetachedPeer = null; window?.Close(); }

    private async Task PollTargetReturnAsync()
    {
        DeskflowPreviewState? state = null;
        Func<string, Task<DeskflowPreviewState>>? end = null;
        string? local = null;
        if (host is { } currentHost && currentHost.Devices.Count > 0)
        {
            state = await currentHost.ReadPreviewAsync(CancellationToken.None);
            local = HostName.Text.Trim();
            end = id => currentHost.EndPreviewAsync(id, CancellationToken.None);
        }
        else if (peer is { } currentPeer && peerStore?.Get() is { } invitation)
        {
            // A late request can execute after a Viewing read. Keep any existing
            // emergency-return window available while the owner is unresolved.
            if (PeerControlUnresolved) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var guarded = PreviewPeer(currentPeer, invitation);
            state = await guarded.ReadPreviewAsync(timeout.Token);
            if (PeerControlUnresolved) return;
            local = invitation.PeerName;
            end = id => guarded.EndPreviewAsync(id);
        }
        ApplyTargetReturn(state, local, end);
    }

    private void ApplyTargetReturn(DeskflowPreviewState? state, string? local,
        Func<string, Task<DeskflowPreviewState>>? end)
    {
        if (targetReturnClosing) throw new InvalidOperationException("归还窗口正在关闭，请稍候。");
        var wanted = state is { Phase: DeskflowPreviewPhase.Active or DeskflowPreviewPhase.Recovery } &&
            !string.IsNullOrWhiteSpace(state.Id) && !string.IsNullOrWhiteSpace(local) && state.TargetComputer == local ? state.Id : null;
        if (targetReturn?.SessionId == wanted)
        {
            // A canceled close may have hidden an owned window. A later
            // observation of the same session can restore the existing entry.
            if (targetReturn is { IsVisible: false } hidden) hidden.Show();
            return;
        }
        CloseTargetReturn();
        if (targetReturn is not null) throw new InvalidOperationException("归还窗口尚未关闭，请检查键鼠。");
        if (wanted is null || end is null) return;
        var window = new PreviewReturnWindow(wanted, async id =>
        {
            var returned = await end(id);
            if (returned.HasSession) throw new InvalidOperationException("键鼠归还尚未确认，请重试。");
        });
        targetReturn = window;
        window.Closed += (_, _) => { if (ReferenceEquals(targetReturn, window)) targetReturn = null; };
        window.Show();
    }

    private void CloseTargetReturn()
    {
        var window = targetReturn;
        if (window is null) return;
        if (targetReturnClosing) throw new InvalidOperationException("归还窗口正在关闭，请稍候。");
        targetReturnClosing = true;
        try
        {
            window.Close();
            // Only the Closed callback releases ownership; a canceled close
            // may hide the window while keeping it alive.
            if (ReferenceEquals(targetReturn, window)) throw new InvalidOperationException("归还窗口尚未关闭，请检查键鼠。");
        }
        finally { targetReturnClosing = false; }
    }


    private void InitializePreview()
    {
        Loaded += (_, _) =>
        {
            HostRefreshChoice.IsEnabled = PeerRefreshChoice.IsEnabled = false;
            HostRefreshChoice.SelectionChanged += async (_, _) => await SaveHostRefreshAsync();
            PeerRefreshChoice.SelectionChanged += async (_, _) => await SavePeerRefreshAsync();
            try { SystemEvents.SessionSwitch += PreviewSessionSwitch; observingSession = true; }
            catch (InvalidOperationException) { }
        };
    }

    private static int RefreshChoice(ComboBox choice) => choice.SelectedItem is ComboBoxItem { Tag: string text } && int.TryParse(text, out var seconds)
        ? seconds : PreviewCaptureCadence.DefaultRefreshSeconds;
    private void SetRefreshChoice(ComboBox choice, int seconds)
    {
        updatingRefreshChoice = true;
        try { choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == seconds.ToString()) ?? choice.Items[0]; }
        finally { updatingRefreshChoice = false; }
    }
    private async Task RefreshHostRefreshChoiceAsync()
    {
        var current = host; var selected = HostDevices.SelectedItem as TrustedDeviceIdentity;
        HostRefreshChoice.IsEnabled = false;
        if (current is null || selected is null || !current.Devices.Any(device => device.Name == selected.Name)) return;
        try
        {
            var seconds = await current.GetPreviewRefreshSecondsAsync(selected.Name, CancellationToken.None);
            if (!ReferenceEquals(current, host) || !ReferenceEquals(selected, HostDevices.SelectedItem)) return;
            SetRefreshChoice(HostRefreshChoice, seconds); HostRefreshChoice.IsEnabled = true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ObjectDisposedException) { HostRefreshChoice.IsEnabled = false; }
    }
    private async Task SaveHostRefreshAsync()
    {
        if (updatingRefreshChoice || !HostRefreshChoice.IsEnabled) return;
        await WorkAsync(async () =>
        {
            var current = host ?? throw new InvalidOperationException("请先启动共享。");
            var selected = SelectedHostDevice();
            await current.SetPreviewRefreshSecondsAsync(selected.Name, RefreshChoice(HostRefreshChoice), CancellationToken.None);
            Feedback.Text = "监看刷新已保存；操作时自动使用实时画面。";
        });
        await RefreshHostRefreshChoiceAsync();
    }
    private async Task SavePeerRefreshAsync()
    {
        if (updatingRefreshChoice || !PeerRefreshChoice.IsEnabled) return;
        await WorkAsync(() => SetPeerRefreshAsync(RefreshChoice(PeerRefreshChoice), CancellationToken.None));
        SetRefreshChoice(PeerRefreshChoice, peerRefreshSeconds);
    }

    private async Task SetPeerRefreshAsync(int seconds, CancellationToken token)
    {
        PreviewCaptureCadence.ValidateRefreshSeconds(seconds);
        var invitation = peerStore?.Get() ?? throw new InvalidOperationException("请先连接主控。");
        var settings = peerSettings ?? throw new InvalidOperationException("本机设置尚未打开。");
        var key = InvitationKey(invitation);
        await settings.SetAsync(key, settings.Get(key) with { PreviewRefreshSeconds = seconds }, token);
        peerRefreshSeconds = seconds;
        SetRefreshChoice(PeerRefreshChoice, seconds);
        Feedback.Text = "监看刷新已保存；操作时自动使用实时画面。";
    }

    private void DisposePreviewObservation()
    {
        CloseHostDetached();
        ClosePeerDetached();
        CloseTargetReturn();
#if NIAN_PEER_EMERGENCY_RESEARCH
        var emergency = peerEmergencyWindow; peerEmergencyWindow = null; emergency?.Release();
#endif
        if (observingSession) { SystemEvents.SessionSwitch -= PreviewSessionSwitch; observingSession = false; }
    }

    private void PreviewSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)) return;
        Dispatcher.BeginInvoke(async () =>
        {
            previewSessionLocked = e.Reason == SessionSwitchReason.SessionLock;
            previewEpoch++;
            if (previewSessionLocked) await InvalidateLockedPreviewAsync();
        });
    }

    private async Task InvalidateLockedPreviewAsync()
    {
        if (peerLiveUpload is { } upload) await upload.ObserveAsync(null, false);
        var current = host;
        if (current is not null)
            foreach (var item in current.Devices)
                try { await current.SetPreviewSourceAsync(item.Name, Source(item.Name).SetAvailability(hostPreviewShares.Contains(item.Name) ? "locked" : "disabled"), CancellationToken.None); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or ObjectDisposedException) { }
        var data = peerData;
        if (data is not null)
        {
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await data.SetPreviewSourceAsync(peerPreviewSource.SetAvailability(peerShareEnabled ? "locked" : "disabled"), timeout.Token); }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or OperationCanceledException or UnauthorizedAccessException or ObjectDisposedException) { }
        }
    }

    private PreviewCaptureIdentity Source(string peer)
    {
        if (!hostPreviewSources.TryGetValue(peer, out var value)) hostPreviewSources[peer] = value = new();
        return value;
    }
    private PreviewCaptureCadence Cadence(string peer)
    {
        if (!hostCaptureCadences.TryGetValue(peer, out var value)) hostCaptureCadences[peer] = value = new();
        return value;
    }

    private void RefreshHostPreviewToggle()
    {
        var selected = HostDevices.SelectedItem as TrustedDeviceIdentity;
        HostSharePreview.IsEnabled = selected is not null && host?.Devices.Any(x => x.Name == selected.Name) == true;
        HostSharePreview.IsChecked = selected is not null && hostPreviewShares.Contains(selected.Name);
    }

    private async void HostViewPreview_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (hostMiniService is not null && HostViewPreview.IsChecked == true)
        { HostViewPreview.IsChecked = false; throw new InvalidOperationException("请在互联小窗中查看画面。"); }
        if (host is null || HostDevices.SelectedItem is not TrustedDeviceIdentity selected || !host.Devices.Any(x => x.Name == selected.Name))
        { HostViewPreview.IsChecked = false; throw new InvalidOperationException("请先选择已连接的设备。"); }
        hostViewEnabled = HostViewPreview.IsChecked == true; previewEpoch++;
        if (!hostViewEnabled) await ClearHostViewAsync(); else await PollHostPreviewAsync();
    });

    private async void HostSharePreview_Click(object sender, RoutedEventArgs e)
    {
        var wanted = HostSharePreview.IsChecked == true;
        await WorkAsync(async () =>
        {
            var device = SelectedHostDevice();
            if (host is null || !host.Devices.Any(x => x.Name == device.Name)) throw new InvalidOperationException("请先启动设备共享。");
            previewEpoch++;
            if (!wanted) hostPreviewShares.Remove(device.Name);
            await host.SetPreviewSourceAsync(device.Name, Source(device.Name).SetAvailability(wanted ? Availability() : "disabled"), CancellationToken.None, livePreviewCapture);
            if (wanted) hostPreviewShares.Add(device.Name); else Cadence(device.Name).Reset();
            Feedback.Text = wanted ? "已允许所选设备查看本机画面。" : "已关闭对所选设备的画面共享。";
        });
        RefreshHostPreviewToggle();
    }

    private async void PeerViewPreview_Click(object sender, RoutedEventArgs e)
    {
        var wanted = PeerViewPreview.IsChecked == true;
        await WorkAsync(() => SetPeerViewPreviewAsync(wanted, CancellationToken.None));
        PeerViewPreview.IsChecked = peerViewEnabled;
    }

    private async Task SetPeerViewPreviewAsync(bool enabled, CancellationToken cancellationToken)
    {
        var data = peerData ?? throw new InvalidOperationException("请先连接主控。");
        peerViewEnabled = enabled; PeerViewPreview.IsChecked = enabled; previewEpoch++;
        if (!peerViewEnabled)
        {
            ClosePeerDetached();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await data.SetPreviewRequestedAsync(false, timeout.Token); peerPreviewRequest = null; lastPeerFrame = PreviewFrame.Off();
            SetPreview(PeerPreviewImage, PeerPreviewStatus, PreviewFrame.Off());
        }
        else await PollPeerPreviewAsync();
    }

    private async void PeerSharePreview_Click(object sender, RoutedEventArgs e)
    {
        var wanted = PeerSharePreview.IsChecked == true;
        await WorkAsync(async () =>
        {
            var data = peerData ?? throw new InvalidOperationException("请先连接主控。");
            previewEpoch++;
            if (!wanted) { peerShareEnabled = false; if (peerLiveUpload is { } upload) await upload.ObserveAsync(null, false); }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await data.SetPreviewSourceAsync(peerPreviewSource.SetAvailability(wanted ? Availability() : "disabled"), timeout.Token);
            if (wanted) peerShareEnabled = true; else peerCaptureCadence.Reset();
            Feedback.Text = wanted ? "已允许主控查看本机画面。" : "已关闭本机画面共享。";
        });
        PeerSharePreview.IsChecked = peerShareEnabled;
    }

    private async Task PollHostPreviewAsync()
    {
        var current = host;
        if (current is null) return;
        var selected = (HostDevices.SelectedItem as TrustedDeviceIdentity)?.Name;
        if (!hostViewEnabled || selected is null || !current.Devices.Any(x => x.Name == selected)) await ClearHostViewAsync();
        else
        {
            var viewEpoch = previewEpoch;
            if (hostViewedPeer != selected)
            {
                await ClearHostViewAsync();
                if (!ReferenceEquals(current, host) || !hostViewEnabled || previewSessionLocked || viewEpoch != previewEpoch ||
                    (HostDevices.SelectedItem as TrustedDeviceIdentity)?.Name != selected) return;
                hostViewedPeer = selected; hostPreviewRequest = null;
            }
            if (ShouldRotate(hostPreviewRenewed)) hostPreviewRequest = Guid.NewGuid();
            hostPreviewRequest ??= Guid.NewGuid();
            await current.SetPreviewRequestedAsync(selected, true, CancellationToken.None, hostPreviewRequest);
            if (!HostPreviewStillCurrent(selected, viewEpoch, () => ReferenceEquals(current, host))) return;
            hostPreviewRenewed = Stopwatch.GetTimestamp();
            await ApplyHostPreviewFrameAsync(selected, viewEpoch, current.GetPreviewAsync(selected, CancellationToken.None),
                () => ReferenceEquals(current, host), () => current.GetRemotePreviewSourceAsync(selected, CancellationToken.None));
        }
        foreach (var device in current.Devices)
        {
            var source = Source(device.Name);
            var allowed = hostPreviewShares.Contains(device.Name);
            var snapshot = source.SetAvailability(allowed ? Availability() : "disabled");
            await current.SetPreviewSourceAsync(device.Name, snapshot, CancellationToken.None, livePreviewCapture);
            var demand = await current.GetCaptureDemandAsync(device.Name, CancellationToken.None);
            var cadence = Cadence(device.Name);
            if (!cadence.ShouldCapture(allowed && snapshot.Availability == "enabled" && demand.Requested && demand.RequestId is not null,
                demand.RefreshSeconds, demand.Width, demand.Height)) continue;
            var epoch = previewEpoch;
            var frame = await previewCapture.CaptureAsync(CancellationToken.None, demand.Width, demand.Height);
            if (epoch != previewEpoch || !ReferenceEquals(current, host) || !hostPreviewShares.Contains(device.Name) || Availability() != "enabled") continue;
            frame = frame with { RequestId = demand.RequestId, SourceStatus = snapshot, CaptureSequence = source.NextSequence(demand.RequestId) };
            await current.PublishPreviewAsync(device.Name, frame, CancellationToken.None);
        }
    }

    private async Task<bool> ApplyHostPreviewFrameAsync(string selected, long epoch, Task<PreviewFrame> frameTask,
        Func<bool> runtimeMatches, Func<Task<PreviewSourceStatus?>> readSource)
    {
        var frame = await frameTask;
        if (!HostPreviewStillCurrent(selected, epoch, runtimeMatches)) return false;
        SetPreview(HostPreviewImage, HostPreviewStatus, frame);
        if (hostDetachedPeer == selected)
        {
            var source = await readSource();
            if (!HostPreviewStillCurrent(selected, epoch, runtimeMatches) || hostDetachedPeer != selected) return false;
            UpdateHostDetached(frame, source);
        }
        return true;
    }

    private bool HostPreviewStillCurrent(string selected, long epoch, Func<bool> runtimeMatches) =>
        runtimeMatches() && hostViewEnabled && !previewSessionLocked && epoch == previewEpoch && hostViewedPeer == selected &&
        (HostDevices.SelectedItem as TrustedDeviceIdentity)?.Name == selected;

    private async Task PollPeerPreviewAsync()
    {
        var data = peerData;
        if (data is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        PreviewFrame? frame = null;
        var wantsFrame = peerViewEnabled && (peerMiniService is null || peerMiniPreviewVisible || IsVisible && WindowState != WindowState.Minimized);
        if (wantsFrame)
        {
            if (ShouldRotate(peerPreviewRenewed)) peerPreviewRequest = Guid.NewGuid();
            peerPreviewRequest ??= Guid.NewGuid();
            await data.SetPreviewRequestedAsync(true, timeout.Token, maxWidth: peerMiniHighResolution ? 1920 : 960,
                maxHeight: peerMiniHighResolution ? 1080 : 540, previewRefreshSeconds: peerRefreshSeconds, requestId: peerPreviewRequest);
            peerPreviewRenewed = Stopwatch.GetTimestamp();
            frame = await data.GetPreviewAsync(timeout.Token);
            if (ReferenceEquals(data, peerData) && peerViewEnabled) { lastPeerFrame = frame; SetPreview(PeerPreviewImage, PeerPreviewStatus, frame); }
        }
        else if (peerViewEnabled && peerPreviewRequest is not null)
        {
            await data.SetPreviewRequestedAsync(false, timeout.Token);
            peerPreviewRequest = null; peerPreviewRenewed = 0; lastPeerFrame = PreviewFrame.Off("paused");
        }
        var source = peerPreviewSource.SetAvailability(peerShareEnabled ? Availability() : "disabled");
        await data.SetPreviewSourceAsync(source, timeout.Token);
        var status = await data.GetStatusAsync(timeout.Token, supportsLivePreview: livePreviewCapture.IsSupported, supportsPassivePreviewGeneration: true);
        peerRemoteSource = status.LocalPreviewSourceStatus; peerRemoteLiveSupported = status.LivePreviewSupported;
        peerRemoteGenerationSupported = status.PassivePreviewGenerationSupported;
        if (frame is not null) UpdatePeerDetached(frame);
        if (ReferenceEquals(data, peerData)) MaybeReceivePending(data, status);
        if (peerLiveUpload is { } upload)
            await upload.ObserveAsync(status.RequestedLivePreview,
                ReferenceEquals(data, peerData) && peerShareEnabled && Availability() == "enabled");
        if (status.RequestedLivePreview is not null) return;
        if (!peerCaptureCadence.ShouldCapture(peerShareEnabled && source.Availability == "enabled" && status.CaptureRequested && status.CaptureRequestId is not null,
            status.PreviewRefreshSeconds, status.CaptureMaxWidth, status.CaptureMaxHeight)) return;
        var epoch = previewEpoch;
        var captured = await previewCapture.CaptureAsync(timeout.Token, status.CaptureMaxWidth, status.CaptureMaxHeight);
        if (epoch != previewEpoch || !ReferenceEquals(data, peerData) || !peerShareEnabled || Availability() != "enabled") return;
        captured = captured with { RequestId = status.CaptureRequestId, SourceStatus = source, CaptureSequence = peerPreviewSource.NextSequence(status.CaptureRequestId) };
        await data.PublishPreviewAsync(captured, timeout.Token);
    }

    private static bool ShouldRotate(long last) => last == 0 || Stopwatch.GetElapsedTime(last) >= TimeSpan.FromSeconds(3);
    private string Availability() => !previewSessionLocked && WindowsPreviewCaptureProvider.IsInteractiveDesktop() ? "enabled" : "locked";

    private async Task ClearHostViewAsync()
    {
        CloseHostDetached();
        var old = hostViewedPeer; hostViewedPeer = null; hostPreviewRequest = null; hostPreviewRenewed = 0;
        SetPreview(HostPreviewImage, HostPreviewStatus, PreviewFrame.Off());
        if (old is not null && host is { } current)
            try { await current.SetPreviewRequestedAsync(old, false, CancellationToken.None); }
            catch (Exception error) when (error is ArgumentException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task StopHostPreviewAsync()
    {
        CloseTargetReturn();
        previewEpoch++; hostViewEnabled = false; HostViewPreview.IsChecked = false;
        await ClearHostViewAsync();
        var current = host;
        if (current is not null)
            foreach (var device in current.Devices)
                try { await current.SetPreviewSourceAsync(device.Name, Source(device.Name).SetAvailability("disabled"), CancellationToken.None); }
                catch (Exception error) when (error is ArgumentException or ObjectDisposedException or InvalidOperationException) { }
        hostPreviewShares.Clear(); hostCaptureCadences.Clear(); HostSharePreview.IsChecked = false;
    }

    private async Task StopPeerPreviewAsync()
    {
#if DISCOVERY_PORTABLE
        // An existing stop can already be in flight while CanStop is false.
        // Await its shared cleanup task before closing or retiring the connection.
        if (peerDetachedSession is { } session)
            await session.StopAsync("正在收回键鼠，请稍候");
#endif
        ClosePeerDetached(); peerRemoteSource = null; peerRemoteLiveSupported = peerRemoteGenerationSupported = false;
        PeerRefreshChoice.IsEnabled = false;
        CloseTargetReturn();
        if (peerLiveUpload is { } upload) { peerLiveUpload = null; await upload.DisposeAsync(); }
        previewEpoch++; peerViewEnabled = peerShareEnabled = false; PeerViewPreview.IsChecked = PeerSharePreview.IsChecked = false;
        peerPreviewRequest = null; peerPreviewRenewed = 0; peerCaptureCadence.Reset(); lastPeerFrame = PreviewFrame.Off();
        SetPreview(PeerPreviewImage, PeerPreviewStatus, PreviewFrame.Off());
        if (peerData is not { } data) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await data.SetPreviewRequestedAsync(false, timeout.Token);
            await data.SetPreviewSourceAsync(peerPreviewSource.SetAvailability("disabled"), timeout.Token);
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or OperationCanceledException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private static void SetPreview(Image image, TextBlock label, PreviewFrame frame)
    {
        if (frame.State == "live" && frame.JpegBytes.Length != 0)
        {
            using var stream = new MemoryStream(frame.JpegBytes);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            image.Source = bitmap; label.Visibility = Visibility.Collapsed;
        }
        else
        {
            image.Source = null; label.Visibility = Visibility.Visible;
            label.Text = frame.State switch { "locked" => "对方已锁屏", "offline" => "对方未连接", "off" => "画面已关闭", "permission" => "对方需要允许屏幕录制", _ => "等待对方画面" };
        }
    }
}
