using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using DesktopToolbox.Core;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>The toolbox owns the core and the only display coordinator; the original GUI remains a rollback path.</summary>
public sealed partial class IntegratedDeskSwitchService : IIntegratedDeskSwitchService, IDeskflowPreviewControl
{
    private readonly ModuleContext context;
    private readonly DeskLinkRuntimePackage package;
    private readonly IDisplayBridge bridge;
    private readonly string root;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource? focusLifetime = new();
    private readonly object sessionStateGate = new();
    private DeskflowSession? session;
    private bool acceptSessionUpdates;
    private UnifiedSwitchCoordinator? coordinator;
    private PairedControlServer? control;
    private DeskSwitchSettings? settings;
    private DeskLinkState state = new();
    private string? controlAddress;
    private int? controlPort;
    private string? controlFingerprint;
    private DeskLinkSwitchResult? lastSwitch;
    private string? lastError;
    private int disposed;
    private int stopping;
    private bool cleanupPending;

    public IntegratedDeskSwitchService(ModuleContext context, string packageDirectory, IDisplayBridge bridge)
    {
        this.context = context; this.bridge = bridge;
        package = new(packageDirectory);
        root = context.Paths.Resolve("desk-link");
    }

    private DeskLinkState CurrentSessionState
    {
        get
        {
            DeskflowSession? owner;
            lock (sessionStateGate) { owner = acceptSessionUpdates ? session : null; if (owner is null) return state; }
            var authoritative = owner.CurrentState;
            lock (sessionStateGate)
            {
                if (acceptSessionUpdates && ReferenceEquals(owner, session) && authoritative.SessionStateVersion > state.SessionStateVersion)
                    state = authoritative;
                return state;
            }
        }
    }
    public bool IsRunning => cleanupPending || CurrentSessionState.ProcessState is DeskLinkProcessState.Starting or DeskLinkProcessState.Running or DeskLinkProcessState.Stopping;
    public IntegratedDeskSwitchStatus Snapshot
    {
        get
        {
            var current = CurrentSessionState;
            return new() { Available = package.IsAvailable, Session = coordinator?.DecorateState(current) ?? current, LastSwitch = lastSwitch,
                ControlAddress = controlAddress, ControlPort = controlPort, ControlFingerprint = controlFingerprint, LastError = lastError };
        }
    }

    public async Task<IntegratedDeskSwitchStatus> StartAsync(DeskSwitchSettings next, CancellationToken token)
    {
        next.Validate();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await lifecycle.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (IsRunning) return Snapshot;
            // The standalone product has separate credentials and must never take
            // over the toolbox's existing listeners. Also check the data listener
            // before launching the keyboard process, so a conflict leaves no core.
            if (!PortOwnershipPolicy.IsSafe(TcpListenerOwners.ReadAll(), checked((uint)Environment.ProcessId)))
                throw new InvalidOperationException("互联端口正在使用。请先停止另一套电脑互联，再启动本软件。");
            var otherEngines = Process.GetProcessesByName("deskflow-core");
            try { if (otherEngines.Length != 0) throw new InvalidOperationException("已有键鼠共享引擎运行。请先停止另一套电脑互联，再启动本软件。"); }
            finally { foreach (var process in otherEngines) process.Dispose(); }
            EnsureOriginalGuiExited();
            if (!package.IsAvailable) throw new InvalidOperationException("集成共享引擎尚未随包就绪，请使用完整的 0.7.1 版本。");
            await ReleaseComponentsAsync();
            lastError = null; lastSwitch = null;
            Volatile.Write(ref previewPeerObservedAt, 0);
            settings = next;
            DeskLinkRuntimePackage.EnsureOrdinaryPath(root);
            Directory.CreateDirectory(root);
            var engine = await package.PrepareAsync(Path.Combine(root, "engine-0.7"), token);
            var profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(profile);
            await MigrateExistingTrustAsync(profile, token);
            var address = FindLanAddress();
            var owned = new DeskflowSession(new()
            {
                EnginePath = engine, ProfileDirectory = profile, Role = DeskLinkRole.Server,
                LocalName = next.LocalName, RemoteName = next.RemoteName,
                // The collaboration bridge provides an immediate two-sided pause without restarting the keyboard core.
                UseNativeControl = true, ClipboardEnabled = false,
                CertificatePath = Path.Combine(profile, "tls", "deskflow.pem")
            });
            lock (sessionStateGate) { session = owned; state = new(); acceptSessionUpdates = true; }
            var handoff = new UnifiedSwitchCoordinator(new DeskflowKeyboardAdapter(owned),
                next.LinkDisplay ? new UnifiedDisplayAdapter(bridge, context.Paths.Resolve("desk-switch.json"), next) : new DisabledDisplay(),
                next.LocalName, next.RemoteName, next.LinkDisplay);
            coordinator = handoff;
            focusLifetime = new();
            Volatile.Write(ref stopping, 0);
            owned.StateChanged += OnState;
            owned.FocusChanged += OnFocus;
            handoff.ResultChanged += OnResult;
            await owned.StartAsync(token);
            ApplySessionState(owned, await owned.GetStateAsync(token));
            control = new(new()
            {
                ProfileDirectory = Path.Combine(root, "control"), BindAddress = address,
                AllowedTargets = [next.LocalName, next.RemoteName], Port = 24801
            }, async requestToken => handoff.DecorateState(await owned.GetStateAsync(requestToken)),
                (computer, requestToken) => SwitchPairedAsync(handoff, computer, requestToken), new RemotePreviewEndpoint(this),
                () => Volatile.Write(ref previewPeerObservedAt, System.Diagnostics.Stopwatch.GetTimestamp()));
            control.PeerError += (_, error) => { lastError = "Mac 联动通道：" + error.Message; Publish(); };
            await control.StartAsync(token);
            controlPort = control.Port;
            controlFingerprint = control.CertificateSha256;
            controlAddress = address.ToString();
            previewWatch = WatchPreviewExpiryAsync(handoff, focusLifetime.Token);
            Publish();
            return Snapshot;
        }
        catch
        {
            await ReleaseComponentsAsync();
            throw;
        }
        finally { lifecycle.Release(); }
    }

    public async Task<IntegratedDeskSwitchStatus> StopAsync(CancellationToken token)
    {
        await lifecycle.WaitAsync(token);
        try { ThrowIfDisposed(); return await StopInsideAsync(); }
        finally { lifecycle.Release(); }
    }

    private async Task<IntegratedDeskSwitchStatus> StopInsideAsync()
    {
        // Report this stop attempt, rather than carrying forward an older peer error.
        lastError = null;
        // Stop accepting hotkey work before recovery. Once cleanup begins, a cancelled
        // WebView request must not strand a live input core or paired endpoint.
        Volatile.Write(ref stopping, 1);
        if (session is not null) session.FocusChanged -= OnFocus;
        try
        {
            if (focusLifetime is not null) await focusLifetime.CancelAsync();

            if (coordinator?.HasInputPreviewIntent == true)
            {
                // Stopping the owned core recovers local input; cleanup must not create a display intent.
                var preview = await coordinator.ReadPreviewAsync(CancellationToken.None);
                if (preview.HasSession)
                {
                    var returned = await coordinator.EndPreviewAsync(preview.Id!, CancellationToken.None);
                    if (returned.HasSession) lastError = returned.Message ?? "键鼠操作尚未确认归还。";
                }
            }
            else if (session is not null && coordinator is not null && settings is not null && CurrentSessionState.NativeControlAvailable)
            {
                var result = await coordinator.SwitchAsync(settings.LocalName, CancellationToken.None);
                if (!result.Ok) lastError = result.Error ?? "键鼠或显示器回切尚未确认。";
                // The Mac polls focus every 750 ms with a 2 s request deadline. Keep
                // state reads available briefly so its active video link can return the display.
                if (control is not null && result.InputTransferred &&
                    string.Equals(result.ActiveComputer, settings.LocalName, StringComparison.OrdinalIgnoreCase))
                    await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
            }
        }
        catch (Exception error) { lastError = "停止前回到 Windows 未完全完成：" + error.Message; }
        finally { await ReleaseComponentsAsync(); }
        Publish();
        return Snapshot;
    }

    public async Task<IntegratedDeskSwitchStatus> RefreshAsync(CancellationToken token)
    {
        await lifecycle.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (session is { } current) ApplySessionState(current, await current.GetStateAsync(token));
            return Snapshot;
        }
        finally { lifecycle.Release(); }
    }
    public async Task<DeskLinkSwitchResult> SwitchAsync(string target, CancellationToken token)
    {
        await lifecycle.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            var current = settings ?? throw new InvalidOperationException("请先开始键鼠共享。");
            var computer = target switch { "local" => current.LocalName, "remote" => current.RemoteName, _ => throw new ArgumentException("只能切到 Windows 或 Mac。") };
            if (coordinator is null || !IsRunning) throw new InvalidOperationException("请先开始键鼠共享。");
            // Peeking must not expire the lease and lose this button's input-only return intent.
            var preview = coordinator.DecorateState(CurrentSessionState).InputPreview;
            if (preview?.HasSession == true && string.Equals(preview.ViewerComputer, computer, StringComparison.Ordinal))
            {
                var returned = await coordinator.EndPreviewAsync(preview.Id!, token);
                var confirmed = returned.Phase == DeskflowPreviewPhase.Viewing && !returned.HasSession;
                lastSwitch = new()
                {
                    Ok = confirmed, InputTransferred = confirmed, Target = computer,
                    ActiveComputer = confirmed ? computer : null, VideoStatus = "disabled",
                    Error = confirmed ? null : returned.Message
                };
                Publish();
                return lastSwitch;
            }
            return await coordinator.SwitchAsync(computer, token);
        }
        finally { lifecycle.Release(); }
    }
    public async Task ConfirmPeerAsync(string fingerprint, CancellationToken token)
    {
        await lifecycle.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (session is null) throw new InvalidOperationException("共享会话尚未开始。");
            await session.ConfirmPeerAsync(fingerprint, token);
        }
        finally { lifecycle.Release(); }
    }
    public async Task RejectPeerAsync(CancellationToken token)
    {
        await lifecycle.WaitAsync(token);
        try { ThrowIfDisposed(); await StopInsideAsync(); }
        finally { lifecycle.Release(); }
    }
    public async Task<string> ExportPairingAsync(CancellationToken token)
    {
        await lifecycle.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (control is null || controlAddress is null || settings is null || !IsRunning)
                throw new InvalidOperationException("请先开始共享，再生成 Mac 配对文件。");
            var bundle = control.CreatePairingBundle(controlAddress, settings.LocalName, settings.RemoteName, DeskLinkDisplayBindingFactory.FromSettings(settings));
            var path = Path.Combine(root, "Mac配对.json");
            DeskLinkRuntimePackage.EnsureOrdinaryPath(path);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, bundle.ToJson(), new UTF8Encoding(false), token);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return path;
        }
        finally { lifecycle.Release(); }
    }
    private async Task<DeskLinkSwitchResult> SwitchPairedAsync(UnifiedSwitchCoordinator handoff, string computer, CancellationToken token)
    {
        bool Refusing() => Volatile.Read(ref stopping) != 0 || Volatile.Read(ref disposed) != 0 || !ReferenceEquals(coordinator, handoff);
        DeskLinkSwitchResult Refused() => new() { Target = computer, ActiveComputer = CurrentSessionState.ActiveComputer,
            Error = "正在停止共享，暂不接受新的键鼠切换。" };
        var focus = focusLifetime;
        if (Refusing() || focus is null) return Refused();
        try
        {
            // This callback must not take lifecycle: Stop keeps state reads alive while
            // awaiting cleanup, and the endpoint awaits these callbacks on shutdown.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, focus.Token);
            if (Refusing()) return Refused();
            return await handoff.SwitchAsync(computer, linked.Token);
        }
        catch (OperationCanceledException) when (Refusing()) { return Refused(); }
        catch (ObjectDisposedException) when (Refusing()) { return Refused(); }
    }
    private sealed class DisabledDisplay : IUnifiedDisplayControl
    {
        public Task<DisplaySwitchOutcome> ApplyAsync(string computer, CancellationToken token) => Task.FromResult(new DisplaySwitchOutcome("disabled"));
    }
    // An awaited read and a StateChanged callback may complete out of order.
    // Compare owner and version in one short gate; observers run outside it.
    private bool ApplySessionState(DeskflowSession owner, DeskLinkState value)
    {
        lock (sessionStateGate)
        {
            if (!acceptSessionUpdates || !ReferenceEquals(owner, session) || value.SessionStateVersion < state.SessionStateVersion) return false;
            state = value;
            return true;
        }
    }
    private void OnState(object? sender, DeskLinkState value)
    { if (sender is DeskflowSession owner && ApplySessionState(owner, value)) Publish(); }
    private void OnResult(object? sender, DeskLinkSwitchResult value)
    {
        if (!ReferenceEquals(sender, coordinator)) return;
        lastSwitch = value; Publish();
    }
    private async void OnFocus(object? sender, FocusChangedEvent change)
    {
        var handoff = coordinator;
        var focus = focusLifetime;
        if (handoff is null || focus is null || !ReferenceEquals(sender, session) || Volatile.Read(ref disposed) != 0) return;
        try { await handoff.OnFocusChangedAsync(change, focus.Token); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { lastError = "联合切换未完成：" + error.Message; Publish(); }
    }
    private void Publish() => context.Emit("deskSwitch.sessionChanged", Snapshot);

    private async Task MigrateExistingTrustAsync(string profile, CancellationToken token)
    {
        var previous = context.Paths.Resolve("desk-switch");
        var tls = Path.Combine(profile, "tls");
        DeskLinkRuntimePackage.EnsureOrdinaryPath(tls); Directory.CreateDirectory(tls);
        foreach (var pair in new[]
        {
            (Path.Combine(previous,"tls","deskflow.pem"), Path.Combine(tls,"deskflow.pem")),
            (Path.Combine(previous,"engine","settings","tls","trusted-clients"), Path.Combine(tls,"trusted-clients")),
            (Path.Combine(previous,"engine","settings","tls","trusted-servers"), Path.Combine(tls,"trusted-servers"))
        })
        {
            token.ThrowIfCancellationRequested();
            DeskLinkRuntimePackage.EnsureOrdinaryPath(pair.Item1); DeskLinkRuntimePackage.EnsureOrdinaryPath(pair.Item2);
            if (File.Exists(pair.Item2) || !File.Exists(pair.Item1)) continue;
            if (new FileInfo(pair.Item1).Length > 1024 * 1024) throw new InvalidDataException("旧配对资料超出安全范围，已保留原文件。");
            File.Copy(pair.Item1, pair.Item2);
        }
        await Task.CompletedTask;
    }

    private static void EnsureOriginalGuiExited()
    {
        foreach (var name in new[] { "deskflow", "deskflow-core" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
                if (!process.HasExited) throw new InvalidOperationException("请先在原 Deskflow 中正常停止并从“文件 → 退出”，然后在工具箱开始共享。");
        }
    }

    private static IPAddress FindLanAddress()
    {
        return LanAddressSelector.SelectSystemAddress() ??
            throw new InvalidOperationException("未发现可供 Mac 连接的局域网地址，请连接同一局域网后重试。");
    }

    private async Task ReleaseComponentsAsync()
    {
        var focus = focusLifetime; focusLifetime = null;
        var endpoint = control; control = null;
        var handoff = coordinator; coordinator = null;
        DeskflowSession? owned;
        lock (sessionStateGate)
        {
            owned = session; session = null; acceptSessionUpdates = false;
            state = state with { ProcessState = DeskLinkProcessState.Stopping,
                ConnectionState = DeskLinkConnectionState.Disconnected, NativeControlAvailable = false,
                ActiveComputer = null, ConnectedComputers = [] };
        }
        controlAddress = null; controlPort = null; controlFingerprint = null;
        if (owned is not null) { owned.StateChanged -= OnState; owned.FocusChanged -= OnFocus; }
        if (handoff is not null) handoff.ResultChanged -= OnResult;

        // Each cleanup stage still runs if another stage fails. Cancel and await all
        // coordinated display work before stopping the process that owns input focus.
        try { if (focus is not null) await focus.CancelAsync(); }
        catch (Exception error) { lastError = "快捷键切屏取消未完成：" + error.Message; }
        finally { focus?.Dispose(); }
        try { await previewWatch; }
        catch (OperationCanceledException) { }
        catch (Exception error) { lastError = "仅键鼠操作归还检查未完成：" + error.Message; }
        previewWatch = Task.CompletedTask;
        try { if (endpoint is not null) await endpoint.DisposeAsync(); }
        catch (Exception error) { lastError = "配对通道清理未完成：" + error.Message; }
        try
        {
            if (handoff is not null) { await handoff.DisposeAsync(); lastSwitch = handoff.LastResult ?? lastSwitch; }
        }
        catch (Exception error) { lastError = "显示器联动清理未完成：" + error.Message; }
        cleanupPending = false;
        try { if (owned is not null) await owned.DisposeAsync(); }
        catch (Exception error)
        {
            // Keep ownership so Stop can be retried; a failed cleanup must not be
            // advertised as a stopped process or allow a second sharing session.
            lock (sessionStateGate) session = owned;
            cleanupPending = true;
            lastError = "键鼠共享清理未完成：" + error.Message;
        }
        lock (sessionStateGate)
            state = state with { ProcessState = cleanupPending ? DeskLinkProcessState.Faulted : DeskLinkProcessState.Stopped, ConnectionState = DeskLinkConnectionState.Disconnected,
                ActiveComputer = null, ConnectedComputers = [], NativeControlAvailable = false, ProcessId = cleanupPending ? state.ProcessId : null, PendingPeerFingerprint = null };
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifecycle.WaitAsync();
        try { await StopInsideAsync(); }
        finally { lifecycle.Release(); }
        // Queued callers must acquire the gate and observe disposed, rather than race
        // a disposed semaphore. No WaitHandle is allocated, so the gate is GC-only.
    }
}
