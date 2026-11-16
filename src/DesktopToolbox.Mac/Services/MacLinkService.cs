using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services.Display;

namespace DesktopToolbox.Mac.Services;

/// <summary>Owns the Mac input client and requests confirmed Windows focus before local video recovery.</summary>
public sealed partial class MacLinkService : IMacLinkService, IDeskflowPreviewControl
{
    private readonly MacPrivateStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly MacDisplayBridgeProcess displays;
    private readonly MacDisplaySwitchCoordinator displaySwitch;
    private MacConnectionSettings settings = new();
    private readonly object sessionStateGate = new();
    private long snapshotVersion;
    private DeskflowSession? session;
    private bool acceptSessionUpdates;
    private DeskLinkState clientState = new();
    private DeskLinkState? windowsState;
    private DeskLinkPairingBundle? pairing;
    private sealed record ApprovedPairingSnapshot(DeskLinkPairingBundle? Bundle, bool Disposed = false);
    private ApprovedPairingSnapshot? approvedPairingSnapshot;
    private PairedControlClient? control;
    private (PairingPreview Preview, DeskLinkPairingBundle Bundle)? candidate;
    private bool loaded;
    private bool disposed;
    private bool controlReady;
    private bool stopRecoveryNeeded;
    private string controlError = "";
    private string displayMessage = "尚未启用 Mac 本地回切。";
    private bool displayRecoveryError;
    public event Action<MacLinkSnapshot>? Changed;

    public MacLinkService(string dataDirectory)
    {
        store = new(dataDirectory);
        displays = new MacDisplayBridgeProcess();
        displaySwitch = new(async (binding, token) =>
        {
            var result = await displays.SetInputAsync(new(binding.VendorId, binding.ProductId), checked((ushort)binding.WindowsInput), token);
            return new(true, result.Status, result.Error);
        });
    }

    public async Task<DeskLinkPairingBundle?> GetApprovedPairingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var committed = Volatile.Read(ref approvedPairingSnapshot);
        if (committed is not null)
        {
            ObjectDisposedException.ThrowIf(committed.Disposed, this);
            return committed.Bundle;
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed(); await LoadAsync(cancellationToken);
            committed = Volatile.Read(ref approvedPairingSnapshot);
            ObjectDisposedException.ThrowIf(committed?.Disposed == true, this);
            return committed?.Bundle;
        }
        finally { gate.Release(); }
    }

    public async Task<MacLinkSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return Snapshot();
        try
        {
            ThrowIfDisposed(); await LoadAsync(cancellationToken);
            if (session is { } current) ApplySessionState(current, await current.GetStateAsync(cancellationToken));
            // Keep the last authenticated readiness while the bounded status read is
            // in flight. Other UI updates can observe this service concurrently; a
            // temporary false would otherwise cancel an active preview handoff.
            controlError = "";
            if (control is not null)
            {
                try
                {
                    windowsState = await ReadWindowsStateAsync(cancellationToken);
                    controlReady = IsNativeReady(windowsState);
                    if (!controlReady) { controlError = windowsState.LastError ?? "Windows 联合切换接口尚未就绪，请先在 Windows 开始共享。"; displaySwitch.ResetBaseline(); }
                    else
                    {
                        // The authenticated native Windows state remains authoritative even
                        // if this input client has just disconnected during Windows recovery.
                        var result = await displaySwitch.ObserveAsync(windowsState, cancellationToken);
                        if (result is not null) SetDisplayMessage(result);
                    }
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && IsControlError(error))
                { controlReady = false; windowsState = null; displaySwitch.ResetBaseline(); controlError = "无法连接 Windows 联合切换通道：" + error.Message; }
                catch { controlReady = false; throw; }
            }
            else { controlReady = false; windowsState = null; }
            return Publish();
        }
        finally { gate.Release(); }
    }

    public async Task StartAsync(MacConnectionSettings value, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("此客户端请在 Mac 上开始共享。");
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed(); await LoadAsync(cancellationToken);
            if (CurrentClientState.ProcessState is DeskLinkProcessState.Running or DeskLinkProcessState.Starting) throw new InvalidOperationException("请先停止此 Mac 的共享。");
            if (displaySwitch.IsWriteInFlight || displays.HasUnfinishedChildren) throw new InvalidOperationException("上一条 Mac 显示命令尚未结束，请稍后再开始共享。");
            if (pairing is not null) value.ValidateApprovedPairing(pairing.Host, pairing.WindowsName, pairing.MacName);
            if (!File.Exists(value.EnginePath)) throw new FileNotFoundException("没有找到官方 Deskflow 核心。请先安装 Apple 芯片版 Deskflow，或在高级设置中选择核心文件。", value.EnginePath);
            if (session is { } previous)
            {
                DetachSession(previous); previous.StateChanged -= OnSessionChanged;
                try { await previous.DisposeAsync(); }
                catch { RestoreFailedSession(previous); throw; }
            }
            var options = new DeskflowSessionOptions { EnginePath = value.EnginePath, ProfileDirectory = Path.Combine(store.DirectoryPath, "client"), LocalName = value.MacName, RemoteName = value.WindowsName, Role = DeskLinkRole.Client, ServerAddress = value.WindowsHost, UseNativeControl = false };
            var current = new DeskflowSession(options); ReplaceSession(current); current.StateChanged += OnSessionChanged;
            await store.WriteAsync("connection.json", value, cancellationToken); settings = value;
            displaySwitch.ResetBaseline();
            ApplySessionState(current, await current.StartAsync(cancellationToken)); stopRecoveryNeeded = true; Publish();
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (disposed) return;
            try { await RecoverBeforeStopAsync(); }
            finally
            {
                try { await displays.StopOwnedChildrenAsync(); }
                catch (Exception error) { SetDisplayError("Mac 显示辅助进程停止未完成，请再次点击停止共享：" + error.Message); throw; }
                finally { if (session is { } current) ApplySessionState(current, await current.StopAsync(CancellationToken.None)); displaySwitch.ResetBaseline(); Publish(); }
            }
        }
        finally { gate.Release(); }
    }

    public async Task ConfirmPeerAsync(string fingerprint, bool accept, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var current = session ?? throw new InvalidOperationException("尚未开始 Mac 键鼠连接。");
            if (accept) await current.ConfirmPeerAsync(fingerprint, cancellationToken); else await current.RejectPeerAsync(cancellationToken);
            ApplySessionState(current, await current.GetStateAsync(cancellationToken)); Publish();
        }
        finally { gate.Release(); }
    }

    public async Task<PairingPreview> PreviewPairingAsync(string json, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (json.Length > 65536) throw new InvalidDataException("配对文件过大。");
            var bundle = DeskLinkPairingBundle.Parse(json);
            var preview = new PairingPreview(Guid.NewGuid().ToString("N"), bundle.Host, bundle.Port, bundle.WindowsName, bundle.MacName, bundle.CertificateSha256, DescribeBinding(bundle.Display));
            candidate = (preview, bundle); return preview;
        }
        finally { gate.Release(); }
    }

    public async Task AcceptPairingAsync(string previewId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed(); await LoadAsync(cancellationToken);
            if (CurrentClientState.ProcessState is DeskLinkProcessState.Running or DeskLinkProcessState.Starting || displaySwitch.IsWriteInFlight || displays.HasUnfinishedChildren) throw new InvalidOperationException("请先停止共享并等待显示命令结束，再更换配对设备。");
            if(previewBeginPending||ownedPreviewId is not null||previewHandoff.HasSession)
            {
                await ReturnPreviewBeforeStopInsideAsync();
                if(previewBeginPending||ownedPreviewId is not null||previewHandoff.HasSession)throw new InvalidOperationException("请先确认预览键鼠已经归还，再更换配对设备。");
            }
            var pending = candidate ?? throw new InvalidOperationException("请重新导入待核对的配对文件。");
            if (pending.Preview.Id != previewId) throw new InvalidOperationException("配对资料已变化，请重新核对。");
            await store.WriteAsync("paired-control.json", new SavedPairing(true, pending.Bundle.ToJson()), cancellationToken);
            settings = settings with { WindowsHost = pending.Bundle.Host, WindowsName = pending.Bundle.WindowsName, MacName = pending.Bundle.MacName };
            await store.WriteAsync("connection.json", settings, cancellationToken);
            if (control is not null) await control.DisposeAsync();
            pairing = pending.Bundle; control = new(pairing, userApproved: true); candidate = null; controlReady = false;
            previewHandoff=new();ownedPreviewId=null;previewBeginPending=false;ClearPreviewDisplayIntent();
            ConfigureDisplayRecovery(); PublishApprovedPairing(); Publish();
        }
        finally { gate.Release(); }
    }

    public async Task<MacSwitchOutcome> SwitchAsync(string target, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (pairing is null || control is null) throw new InvalidOperationException("联合切换通道尚未就绪。");
            if (target != pairing.WindowsName && target != pairing.MacName) throw new ArgumentException("只能切到已配对的两台电脑。");
            if(await TryReturnPreviewForSwitchInsideAsync(target,cancellationToken) is{} previewReturn){Publish();return previewReturn;}
            if(!controlReady)throw new InvalidOperationException("联合切换通道尚未就绪。");
            if (target == pairing.MacName && !IsTrustedConnected(CurrentClientState)) throw new InvalidOperationException("Mac 键鼠连接尚未就绪或指纹尚未确认，不能切到 Mac。");
            if (displaySwitch.IsWriteInFlight || displays.HasUnfinishedChildren) throw new InvalidOperationException("上一条 Mac 显示命令尚未结束；为避免迟到切屏，请稍后重试。");
            var result = await RequestSwitchInsideAsync(target, cancellationToken);
            Publish(); return result;
        }
        finally { gate.Release(); }
    }

    private async Task<MacSwitchOutcome> RequestSwitchInsideAsync(string target, CancellationToken token)
    {
        var result = await control!.SwitchAsync(target, token);
        windowsState = (windowsState ?? new()) with { ActiveComputer = result.ActiveComputer };
        if (!result.InputTransferred) return new(result.Ok, result.ActiveComputer ?? "", false, result.VideoStatus, result.Error ?? "");
        if(target!=pairing!.WindowsName&&!previewDisplayIntent)return new(result.Ok,result.ActiveComputer??"",true,result.VideoStatus,result.Error??"");
        try
        {
            windowsState = await ReadWindowsStateAsync(token);
            controlReady=IsNativeReady(windowsState);
            if (!controlReady || windowsState is not { ProcessId: > 0, Revision: >= 0 } || windowsState.ActiveComputer != target)
            {
                SetDisplayError("Windows 原生控制权已变化，未发送 Mac 本地切屏命令。");
                return new(false, windowsState.ActiveComputer ?? "", false, "notAttempted", displayMessage);
            }
            // An authenticated ordinary switch also confirms new intent at the same native revision.
            // Keep the coordinator's old revision watermark so late polls remain harmless.
            if(!windowsState.DisplaySuppressed&&windowsState.InputPreview?.HasSession!=true)ClearPreviewDisplayIntent();
            if(target!=pairing!.WindowsName)return new(result.Ok,result.ActiveComputer??"",true,result.VideoStatus,result.Error??"");
            var local = await displaySwitch.ReturnAfterConfirmedExplicitSwitchAsync(windowsState, token);
            SetDisplayMessage(local);
            if(local.VideoStatus=="suppressed")return new(result.Ok,result.ActiveComputer??"",true,"suppressed","");
            if (local.VideoStatus == "disabled" && result.VideoStatus == "disabled") return new(true, result.ActiveComputer ?? "", true, "disabled", "");
            return new(false, result.ActiveComputer ?? "", true, local.VideoStatus == "disabled" ? "notAttempted" : local.VideoStatus, local.Error ?? "");
        }
        catch (Exception error) when (!token.IsCancellationRequested && IsControlError(error))
        {
            displaySwitch.ResetBaseline(); controlReady = false;
            SetDisplayError("键鼠已交接，但 Mac 未取得新的原生焦点确认，未发送本地切屏命令。");
            return new(false, result.ActiveComputer ?? "", true, "notAttempted", displayMessage + error.Message);
        }
    }

    private async Task RecoverBeforeStopAsync()
    {
        if(await ReturnPreviewBeforeStopInsideAsync()) {stopRecoveryNeeded=false;return;}
        if (!stopRecoveryNeeded) return;
        stopRecoveryNeeded = false;
        if (control is null || pairing is null) { SetDisplayError("未建立联合切换配对，停止前无法确认 Windows 控制权；请手动确认显示器输入。"); return; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(9));
        try { var result = await RequestSwitchInsideAsync(pairing.WindowsName, deadline.Token); if (!result.InputTransferred) SetDisplayError("停止前未能确认键鼠回到 Windows，未发送 Mac 切屏命令。"); }
        catch (Exception error) when (IsControlError(error)) { SetDisplayError("停止前恢复未完成：" + error.Message + " 请检查显示器输入。"); displaySwitch.ResetBaseline(); }
    }

    private async Task<DeskLinkState> ReadWindowsStateAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var value=await control!.GetStateAsync(timeout.Token);
        ObservePreviewState(value);return value;
    }
    private static bool IsNativeReady(DeskLinkState value) => value.NativeControlAvailable && value.ProcessState == DeskLinkProcessState.Running;
    private static bool IsControlError(Exception error) => error is IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or TimeoutException;
    private void ConfigureDisplayRecovery() { displaySwitch.Configure(pairing?.Display, settings.WindowsName, settings.MacName); displayMessage = DescribeBinding(pairing?.Display); displayRecoveryError = false; }
    private static string DescribeBinding(DeskLinkDisplayBinding? value) => value is { Enabled: true } ? $"Mac 本地回切已启用（显示器 {value.VendorId:X4}:{value.ProductId:X4}，Windows 输入 {value.WindowsInput}）；切屏命令返回后仍需实际确认画面。" : "此配对未启用 Mac 本地回切；如需自动回 Windows，请在新版 Windows 工具箱重新导出配对。";
    private void SetDisplayMessage(MacLocalDisplaySwitchResult result)
    {
        displayMessage = result.VideoStatus == "acceptedUnverified" ? "Mac 已接受回 Windows 的切屏命令，实际画面仍待确认。" : result.VideoStatus == "disabled" ? DescribeBinding(pairing?.Display) : result.VideoStatus=="suppressed"?"预览操作仅交接键鼠，显示器输入保持不变。":result.Error ?? "Mac 本地显示切换未确认，请检查实际画面。";
        displayRecoveryError = result.NeedsAttention;
    }
    private void SetDisplayError(string message) { displayMessage = message; displayRecoveryError = true; }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (loaded) return;
        settings = await store.ReadAsync<MacConnectionSettings>("connection.json", ct) ?? new();
        if (!File.Exists(settings.EnginePath))
        {
            var userInstall = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Deskflow.app", "Contents", "MacOS", "deskflow-core");
            if (File.Exists(userInstall)) settings = settings with { EnginePath = userInstall };
        }
        var saved = await store.ReadAsync<SavedPairing>("paired-control.json", ct);
        if (saved is { UserApproved: true }) { pairing = DeskLinkPairingBundle.Parse(saved.BundleJson); control = new(pairing, userApproved: true); }
        ConfigureDisplayRecovery(); loaded = true; PublishApprovedPairing();
    }
    private void PublishApprovedPairing()
    {
        // Load and approval commits hold gate; only disposal can change this
        // reference concurrently. Never resurrect a snapshot after that boundary.
        var previous = Volatile.Read(ref approvedPairingSnapshot);
        if (previous?.Disposed != true)
            Interlocked.CompareExchange(ref approvedPairingSnapshot, new ApprovedPairingSnapshot(pairing), previous);
    }
    // Events and awaited reads can complete out of order. Version and session
    // identity are checked together; never call external observers under this gate.
    private static bool IsTrustedConnected(DeskLinkState value) => value.ConnectionState == DeskLinkConnectionState.Connected && string.IsNullOrWhiteSpace(value.PendingPeerFingerprint);
    private DeskLinkState CurrentClientState
    {
        get
        {
            DeskflowSession? owner;
            lock (sessionStateGate) { owner = acceptSessionUpdates ? session : null; if (owner is null) return clientState; }
            var authoritative = owner.CurrentState;
            lock (sessionStateGate)
            {
                if (acceptSessionUpdates && ReferenceEquals(owner, session) && authoritative.SessionStateVersion > clientState.SessionStateVersion)
                    clientState = authoritative;
                return clientState;
            }
        }
    }
    private bool ApplySessionState(DeskflowSession owner, DeskLinkState value)
    {
        lock (sessionStateGate)
        {
            if (!acceptSessionUpdates || !ReferenceEquals(owner, session) || value.SessionStateVersion < clientState.SessionStateVersion) return false;
            clientState = value;
            return true;
        }
    }
    private void ReplaceSession(DeskflowSession? next)
    {
        lock (sessionStateGate) { session = next; acceptSessionUpdates = next is not null; clientState = new(); }
    }
    private void DetachSession(DeskflowSession owner)
    { lock (sessionStateGate) { if (ReferenceEquals(session, owner)) { session = null; acceptSessionUpdates = false; } } }
    private void RestoreFailedSession(DeskflowSession owner)
    {
        // Keep ownership for retry, but never let callbacks captured before the
        // failed cleanup restore stale connection evidence.
        lock (sessionStateGate)
        {
            session = owner; acceptSessionUpdates = false;
            clientState = clientState with { ProcessState = DeskLinkProcessState.Faulted,
                ConnectionState = DeskLinkConnectionState.Disconnected, PendingPeerFingerprint = null,
                LastError = "键鼠共享清理未完成，请再次停止后重试。" };
        }
    }
    private void OnSessionChanged(object? sender, DeskLinkState value)
    { if (sender is DeskflowSession owner && ApplySessionState(owner, value)) Publish(); }
    private MacLinkSnapshot Snapshot()
    {
        // Catch up even if an earlier session observer is still handling a prior event.
        _ = CurrentClientState;
        DeskLinkState currentClient;
        long version;
        lock (sessionStateGate) { currentClient = clientState; version = ++snapshotVersion; }
        return new()
    {
        LocalSnapshotVersion = version,
        PreviewControlState = controlReady ? windowsState : null, PreviewLocalCoreState = currentClient,
        PreviewHandoffSupported=controlReady&&Volatile.Read(ref windowsState) is { PreviewHandoffSupported: true }&&IsTrustedConnected(currentClient),InputPreview=Volatile.Read(ref previewHandoff),
        Backend = MacLinkBackend.Deskflow, Settings = settings, IsRunning = currentClient.ProcessState is DeskLinkProcessState.Starting or DeskLinkProcessState.Running or DeskLinkProcessState.Stopping,
        IsConnected = IsTrustedConnected(currentClient), ControlConnected = controlReady,
        HasApprovedPairing = pairing is not null, ActiveComputer = windowsState?.ActiveComputer ?? "",
        PendingPeerFingerprint = currentClient.PendingPeerFingerprint ?? "", LocalFingerprint = currentClient.LocalFingerprint ?? "", DisplayRecoveryMessage = displayMessage, HasDisplayRecoveryError = displayRecoveryError,
        StatusMessage = Volatile.Read(ref previewHandoff).HasSession?"预览操作仅交接键鼠，显示器输入保持不变。":IsTrustedConnected(currentClient) ? "Mac 键鼠连接已建立；显示器按已确认的控制权联动。" : currentClient.ProcessState == DeskLinkProcessState.Faulted ? "键鼠共享异常，请检查错误和系统权限。" : currentClient.ProcessState == DeskLinkProcessState.Stopped ? "尚未开始共享。" : "正在连接 Windows，请按提示核对指纹与系统权限。",
        HasPendingDisplayCleanup = displays.HasUnfinishedChildren, Error = currentClient.LastError ?? controlError
        };
    }
    private MacLinkSnapshot Publish() { var value = Snapshot(); Changed?.Invoke(value); return value; }
    private void ThrowIfDisposed() { if (disposed || Volatile.Read(ref approvedPairingSnapshot)?.Disposed == true) throw new ObjectDisposedException(nameof(MacLinkService)); }
    public async ValueTask DisposeAsync()
    {
        // Revoke read access immediately without changing the cleanup flag used
        // under gate below. A pending network refresh must not delay revocation.
        Interlocked.Exchange(ref approvedPairingSnapshot, new ApprovedPairingSnapshot(null, Disposed: true));
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            await RecoverBeforeStopAsync();
            if (session is { } previous)
            {
                DetachSession(previous); previous.StateChanged -= OnSessionChanged;
                try { await previous.DisposeAsync(); }
                catch { RestoreFailedSession(previous); throw; }
            }
            if (control is not null) await control.DisposeAsync();
            await displays.DisposeAsync(); displaySwitch.Dispose(); disposed = true;
        }
        finally { gate.Release(); }
    }
    private sealed record SavedPairing(bool UserApproved, string BundleJson);
}
