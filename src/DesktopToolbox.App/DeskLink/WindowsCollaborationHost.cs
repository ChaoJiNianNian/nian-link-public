using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Threading;
using DesktopToolbox.Core;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.Windows.DeskSwitch;
using DesktopToolbox.Windows.DeskSwitch.Collaboration;
using DesktopToolbox.Windows.DeskSwitch.Collaboration.LivePreview;
using Nian.Input.Control;

namespace DesktopToolbox.App.DeskLink;

/// <summary>Owns the data endpoint independently from the keyboard session and visible windows.</summary>
public sealed partial class WindowsCollaborationHost : IMiniCollaborationService, IAsyncDisposable
{
    private readonly ModuleContext context;
    private readonly IIntegratedDeskSwitchService keyboard;
    private readonly Func<CancellationToken,int,int,Task<PreviewFrame>> captureFrame;
    private readonly ILivePreviewCaptureProvider liveCapture;
    private readonly PreviewCaptureCadence captureCadence;
    private readonly WindowsClipboardAdapter clipboard;
    private readonly Action<Exception> log;
    private readonly int dataPort;
    private readonly SemaphoreSlim refreshGate = new(1, 1), settingsGate = new(1, 1);
    private readonly SemaphoreSlim endpointGate = new(1, 1);
    private readonly SemaphoreSlim previewSettingGate = new(1, 1);
    private readonly object previewDemandGate = new();
    private CancellationTokenSource previewCaptureCycle = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource clipboardLifetime = new();
    private WindowsCollaborationSettings settings = new();
    private ClipboardBridgeCursor cursor = new("uninitialized");
    private DeskSwitchSettings names = new();
    private CollaborationServer? server;
    private string boundAddress = "";
    private bool previewVisible = true, highResolution;
    private int liveCaptureAllowed;
    private bool passiveCaptureSuspended;
    private Task loop = Task.CompletedTask;
    private MiniCollaborationSnapshot snapshot = new();
    private string? clipboardError;
    private string? configurationError;
    private long loggedAt;
    private int disposed;
    public event EventHandler? Changed;
    public MiniCollaborationSnapshot Snapshot => Volatile.Read(ref snapshot);
    public int DataPort => server?.Port ?? dataPort;
    // Restricted to in-process synthetic tests; normal app startup does not set these.
    internal Func<IPAddress?>? TestAddressProvider { get; init; }
    internal bool DisableClipboardForTesting { get; init; }
    // The independent host can serialize its settings form and the complete start.
    public Func<CancellationToken, Func<Task>, Task>? CoordinateKeyboardStartAsync { get; set; }

    public WindowsCollaborationHost(ModuleContext context, IIntegratedDeskSwitchService keyboard, Dispatcher dispatcher, Action<Exception> log, int dataPort = 24802,
        Func<CancellationToken,int,int,Task<PreviewFrame>>? captureFrame = null, TimeProvider? previewClock = null, INativePreviewControl? previewControl = null,
        ILivePreviewCaptureProvider? liveCaptureProvider = null, Func<bool>? desktopUnlockedProvider = null)
    {
        this.context = context; this.keyboard = keyboard; this.log = log; this.dataPort = dataPort; clipboard = new(dispatcher);
        this.captureFrame = captureFrame ?? new WindowsPreviewCaptureProvider().CaptureAsync;
        liveCapture = liveCaptureProvider ?? new WindowsLivePreviewCaptureProvider();
        desktopUnlocked = desktopUnlockedProvider ?? IsDesktopUnlocked;
        observeSessionEvents = desktopUnlockedProvider is null;
        controlClock=previewClock;
        explicitPreviewControl=previewControl;
        captureCadence = new(previewClock);
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        names = (await context.Store.LoadAsync("desk-switch.json", () => new DeskSwitchSettings(), token)).Validate();
        try
        {
            settings = (await context.Store.LoadAsync("collaboration.json", () => new WindowsCollaborationSettings { ClipboardEnabled = names.ClipboardEnabled }, token)).Validate();
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { configurationError = "协同设置读取失败，请重新选择目录：" + error.Message; log(error); }
        Volatile.Write(ref liveCaptureAllowed, settings.PreviewEnabled ? 1 : 0);
        StartDesktopObservation();
        cursor = new(settings.OriginId);
        Publish(PreviewFrame.Off(), "正在检查 Mac 协同连接");
        loop = Task.Run(() => RunAsync(lifetime.Token));
    }

    public async Task ReloadDeviceNamesAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await settingsGate.WaitAsync(linked.Token);
        try
        {
            var current = (await context.Store.LoadAsync("desk-switch.json", () => new DeskSwitchSettings(), linked.Token)).Validate();
            Volatile.Write(ref names, current);
            Publish(Snapshot.Preview, Snapshot.ConnectionMessage);
        }
        finally { settingsGate.Release(); }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await RefreshAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) { LogThrottled(error); Publish(PreviewFrame.Off("error", error.Message), "协同暂不可用：" + error.Message); }
            try { await Task.Delay(500, token); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        if (Volatile.Read(ref disposed) != 0 || !await refreshGate.WaitAsync(0, token)) return;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            token = linked.Token;
            var profile = Path.Combine(context.Paths.Resolve("desk-link"), "control");
            if (!File.Exists(Path.Combine(profile, "control.pem")) || !File.Exists(Path.Combine(profile, "pair-token")))
            { Publish(PreviewFrame.Off("offline"), "先在工具箱开始共享，并让 Mac 导入配对文件"); return; }
            var address = FindAddress();
            if (address is null || server is null || boundAddress != address.ToString())
            {
                await endpointGate.WaitAsync(token);
                try
                {
                address = FindAddress();
                if (server is not null) await server.DisposeAsync();
                server = null;
                boundAddress = "";
                if (address is null)
                {
                    captureCadence.Reset();
                    Publish(PreviewFrame.Off("offline"), "未发现可供 Mac 连接的局域网，请检查网络。");
                    return;
                }
                var endpoint = new CollaborationServer(new() { ProfileDirectory = profile, BindAddress = address, Port = dataPort, ReceiveDirectory = NullIfEmpty(settings.ReceiveDirectory), SharedDirectories = Shares(settings) });
                try { await endpoint.StartAsync(token); }
                catch { await endpoint.DisposeAsync(); throw; }
                lock(previewDemandGate)
                {
                    var state=localSource??initialSource;
                    endpoint.SetLocalPreviewSource(state);
                    endpoint.LivePreview.SetLocalCaptureProvider(state.Availability=="enabled"?liveCapture:null);
                    server=endpoint;
                }
                boundAddress = address.ToString(); captureCadence.Reset();
                endpoint.PeerError += (_, error) => LogThrottled(error);
                }
                finally { endpointGate.Release(); }
            }
            var owned = server;
            var selected = settings;
            var sourceState=RefreshLocalSourceStatus();
            ObservePassivePeer(owned);
            owned.SetLocalClipboardEnabled(selected.ClipboardEnabled);
            UpdatePreviewDemand(owned);
            var captureRequested = selected.PreviewEnabled && sourceState.Availability=="enabled" && owned.LocalCaptureRequested;
            var captureRequestId=owned.LocalCaptureRequestId;
            if(captureRequestId!=lastLocalCaptureRequestId){lastLocalCaptureRequestId=captureRequestId;localCaptureSequence=0;captureCadence.Reset();}
            var liveCapturing = owned.LivePreview.IsLocalCaptureActive;
            if (liveCapturing) passiveCaptureSuspended = true;
            else if (passiveCaptureSuspended) { passiveCaptureSuspended = false; captureCadence.Reset(); }
            if (!liveCapturing && captureCadence.ShouldCapture(captureRequested, owned.LocalCaptureRefreshSeconds, owned.LocalCaptureMaxWidth, owned.LocalCaptureMaxHeight))
            {
                CancellationTokenSource? capture;
                lock (previewDemandGate)
                    capture = Volatile.Read(ref liveCaptureAllowed) != 0 ? CancellationTokenSource.CreateLinkedTokenSource(token, previewCaptureCycle.Token) : null;
                if (capture is not null)
                {
                    using (capture)
                    {
                        try
                        {
                            var captureSequence=captureRequestId is null?(long?)null:checked(++localCaptureSequence);
                            var frame = await captureFrame(capture.Token, owned.LocalCaptureMaxWidth, owned.LocalCaptureMaxHeight);
                            // A slow request begun before the live stream must not overwrite its cache.
                            if (!capture.IsCancellationRequested && !owned.LivePreview.IsLocalCaptureActive && owned.LocalCaptureRequestId==captureRequestId && sourceState==RefreshLocalSourceStatus())
                                owned.SetLocalPreview(BindCapturedPreview(frame with{RequestId=captureRequestId,SourceStatus=sourceState,CaptureSequence=captureSequence}));
                            else if (owned.LivePreview.IsLocalCaptureActive) passiveCaptureSuspended = true;
                        }
                        catch (OperationCanceledException) when (capture.IsCancellationRequested && !token.IsCancellationRequested) { }
                    }
                }
            }
            else if (!liveCapturing && !captureRequested) { InvalidateCapturedPreview(); owned.SetLocalPreview(PreviewFrame.Off("off")); }
            if (!DisableClipboardForTesting) await ExchangeClipboardAsync(owned, token);
            var preview = selected.PreviewEnabled && Volatile.Read(ref liveCaptureAllowed) != 0 && previewVisible ? owned.GetRemotePreview() : PreviewFrame.Off("local-off");
            var message = owned.IsPeerConnected ? "Mac 已连接" : "等待 Mac 协同连接";
            Publish(preview, configurationError ?? message);
        }
        finally { refreshGate.Release(); }
    }

    public async Task SwitchAsync(string platform, CancellationToken token = default)
    {
        if (platform is not ("Windows" or "Mac")) throw new ArgumentException("请选择 Windows 或 Mac。");
        var outcome = await keyboard.SwitchAsync(platform == "Windows" ? "local" : "remote", token);
        if (!outcome.InputTransferred) throw new InvalidOperationException(outcome.Error ?? "键鼠切换尚未完成，请检查共享连接。");
        await RefreshAsync(token);
    }

    public async Task StartSharingAsync(CancellationToken token = default)
    {
        if (CoordinateKeyboardStartAsync is { } coordinate)
        {
            await coordinate(token, () => StartSharingInsideAsync(token));
            return;
        }
        await StartSharingInsideAsync(token);
    }
    private async Task StartSharingInsideAsync(CancellationToken token)
    {
        await ReloadDeviceNamesAsync(token);
        await keyboard.StartAsync(Volatile.Read(ref names), token);
        await RefreshAsync(token);
    }
    public async Task StopSharingAsync(CancellationToken token = default)
    {
        var outcome = await keyboard.StopAsync(token);
        await RefreshAsync(token);
        if (keyboard.IsRunning) throw new InvalidOperationException(outcome.LastError ?? "键鼠共享停止未完成，请再点一次停止。");
    }

    public async Task SetPreviewEnabledAsync(bool enabled, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await previewSettingGate.WaitAsync(token);
        try
        {
            if (!enabled)
            {
                // Revoke immediately, then cover an endpoint being installed concurrently.
                CancellationTokenSource oldCapture;
                lock (previewDemandGate)
                {
                    Volatile.Write(ref liveCaptureAllowed, 0);
                    oldCapture = previewCaptureCycle; previewCaptureCycle = new CancellationTokenSource();
                    server?.SetPreviewRequested(false,requestId:submittedPassiveRequestId);
                    submittedPassiveRequestId=null;passiveRequestId=Guid.NewGuid();
                }
                oldCapture.Cancel(); oldCapture.Dispose();
                server?.LivePreview.SetLocalCaptureProvider(null);
                RefreshLocalSourceStatus();
                StopPreviewSessions("画面已关闭，鼠标控制已停止"); InvalidateCapturedPreview(); server?.SetLocalPreview(PreviewFrame.Off("off"));
                await endpointGate.WaitAsync(CancellationToken.None);
                try { server?.LivePreview.SetLocalCaptureProvider(null); }
                finally { endpointGate.Release(); }
            }
            await SaveAsync(value => value with { PreviewEnabled = enabled }, token);
            if (enabled)
            {
                await endpointGate.WaitAsync(token);
                try { Volatile.Write(ref liveCaptureAllowed, 1); RefreshLocalSourceStatus(); }
                finally { endpointGate.Release(); }
            }
            captureCadence.Reset();
            Publish(PreviewFrame.Off(enabled ? "connecting" : "local-off"), Snapshot.ConnectionMessage);
            await RefreshAsync(token);
        }
        finally { previewSettingGate.Release(); }
    }
    public async Task SetPreviewRefreshSecondsAsync(int seconds, CancellationToken token = default)
    {
        PreviewCaptureCadence.ValidateRefreshSeconds(seconds);
        await SaveAsync(value => value with { PreviewRefreshSeconds = seconds }, token);
        UpdatePreviewDemand(server);
        Publish(Snapshot.Preview, Snapshot.ConnectionMessage);
        await RefreshAsync(token);
    }
    public void SetPreviewVisible(bool visible, bool highResolution = false)
    {
        lock (previewDemandGate)
        {
            if(visible&&(!previewVisible||this.highResolution!=highResolution))passiveRequestId=Guid.NewGuid();
            previewVisible = visible; this.highResolution = highResolution;
        }
        UpdatePreviewDemand(server);
        if (!visible) { StopPreviewSessions("预览已隐藏，鼠标控制已停止"); InvalidateCapturedPreview(); Publish(PreviewFrame.Off("paused"), Snapshot.ConnectionMessage); }
    }
    private void UpdatePreviewDemand(CollaborationServer? endpoint)
    {
        if (endpoint is null) return;
        lock (previewDemandGate)
        {
            if(!ReferenceEquals(passiveDemandEndpoint,endpoint))
            {passiveDemandEndpoint=endpoint;submittedPassiveRequestId=null;passiveRequestId=Guid.NewGuid();}
            var wanted=previewVisible && settings.PreviewEnabled && Volatile.Read(ref liveCaptureAllowed) != 0 && localSource?.Availability=="enabled";
            if(!wanted)
            {
                endpoint.SetPreviewRequested(false,requestId:submittedPassiveRequestId);
                if(submittedPassiveRequestId is not null)passiveRequestId=Guid.NewGuid();
                submittedPassiveRequestId=null;return;
            }
            var request=passivePeerSupported?passiveRequestId:(Guid?)null;
            if(request is not null&&submittedPassiveRequestId==request&&endpoint.RemoteCaptureRequestId!=request)
                request=passiveRequestId=Guid.NewGuid();
            endpoint.SetPreviewRequested(true,highResolution?1920:960,highResolution?1080:540,EffectivePreviewRefreshSeconds,request);
            submittedPassiveRequestId=request;
        }
    }
    public async Task SetClipboardEnabledAsync(bool enabled, CancellationToken token = default)
    {
        await SaveAsync(value => value with { ClipboardEnabled = enabled }, token);
        Interlocked.Exchange(ref clipboardLifetime, new CancellationTokenSource()).Cancel();
        cursor.ShouldPublish(clipboard.Sequence, false); clipboardError = null;
        server?.SetLocalClipboardEnabled(enabled);
        Publish(Snapshot.Preview, Snapshot.ConnectionMessage);
    }
    private async Task SaveAsync(Func<WindowsCollaborationSettings, WindowsCollaborationSettings> change, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await settingsGate.WaitAsync(token);
        try
        {
            var next = change(settings).Validate();
            await context.Store.SaveAsync("collaboration.json", next, token);
            settings = next; configurationError = null;
        }
        finally { settingsGate.Release(); }
    }
    private void Publish(PreviewFrame frame, string message)
    {
        if(frame.State is "offline" or "disconnected" or "error") {StopPreviewSessions("画面连接中断，鼠标控制已停止",preserveLive:true);InvalidateCapturedPreview();}
        else if(frame.State!="live")StopPreviewSessions("没有可操作画面，鼠标控制已停止",activeOnly:true,preserveLive:true);
        var keyboardState = keyboard.Snapshot;
        var session = keyboardState.Session;
        var input = session.ActiveComputer;
        var currentNames = Volatile.Read(ref names);
        var active = input == currentNames.LocalName ? "Windows" : input == currentNames.RemoteName ? "Mac" : "";
        var running = keyboard.IsRunning;
        var ready = running && session.ProcessState == DeskLinkProcessState.Running && session.NativeControlAvailable;
        var peerConnected = ready && session.ConnectedComputers.Contains(currentNames.RemoteName, StringComparer.Ordinal);
        var next = new MiniCollaborationSnapshot
        {
            Connected = server?.IsPeerConnected == true, ConnectionMessage = message, ActiveComputer = active,
            KeyboardSharingRunning = running, KeyboardSharingConnected = peerConnected,
            KeyboardCanStart = !running && keyboardState.Available, KeyboardCanStop = running,
            KeyboardSharingMessage = running && session.ProcessState == DeskLinkProcessState.Faulted
                ? keyboardState.LastError ?? session.LastError ?? "键鼠共享清理未完成，请重试停止"
                : peerConnected ? "键鼠已连接" : running ? "等待 Mac 启动键鼠共享" : "键鼠共享未启动",
            CanSwitchWindows = ready, CanSwitchMac = peerConnected,
            PreviewEnabled = settings.PreviewEnabled && Volatile.Read(ref liveCaptureAllowed) != 0, PreviewRefreshSeconds = settings.PreviewRefreshSeconds,
            Preview = settings.PreviewEnabled && Volatile.Read(ref liveCaptureAllowed) != 0 && previewVisible ? frame : PreviewFrame.Off("local-off"),
            PassivePreviewRequestId=CurrentPassivePreviewRequest(),RemotePreviewSourceStatus=server?.RemotePreviewSourceStatus,
            ClipboardEnabled = settings.ClipboardEnabled,
            ClipboardMessage = clipboardError ?? (!settings.ClipboardEnabled ? "剪贴板互通已暂停" : server?.RemoteClipboardEnabled == true ? "文字和链接互通中" : "请在 Mac 同时开启剪贴板互通"),
            ReceiveDirectory = settings.ReceiveDirectory, SharedDirectory = settings.SharedDirectory, Transfers = server?.GetTransfers() ?? []
        };
        Volatile.Write(ref snapshot, next); Changed?.Invoke(this, EventArgs.Empty);
    }
    private IPAddress? FindAddress()
    {
        try { return TestAddressProvider is null ? LanAddressSelector.SelectSystemAddress(keyboard.Snapshot.ControlAddress) : TestAddressProvider(); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // An unexpected enumeration error cannot leave the previous network
            // listener active while its address is no longer verifiable.
            LogThrottled(error);
            return null;
        }
    }
    private static IReadOnlyList<SharedDirectory> Shares(WindowsCollaborationSettings value) => value.SharedDirectory.Length == 0 ? [] : [new("windows", Path.GetFileName(value.SharedDirectory.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : "共享资料", value.SharedDirectory)];
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    private void LogThrottled(Exception error) { var now = Environment.TickCount64; if (now - Interlocked.Read(ref loggedAt) < 10000) return; Interlocked.Exchange(ref loggedAt, now); log(error); }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        StopDesktopObservation();
        InvalidateCapturedPreview();await DisposePreviewSessionsAsync();
        lifetime.Cancel(); clipboardLifetime.Cancel(); await loop;
        await refreshGate.WaitAsync();
        try
        {
            await endpointGate.WaitAsync();
            try { if (server is not null) await server.DisposeAsync(); server = null; }
            finally { endpointGate.Release(); }
        }
        finally { refreshGate.Release(); }
        lifetime.Dispose(); clipboardLifetime.Dispose(); previewCaptureCycle.Dispose(); refreshGate.Dispose(); endpointGate.Dispose(); settingsGate.Dispose();
    }
}
