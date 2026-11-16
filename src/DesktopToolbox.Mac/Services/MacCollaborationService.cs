using System.Text.Json;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services.LivePreview;
using Nian.Input.Control;

namespace DesktopToolbox.Mac.Services;

/// <summary>Independent collaboration data channel; keyboard and display switching keep their own lifecycle.</summary>
public sealed partial class MacCollaborationService : IAsyncDisposable
{
#if NIAN_PREVIEW_TEST_HOOKS
    internal Func<Task>? TestBeforeMacCapture;
    internal Func<Task>? TestAfterLiveUploadStopped;
    internal Action? TestLiveUploadAuthorized;
    internal Func<Task>? TestBeforeShareSave;
    internal Func<Task>? TestBeforeClientPublish;
#endif
    private readonly MacPrivateStore store;
    private readonly Func<CancellationToken, Task<DeskLinkPairingBundle?>> getPairing;
    private readonly IMacMediaBridge media;
    private readonly ILivePreviewCaptureProvider liveCapture;
    private readonly int dataPort;
    private readonly bool usePairedDataPort;
    private readonly SemaphoreSlim clientGate = new(1, 1);
    private readonly MacClipboardEchoGuard clipboard = new();
    private readonly PreviewCaptureCadence captureCadence;
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<TransferReceipt> receipts = [];
    private CollaborationClient? client;
    private DeskLinkPairingBundle? paired;
    private Task? loop;
    private Task? receive;
    // Guarded by pollGate together with queue-receive startup and destination changes.
    private int activeSharedDownloads;
    private MacCollaborationSnapshot snapshot = new();
    private bool miniVisible = true;
    private bool detachedVisible;
    private int previewGeneration;
    private int shareGeneration;
    private int preferenceEpoch;
    private int disposed;
    private readonly object disposalGate = new();
    private Task? disposalTask;
    private readonly System.Collections.Concurrent.ConcurrentBag<CancellationTokenSource> retiredCycles = [];
    private string? lastClipboardId;
    private readonly object clipboardRetrySync = new();
    private string? failedClipboardId;
    private int failedClipboardAttempts;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly SemaphoreSlim previewPreferenceGate = new(1, 1);
    private CancellationTokenSource clipboardCycle = new();
    private CancellationTokenSource previewCycle = new();
    private CancellationTokenSource shareCycle = new();
    private CancellationTokenSource? sending;
    private Task journalWrite = Task.CompletedTask;
    public event Action<MacCollaborationSnapshot>? Changed;
    public MacCollaborationSnapshot Snapshot => Volatile.Read(ref snapshot);
    public bool CanPauseSending => Volatile.Read(ref sending) is not null;
    public bool CanRetrySending => Volatile.Read(ref sending) is null && Snapshot.Settings.PendingSend is not null;
    internal MacCollaborationSnapshot UpdateState(Func<MacCollaborationSnapshot, MacCollaborationSnapshot> change)
    {
        while (true)
        {
            var current = Volatile.Read(ref snapshot); var next = change(current);
            if (ReferenceEquals(Interlocked.CompareExchange(ref snapshot, next, current), current)) return next;
        }
    }
    public MacCollaborationService(string dataDirectory, Func<CancellationToken, Task<DeskLinkPairingBundle?>> getPairing, IMacMediaBridge? media = null, int dataPort = 24802, TimeProvider? clock = null)
        :this(dataDirectory,getPairing,media,dataPort,clock,null){}
    public MacCollaborationService(string dataDirectory,Func<CancellationToken,Task<DeskLinkPairingBundle?>> getPairing,IMacMediaBridge? media,int dataPort,TimeProvider? clock,INativePreviewControl? nativeControl)
        :this(dataDirectory,getPairing,media,dataPort,clock,nativeControl,null){}
    public MacCollaborationService(string dataDirectory,Func<CancellationToken,Task<DeskLinkPairingBundle?>> getPairing,IMacMediaBridge? media,int dataPort,TimeProvider? clock,INativePreviewControl? nativeControl,IDeskflowPreviewControl? deskflowPreviewControl)
        :this(dataDirectory,getPairing,media,dataPort,clock,nativeControl,deskflowPreviewControl,null){}
    internal MacCollaborationService(string dataDirectory,Func<CancellationToken,Task<DeskLinkPairingBundle?>> getPairing,IMacMediaBridge? media,int dataPort,TimeProvider? clock,INativePreviewControl? nativeControl,IDeskflowPreviewControl? deskflowPreviewControl,ILivePreviewCaptureProvider? liveCapture,bool usePairedDataPort=false)
    {store=new(dataDirectory);this.getPairing=getPairing;this.media=media??new MacMediaBridge();this.liveCapture=liveCapture??new MacLivePreviewCaptureProvider();this.dataPort=dataPort;this.usePairedDataPort=usePairedDataPort;captureCadence=new(clock);controlClock=clock;this.nativeControl=nativeControl;this.deskflowPreviewControl=deskflowPreviewControl;}
    public static MacCollaborationService CreateForMultiDevice(string directory, Func<CancellationToken, Task<DeskLinkPairingBundle?>> getPairing,
        IDeskflowPreviewControl? previewControl = null) =>
        new(directory, getPairing, null, 24802, null, null, previewControl, null, usePairedDataPort: true);
    public async Task StartAsync(CancellationToken ct = default)
    {
        var settings = await store.ReadAsync<MacCollaborationSettings>("collaboration.json", ct) ?? new();
        settings = settings with { PreviewRefreshSeconds = PreviewCaptureCadence.NormalizeRefreshSeconds(settings.PreviewRefreshSeconds) };
        // Saved authorization cannot prove that a later revocation was written
        // successfully. Multi-device sharing requires fresh consent each session.
        if(usePairedDataPort)settings=settings with{ShareScreenEnabled=false};
        if(!settings.ShareScreenEnabled)
        {
            var disabledCycle=new CancellationTokenSource();disabledCycle.Cancel();
            var prior=Interlocked.Exchange(ref shareCycle,disabledCycle);prior.Cancel();retiredCycles.Add(prior);
        }
        UpdateState(current => current with { Settings = settings }); Publish();
        loop ??= RunAsync(lifetime.Token);
    }
    public void SetViewerVisibility(bool mini, bool detached)
    {
        var previouslyVisible=miniVisible||detachedVisible;miniVisible=mini;detachedVisible=detached;
        if(!previouslyVisible&&(mini||detached))RotatePassiveRequest();
        if(!mini&&!detached){StopPreviewSessions("预览已隐藏，鼠标控制已停止");InvalidateCapturedPreview();}
    }
    // Compatibility for the older combined control: explicitly changes both directions.
    public async Task SetPreviewEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (enabled)
        {
            await SetViewPreviewEnabledAsync(true, ct);
            await SetShareScreenEnabledAsync(true, ct);
            return;
        }
        // Start both revocations before awaiting either side's network cleanup.
        // A stalled peer must not keep the other permission enabled.
        await Task.WhenAll(SetShareScreenEnabledAsync(false, ct), SetViewPreviewEnabledAsync(false, ct));
    }

    public async Task SetViewPreviewEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        int generation;
        await previewPreferenceGate.WaitAsync(ct);
        try
        {
            if (Snapshot.Settings.PreviewEnabled == enabled) return;
            generation=Interlocked.Increment(ref previewGeneration); Interlocked.Increment(ref preferenceEpoch);
            var previousCycle = Interlocked.Exchange(ref previewCycle, new CancellationTokenSource()); previousCycle.Cancel(); retiredCycles.Add(previousCycle);
            UpdateState(current => current with { Settings = current.Settings with { ShareScreenEnabled = current.Settings.ShareScreenEnabled, PreviewEnabled = enabled }, Preview = PreviewFrame.Off(enabled ? "loading" : "off") });
            if(enabled)RotatePassiveRequest(); else StopPreviewSessions("画面已关闭，鼠标控制已停止");
            Publish();
        }
        finally { previewPreferenceGate.Release(); }
        await SaveSettingsAsync(ct);
        if (!enabled && client is{} connection) await SetRemotePreviewRequestAsync(connection,false,generation,ct);
    }

    public async Task SetShareScreenEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        int generation = 0;
        CancellationToken permissionToken = default;
        await previewPreferenceGate.WaitAsync(ct);
        try
        {
            if (Snapshot.Settings.ShareScreenEnabled == enabled && enabled) return;
            captureCadence.Reset();
            generation=Interlocked.Increment(ref shareGeneration); Interlocked.Increment(ref preferenceEpoch);
            var replacement=new CancellationTokenSource();if(!enabled)replacement.Cancel();
            var previousCycle=Interlocked.Exchange(ref shareCycle,replacement); previousCycle.Cancel(); retiredCycles.Add(previousCycle);
            permissionToken=shareCycle.Token;
            UpdateState(current=>current with{Settings=current.Settings with{ShareScreenEnabled=enabled}});
            InvalidateLocalSourceForChoice(enabled);
            if(!enabled)InvalidateCapturedPreview();
            Publish();
        }
        finally { previewPreferenceGate.Release(); }
        // Preference changes and cancellation happen before any network or OS wait.
        if(!enabled)await StopLiveUploadIfSharingStillDisabledAsync(generation);
#if NIAN_PREVIEW_TEST_HOOKS
        if(TestBeforeShareSave is{} beforeSave)await beforeSave();
#endif
        await SaveSettingsAsync(ct);
        if(generation!=Volatile.Read(ref shareGeneration))return;
        if(client is{} connection)
        {
            if(Snapshot.PassivePreviewGenerationSupported)await PublishLocalSourceStatusAsync(connection,ct);
            if(!enabled)await PublishRemotePreviewAsync(connection,PreviewFrame.Off(),generation,ct);
        }
        // Never hold the preference gate while the OS permission prompt is open:
        // the user must be able to revoke sharing immediately.
        if(enabled)
        {
            using var permissionCycle=CancellationTokenSource.CreateLinkedTokenSource(ct,permissionToken,lifetime.Token);
            using var permission=await media.RequestAsync(new { action="permission" },permissionCycle.Token);
            permissionCycle.Token.ThrowIfCancellationRequested();
            if(generation==Volatile.Read(ref shareGeneration)&&Snapshot.Settings.ShareScreenEnabled&&permission.RootElement.GetProperty("status").GetString()=="permissionRequired")
                SetStatus("请在Mac系统设置允许屏幕录制；Windows画面仍可查看");
        }
    }
    public async Task SetPreviewRefreshSecondsAsync(int seconds, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        PreviewCaptureCadence.ValidateRefreshSeconds(seconds);
        UpdateState(current => current with { Settings = current.Settings with { PreviewRefreshSeconds = seconds } }); Publish();
        await SaveSettingsAsync(ct);
    }
    public async Task SetClipboardEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Interlocked.Increment(ref preferenceEpoch);
        var previousCycle = Interlocked.Exchange(ref clipboardCycle, new CancellationTokenSource()); previousCycle.Cancel(); retiredCycles.Add(previousCycle);
        clipboard.Reset(); lastClipboardId = null; ResetClipboardRetries();
        UpdateState(current => current with { Settings = current.Settings with { ClipboardEnabled = enabled }, ClipboardError = null }); Publish();
        await SaveSettingsAsync(ct);
        if (client is not null) await client.SetClipboardEnabledAsync(enabled, ct);
    }
    public async Task SetPinnedAsync(bool pinned, CancellationToken ct = default)
    { UpdateState(current => current with { Settings = current.Settings with { Pinned = pinned } }); Publish(); await SaveSettingsAsync(ct); }
    public async Task SetPlacementAsync(int x, int y, string edge, CancellationToken ct = default)
    { UpdateState(current => current with { Settings = current.Settings with { WindowX = x, WindowY = y, DockEdge = edge } }); await SaveSettingsAsync(ct); }
    public async Task SetReceiveDirectoryAsync(string path, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new IOException("请选择存在的收件文件夹。");
        // Starting a receive and changing its destination must be serialized. A transfer already
        // underway continues to its original destination; do not make the UI show a new one yet.
        await pollGate.WaitAsync(ct);
        try
        {
            if (receive is { IsCompleted: false } || activeSharedDownloads > 0) throw new InvalidOperationException("文件正在接收，请等待接收完成后再更换文件夹。");
            await settingsGate.WaitAsync(ct);
            try
            {
                var next = Snapshot.Settings with { ReceiveDirectory = path };
                await store.WriteAsync("collaboration.json", next, ct);
                UpdateState(current => current with { Settings = current.Settings with { ReceiveDirectory = path }, ReceiveSetupRequired = false, ReceiveError = null });
            }
            finally { settingsGate.Release(); }
        }
        finally { pollGate.Release(); }
        Publish();
    }
    private async Task SaveSettingsAsync(CancellationToken ct)
    { await settingsGate.WaitAsync(ct); try { await store.WriteAsync("collaboration.json", snapshot.Settings, ct); } finally { settingsGate.Release(); } }
    public Task SendFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => SendCoreAsync(paths, null, ct);
    public Task RetrySendAsync(CancellationToken ct = default)
    {
        var pending = snapshot.Settings.PendingSend ?? throw new InvalidOperationException("当前没有待重试的发送。");
        return SendCoreAsync(pending.Paths, pending.Id.Length == 0 ? null : pending.Id, ct);
    }
    public void PauseSending() => sending?.Cancel();
    private async Task SendCoreAsync(IReadOnlyList<string> paths, string? transferId, CancellationToken ct)
    {
        if (!await sendGate.WaitAsync(0, ct)) throw new InvalidOperationException("已有文件正在发送，请等待或先暂停。");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); sending = operation;
        try
        {
            var connection = await RequireClientAsync(operation.Token);
            UpdateState(current => current with { Settings = current.Settings with { PendingSend = new(transferId ?? "", paths.ToArray()) } });
            await SaveSettingsAsync(operation.Token); SetStatus("正在准备发送到Windows");
            var receipt = transferId is null ? await connection.SendFilesAsync(paths, Progress(trackSend: true), operation.Token)
                : await connection.ResumeSendFilesAsync(paths, transferId, Progress(trackSend: true), operation.Token);
            await journalWrite;
            AddReceipt(receipt); UpdateState(current => current with { Settings = current.Settings with { PendingSend = null } });
            await SaveSettingsAsync(lifetime.Token); SetStatus("已发送到Windows，原文件保留");
        }
        catch (Exception error) { await journalWrite; SetError(error is OperationCanceledException ? "发送已暂停，可继续上次发送" : "发送未完成，可重试：" + error.Message); throw; }
        finally { sending = null; sendGate.Release(); }
    }
    public async Task<SharedListing> ListSharedAsync(string? root = null, string relativePath = "", CancellationToken ct = default) => await (await RequireClientAsync(ct)).ListSharedAsync(root, relativePath, ct);
    public async Task<SharedPreview> PreviewSharedAsync(SharedItem item, CancellationToken ct = default) => await (await RequireClientAsync(ct)).GetSharedPreviewAsync(item.RootId, item.RelativePath, ct);
    public async Task DownloadSharedAsync(SharedItem item, CancellationToken ct = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        string folder;
        await pollGate.WaitAsync(operation.Token);
        try
        {
            folder = snapshot.Settings.ReceiveDirectory;
            if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("请先在文件传输中选择Mac收件文件夹。");
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Mac 收件文件夹不可用，请重新选择。");
            activeSharedDownloads++;
        }
        finally { pollGate.Release(); }
        try
        {
            var receipt = await (await RequireClientAsync(operation.Token)).DownloadSharedAsync(item.RootId, item.RelativePath, folder, Progress(), operation.Token);
            AddReceipt(receipt); SetStatus("已保存到Mac，可离线使用");
        }
        finally
        {
            // Cleanup must run even when the caller or app is being cancelled.
            await pollGate.WaitAsync();
            try { activeSharedDownloads--; }
            finally { pollGate.Release(); }
        }
    }
    private IProgress<TransferProgress> Progress(bool trackSend = false) => new InlineProgress(value =>
    {
        UpdateState(current => current with { Transfer = value });
        if (trackSend && snapshot.Settings.PendingSend is { } pending && pending.Id != value.Id)
        {
            UpdateState(current => current with { Settings = current.Settings with { PendingSend = pending with { Id = value.Id } } });
            journalWrite = SaveSettingsAsync(lifetime.Token);
        }
        Publish();
    });
    private sealed class InlineProgress(Action<TransferProgress> action) : IProgress<TransferProgress> { public void Report(TransferProgress value) => action(value); }
    private async Task<CollaborationClient> RequireClientAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var bundle = await getPairing(ct);
        await clientGate.WaitAsync(ct);
        try
        {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if(bundle is null)
        {
            await StopLiveUploadAsync();StopPreviewSessions("配对已移除，鼠标控制已停止");InvalidateCapturedPreview();
            if(client is not null){await client.DisposeAsync();client=null;paired=null;}
            Volatile.Write(ref remoteSupportsLivePreview,false);
            UpdateState(current=>current with{PassivePreviewGenerationSupported=false,PassivePreviewRequestId=null,RemotePreviewSourceStatus=null});
            throw new InvalidOperationException("请先在连接设置导入并确认Windows配对。");
        }
        if (client is null || !ReferenceEquals(paired, bundle))
        {
            if (client is not null) {await StopLiveUploadAsync();StopPreviewSessions("配对已变化，鼠标控制已停止");InvalidateCapturedPreview();await client.DisposeAsync();}
#if NIAN_PREVIEW_TEST_HOOKS
            if (TestBeforeClientPublish is {} beforePublish) await beforePublish();
#endif
            Volatile.Write(ref remoteSupportsLivePreview,false);
            UpdateState(current=>current with{PassivePreviewGenerationSupported=false,PassivePreviewRequestId=null,RemotePreviewSourceStatus=null});
            var replacement = new CollaborationClient(bundle, userApproved: true, usePairedDataPort || bundle.Version==2 ? bundle.DataPort : dataPort);
            var accepted = false;
            lock (disposalGate)
            {
                if (Volatile.Read(ref disposed) == 0)
                {
                    paired = bundle; client = replacement; accepted = true;
                }
            }
            if (!accepted)
            {
                await replacement.DisposeAsync();
                throw new ObjectDisposedException(nameof(MacCollaborationService));
            }
            clipboard.Reset(); lastClipboardId = null; ResetClipboardRetries();
            captureCadence.Reset();
        }
        return client ?? throw new InvalidOperationException("协作连接尚未准备好。");
        }
        finally { clientGate.Release(); }
    }
    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var epoch = Volatile.Read(ref preferenceEpoch);
            try
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct); tick.CancelAfter(TimeSpan.FromSeconds(5));
                await PollAsync(tick.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException) when (epoch != Volatile.Read(ref preferenceEpoch)) { /* A manual choice canceled its old media request; the LAN remains healthy. */ }
            catch (Exception error)
            {
                HandleOrdinaryPollingFailure(error);
            }
            try { await Task.Delay(750, ct); } catch (OperationCanceledException) { break; }
        }
    }
    internal Task RefreshOnceAsync(CancellationToken ct = default) => PollAsync(ct);
    internal void HandleOrdinaryPollingFailure(Exception error)
    {
        captureCadence.Reset();
        var protectedLive=HasProtectedLiveSession();
        StopPreviewSessions("画面连接中断，鼠标控制已停止",preserveProtectedLive:true);InvalidateCapturedPreview();
        UpdateState(current => current with { IsOnline = protectedLive&&current.IsOnline, Preview = protectedLive?current.Preview:PreviewFrame.Off("offline"), RemoteClipboardEnabled = false, ReceiveSetupRequired = false, Error = "Windows协同服务暂不可用：" + error.Message });
        Publish();
    }
    private async Task PollAsync(CancellationToken ct)
    {
        await pollGate.WaitAsync(ct);
        try
        {
                var connection = await RequireClientAsync(ct);
                var state = await connection.GetStatusAsync(ct,supportsLivePreview:liveCapture.IsSupported,supportsPassivePreviewGeneration:true);
                // A response from a replaced pairing must not publish capabilities or start capture.
                if(!ReferenceEquals(connection,Volatile.Read(ref client)))return;
                Volatile.Write(ref remoteSupportsLivePreview,state.LivePreviewSupported);
                var wasOnline=Snapshot.IsOnline;
                ApplyRemoteSourceStatus(state);
                if(!wasOnline&&state.PassivePreviewGenerationSupported)RotatePassiveRequest();
                await RefreshLocalSourceStatusAsync(connection,state.PassivePreviewGenerationSupported,ct);
                await UpdateLiveUploadAsync(connection,state.RequestedLivePreview,ct);
                UpdateState(current => current with { IsOnline = true, RemoteClipboardEnabled = state.RemoteClipboardEnabled,
                    ReceiveSetupRequired = state.PendingTransfers.Count > 0 && string.IsNullOrWhiteSpace(current.Settings.ReceiveDirectory),
                    ReceiveError = state.PendingTransfers.Count == 0 ? null : current.ReceiveError, Error = null });
                var generation = previewGeneration;
                var viewing = snapshot.Settings.PreviewEnabled;
                var sharing = snapshot.Settings.ShareScreenEnabled;
                var sharingGeneration = Volatile.Read(ref shareGeneration);
                var subscribe = MacPreviewPresentation.ShouldSubscribe(viewing, miniVisible, detachedVisible, true);
                await SetRemotePreviewRequestAsync(connection,subscribe,generation,ct,detachedVisible ? 1920 : 960,detachedVisible ? 1080 : 540,EffectivePreviewRefreshSeconds);
                if (subscribe&&!HasProtectedLiveSession())
                {
                    var viewerRevision=Interlocked.Read(ref liveViewerRevision);
                    using var viewerCycle = CancellationTokenSource.CreateLinkedTokenSource(ct, previewCycle.Token);
                    var frame = await connection.GetPreviewAsync(viewerCycle.Token);
                    TryApplyPolledPreview(frame,generation,viewerRevision);
                }
                else if(!subscribe)UpdateState(current => current with { Preview = PreviewFrame.Off() });
                if(!state.CaptureRequested||!sharing)InvalidateCapturedPreview();
                var localSource=LocalSourceStatus;
#if NIAN_PREVIEW_TEST_HOOKS
                if(TestBeforeMacCapture is{} beforeCapture)await beforeCapture();
#endif
                if (captureCadence.ShouldCapture(state.CaptureRequested && sharing && state.RequestedLivePreview is null&&(!state.PassivePreviewGenerationSupported||localSource.Availability=="enabled"), state.PreviewRefreshSeconds, state.CaptureMaxWidth, state.CaptureMaxHeight))
                {
                    PreviewFrame frame;
                    CancellationTokenSource? captureCycle=null;
                    Task<MacScreenCaptureReply>? captureTask=null;
                    // Starting a capture and revoking its authorization share
                    // this short gate. Never hold it while awaiting the frame.
                    await previewPreferenceGate.WaitAsync(ct);
                    try
                    {
                        if(sharingGeneration==Volatile.Read(ref shareGeneration)&&Snapshot.Settings.ShareScreenEnabled&&!shareCycle.IsCancellationRequested)
                        {
                            captureCycle=CancellationTokenSource.CreateLinkedTokenSource(ct,shareCycle.Token);
                            captureCycle.Token.ThrowIfCancellationRequested();
                            captureTask=media.CaptureAsync(state.CaptureMaxWidth,state.CaptureMaxHeight,cancellationToken:captureCycle.Token);
                        }
                    }
                    finally{previewPreferenceGate.Release();}
                    if(captureTask is not null)
                    {
                    using(captureCycle)
                    {
                        var sequence=state.PassivePreviewGenerationSupported?NextCaptureSequence(state.CaptureRequestId,localSource):null;
                        var native = await captureTask;
                        frame = native.IsFrame ? new PreviewFrame { State = "live", Width = native.Width, Height = native.Height, JpegBytes = native.JpegBytes!, CapturedAtUtc = DateTimeOffset.UtcNow,Geometry=native.Geometry }
                            : PreviewFrame.Off(native.Status switch { "permissionRequired" => "permission", "locked" => "locked", _ => "error" }, native.Message);
                    if (sharingGeneration != Volatile.Read(ref shareGeneration) || !Snapshot.Settings.ShareScreenEnabled) frame = PreviewFrame.Off();
                    if(state.PassivePreviewGenerationSupported)
                    {
                        if(!Equals(localSource,LocalSourceStatus))frame=PreviewFrame.Off("waiting");
                        frame=frame with{RequestId=state.CaptureRequestId,SourceStatus=localSource,CaptureSequence=sequence};
                    }
                    frame=BindCapturedPreview(frame);
                    await PublishRemotePreviewAsync(connection,frame,sharingGeneration,ct);
                    }
                    }
                }
                else if (state.CaptureRequested && !sharing) await PublishRemotePreviewAsync(connection,PreviewFrame.Off("off", "Mac已关闭实时画面，请在Mac开启"),sharingGeneration,ct);
                await connection.SetClipboardEnabledAsync(snapshot.Settings.ClipboardEnabled, ct);
                if (snapshot.Settings.ClipboardEnabled && state.RemoteClipboardEnabled)
                {
                    try { await SyncClipboardAsync(connection, ct); }
                    catch (MacClipboardWriteException error) { UpdateState(current => current with { ClipboardError = error.Message }); }
                }
                else { clipboard.Reset(); ResetClipboardRetries(); UpdateState(current => current with { ClipboardError = null }); }
                if (state.PendingTransfers.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(snapshot.Settings.ReceiveDirectory)) SetStatus("Windows有待接收文件，请先选择Mac收件文件夹");
                    else if (receive is null || receive.IsCompleted) receive = ReceiveAsync(connection, lifetime.Token);
                }
                Publish();
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw;}
        catch{StopPreviewSessions("画面连接中断，鼠标控制已停止",preserveProtectedLive:true);InvalidateCapturedPreview();throw;}
        finally{pollGate.Release();}
    }
    private async Task ReceiveAsync(CollaborationClient connection, CancellationToken ct)
    {
        var folder = snapshot.Settings.ReceiveDirectory;
        try
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("选择的 Mac 收件文件夹不存在。");
            var received = await connection.ReceivePendingAsync(folder, Progress(), ct);
            foreach (var receipt in received) AddReceipt(receipt);
            if (received.Count > 0) { UpdateState(current => current with { ReceiveError = null }); SetStatus("文件已收到Mac，可离线使用"); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            UpdateState(current => current.Settings.ReceiveDirectory == folder
                ? current with { ReceiveError = "接收未完成，请检查 Mac 收件文件夹、磁盘空间和网络，或重新选择文件夹。" }
                : current);
            Publish();
        }
    }
    private async Task SyncClipboardAsync(CollaborationClient connection, CancellationToken ct)
    {
        const string oversizedMessage = "文字过长，请分段复制（单次最多 256 KB）。";
        using var enabledCycle = CancellationTokenSource.CreateLinkedTokenSource(ct, clipboardCycle.Token);
        ct = enabledCycle.Token;
        if (!snapshot.Settings.ClipboardEnabled) return;
        int observedChangeCount;
        using (var native = await media.RequestAsync(new { action = "clipboardRead" }, ct))
        {
            var root = native.RootElement;
            if (!root.TryGetProperty("changeCount", out var count)) return;
            observedChangeCount = count.GetInt32();
            // Older native helpers may return an image; never decode it or turn
            // its incidental text metadata into a text copy.
            var hasLegacyImage = root.TryGetProperty("png", out var p) && p.ValueKind == JsonValueKind.String;
            var oversized = root.TryGetProperty("oversized", out var size) && size.ValueKind == JsonValueKind.True;
            string? text = !hasLegacyImage && root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var action = clipboard.Observe(count.GetInt32(), text);
            if (action == MacClipboardAction.Clear && snapshot.Settings.ClipboardEnabled)
            {
                await connection.ClearClipboardAsync(ct); clipboard.Commit(observedChangeCount);
                UpdateState(current => current with { ClipboardError = oversized ? oversizedMessage : current.ClipboardError == oversizedMessage ? null : current.ClipboardError });
                return;
            }
            if (oversized)
                UpdateState(current => current with { ClipboardError = oversizedMessage });
            if (action == MacClipboardAction.Publish && snapshot.Settings.ClipboardEnabled)
            {
                var payload = new ClipboardPayload(Guid.NewGuid().ToString("N"), "Mac", DateTimeOffset.UtcNow, Text: text);
                await connection.PublishClipboardAsync(payload, ct); clipboard.Commit(observedChangeCount); lastClipboardId = payload.Id;
                ResetClipboardRetries(); UpdateState(current => current with { ClipboardError = null });
            }
        }
        var incoming = await connection.GetLatestClipboardAsync(ct);
        if (incoming is null || incoming.Id == lastClipboardId || incoming.Origin == "Mac" || !snapshot.Settings.ClipboardEnabled) return;
        // A legacy peer may still send PNG. Remember only its ID, without
        // touching the local clipboard or its change-count baseline.
        if (incoming.Text is null || incoming.PngBytes is not null) { lastClipboardId = incoming.Id; return; }
        if (!CanAttemptClipboardWrite(incoming.Id))
            throw new MacClipboardWriteException("Mac 剪贴板暂时无法写入，请在 Windows 重新复制后再试。");
        using var written = await media.RequestAsync(new { action = "clipboardWrite", expectedChangeCount = observedChangeCount, text = incoming.Text }, ct);
        ct.ThrowIfCancellationRequested();
        var writeRoot = written.RootElement;
        var status = writeRoot.ValueKind == JsonValueKind.Object && writeRoot.TryGetProperty("status", out var writeStatus) && writeStatus.ValueKind == JsonValueKind.String ? writeStatus.GetString() : null;
        if (status == "changed") return; // A fresh local copy wins, and will be observed on the next poll.
        var changed = default(JsonElement);
        var hasCount = writeRoot.ValueKind == JsonValueKind.Object && writeRoot.TryGetProperty("changeCount", out changed) && changed.ValueKind == JsonValueKind.Number && changed.TryGetInt32(out var writeCount) && writeCount >= 0;
        if (status != "written" || !hasCount)
        {
            // The returned generation could belong to a concurrent user copy.
            // Never classify an uncertain change as our own mutation.
            RecordClipboardWriteFailure(incoming.Id);
            throw new MacClipboardWriteException("Mac 剪贴板暂时无法写入，请在 Windows 重新复制后再试。");
        }
        clipboard.MarkRemoteApplied(changed.GetInt32());
        lastClipboardId = incoming.Id; ResetClipboardRetries(); UpdateState(current => current with { ClipboardError = null });
    }
    private sealed class MacClipboardWriteException(string message) : IOException(message);
    private bool CanAttemptClipboardWrite(string id)
    {
        lock (clipboardRetrySync)
        {
            if (failedClipboardId != id) { failedClipboardId = id; failedClipboardAttempts = 0; }
            return failedClipboardAttempts < 3;
        }
    }
    private void RecordClipboardWriteFailure(string id)
    {
        lock (clipboardRetrySync)
            if (failedClipboardId == id) failedClipboardAttempts++;
    }
    private void ResetClipboardRetries() { lock (clipboardRetrySync) { failedClipboardId = null; failedClipboardAttempts = 0; } }
    private void AddReceipt(TransferReceipt receipt) { lock (receipts) { receipts.Insert(0, receipt); if (receipts.Count > 30) receipts.RemoveAt(30); UpdateState(current => current with { Receipts = receipts.ToArray() }); } Publish(); }
    private void SetStatus(string status) { UpdateState(current => current with { Status = status, Error = null }); Publish(); }
    private void SetError(string error) { UpdateState(current => current with { Error = error }); Publish(); }
    private void Publish() => Changed?.Invoke(snapshot);
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (disposalGate)
        {
            if (disposalTask is not null) return new ValueTask(disposalTask);
            Volatile.Write(ref disposed, 1);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposalTask = completion.Task;
        }

        // Publish the task before executing cleanup, and never execute a cleanup
        // callback under disposalGate. Every caller observes the same outcome.
        _ = CompleteDisposalAsync(completion);
        return new ValueTask(completion.Task);
    }
    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try { await DisposeCoreAsync(); completion.TrySetResult(); }
        catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
    private async Task DisposeCoreAsync()
    {
        // Stop admitting background work before waiting for preview cleanup. The
        // preview's return command uses its own deadline and still needs client.
        var cancellation = lifetime.CancelAsync();
        InvalidateCapturedPreview();
        // Cancellation callbacks may block or throw. Start the preview return
        // independently, then observe both before continuing cleanup.
        await Task.WhenAll(cancellation, DisposePreviewSessionsAsync());
        clipboardCycle.Cancel(); previewCycle.Cancel(); shareCycle.Cancel();
        await StopLiveUploadAsync();
        if (loop is not null) await loop;
        if (receive is not null) await receive;
        if (client is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await client.SetPreviewRequestedAsync(false, deadline.Token); await client.PublishPreviewAsync(PreviewFrame.Off(), deadline.Token); await client.SetClipboardEnabledAsync(false, deadline.Token); } catch { }
            await client.DisposeAsync();
        }
        await sendGate.WaitAsync(); sendGate.Release();
        await media.DisposeAsync(); clipboardCycle.Dispose(); previewCycle.Dispose(); shareCycle.Dispose();
        foreach (var retired in retiredCycles) retired.Dispose();
        lifetime.Dispose();
    }
}

