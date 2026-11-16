using System.ComponentModel;
using System.Runtime.CompilerServices;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.ViewModels;

public sealed class MacLinkViewModel : IMacMiniLink, IDisposable
{
    private readonly IMacLinkService service;
    private readonly Action<Action> dispatch;
    private readonly SemaphoreSlim gate = new(1, 1);
    private MacLinkSnapshot snapshot = new();
    private bool initialized;
    private bool disposed;
    private bool busy;
    private string windowsHost = "";
    private string windowsName = "Win-PC";
    private string macName = "My-Mac";
    private string enginePath = new MacConnectionSettings().EnginePath;
    private string statusMessage = "尚未开始共享";
    private string errorMessage = "";
    private string pairingStatusMessage = "";
    private string pairingErrorMessage = "";
    private PairingPreview? pendingPairing;
    private (string Status, string Error)? lastSwitchMessage;

    public MacLinkViewModel(IMacLinkService service, Action<Action>? dispatch = null)
    {
        this.service = service;
        this.dispatch = dispatch ?? (action => action());
        service.Changed += OnServiceChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string WindowsHost { get => windowsHost; set => Set(ref windowsHost, value); }
    public string WindowsName { get => windowsName; set => Set(ref windowsName, value); }
    public string MacName { get => macName; set => Set(ref macName, value); }
    public string EnginePath { get => enginePath; set => Set(ref enginePath, value); }
    public bool IsBusy => busy;
    public bool CanEditConnection => !busy && !snapshot.IsRunning && !snapshot.HasPendingDisplayCleanup;
    public bool CanStart => !busy && !snapshot.IsRunning && !snapshot.HasPendingDisplayCleanup;
    public bool CanStop => !busy && (snapshot.IsRunning || snapshot.HasPendingDisplayCleanup);
    public bool CanSwitch => !busy && !snapshot.HasPendingDisplayCleanup && snapshot.IsRunning && snapshot.IsConnected && snapshot.ControlConnected;
    public bool CanReturnToWindows => !busy && !snapshot.HasPendingDisplayCleanup && snapshot.ControlConnected;
    public bool IsSharingRunning => snapshot.IsRunning;
    public bool IsInputConnected => snapshot.IsRunning && snapshot.IsConnected && snapshot.ControlConnected;
    public string? RemotePreviewReturnId => service is MacLinkService ? MacRemotePreviewReturn.CurrentId(snapshot) : null;
    public async Task ReturnRemotePreviewAsync(string id)
    {
        if(service is not MacLinkService deskflow || RemotePreviewReturnId!=id)
            throw new InvalidOperationException("预览已结束或连接已变化，请使用快捷键归还。 ");
        var result=await deskflow.ReturnRemotePreviewAsync(id);
        if(result.HasSession || result.Phase!=DesktopToolbox.DeskLink.DeskflowPreviewPhase.Viewing)
            throw new InvalidOperationException(result.Message.Length>0?result.Message:"尚未确认键鼠归还，请重试或使用返回快捷键。");
    }
    public bool HasApprovedPairing => snapshot.HasApprovedPairing;
    public bool CanUseCollaborationView => MacWindowPresentation.UseCollaboration(snapshot, HasPairingPreview);
    public bool CanUseCompactView => errorMessage.Length == 0 && MacWindowPresentation.UseCompact(snapshot, false, HasPairingPreview);
    public bool CanConfirmPeer => !busy && PendingPeerFingerprint.Length > 0;
    public bool HasPairingPreview => pendingPairing is not null;
    public PairingPreview? PendingPairing => pendingPairing;
    public string PendingPeerFingerprint => snapshot.PendingPeerFingerprint;
    public string LocalFingerprint => snapshot.LocalFingerprint.Length > 0 ? snapshot.LocalFingerprint : "开始共享后生成并显示";
    public string StatusMessage => statusMessage;
    public string DisplayRecoveryMessage => snapshot.DisplayRecoveryMessage;
    public string ErrorMessage => errorMessage;
    public string PairingStatusMessage => pairingStatusMessage;
    public string PairingErrorMessage => pairingErrorMessage;
    public string ConnectionLabel => snapshot.IsConnected ? "键鼠连接已建立" : snapshot.IsRunning ? "正在等待 Windows 连接" : "共享已停止";
    public string ControlLabel => snapshot.ControlConnected ? "联合切换通道已连接" : snapshot.HasApprovedPairing ? "联合切换通道未连接" : "尚未导入并确认配对";
    private bool HasLiveControl => snapshot.IsRunning && snapshot.IsConnected && snapshot.ControlConnected;
    public bool IsWindowsActive => HasLiveControl && snapshot.ActiveComputer == snapshot.Settings.WindowsName;
    public bool IsMacActive => HasLiveControl && snapshot.ActiveComputer == snapshot.Settings.MacName;
    public string ActiveComputerLabel => IsWindowsActive ? "● 正在控制 Windows" : IsMacActive ? "● 正在控制 Mac" : "控制目标待确认";
    public string PairingDescription => pendingPairing is null ? "" : $"Windows：{pendingPairing.WindowsName} · {pendingPairing.Host}:{pendingPairing.Port}\nMac：{pendingPairing.MacName}\nWindows 证书 SHA-256：\n{pendingPairing.CertificateSha256}\n请与 Windows 工作台显示的指纹核对，一致后确认。\n{pendingPairing.DisplayDescription}";

    public Task InitializeAsync() => RunAsync(async () => ApplySnapshot(await service.RefreshAsync(), replaceSettings: true));
    public Task RefreshAsync() => RunAsync(async () => ApplySnapshot(await service.RefreshAsync()));
    public Task StartAsync() => RunAsync(async () =>
    {
        lastSwitchMessage = null;
        if (string.IsNullOrWhiteSpace(WindowsHost)) throw new ArgumentException("请填写 Windows 的局域网 IP 或主机名。");
        if (string.IsNullOrWhiteSpace(WindowsName) || string.IsNullOrWhiteSpace(MacName)) throw new ArgumentException("请填写两台电脑名称。");
        if (string.Equals(WindowsName.Trim(), MacName.Trim(), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("两台电脑名称必须不同。");
        await service.StartAsync(new() { WindowsHost = WindowsHost.Trim(), WindowsName = WindowsName.Trim(), MacName = MacName.Trim(), EnginePath = EnginePath.Trim() });
        ApplySnapshot(await service.RefreshAsync());
    });
    public Task StopAsync() => RunAsync(async () => { lastSwitchMessage = null; await service.StopAsync(); ApplySnapshot(await service.RefreshAsync()); });
    public Task PreviewPairingAsync(string json) => RunPairingAsync(async () =>
    {
        SetPairingFeedback("正在读取配对资料…", "");
        if (json.Length > 65536) throw new ArgumentException("配对文件过大，请从 Windows 重新导出。");
        pendingPairing = await service.PreviewPairingAsync(json);
        statusMessage = "配对资料已读取，核对指纹后才会连接。";
        SetPairingFeedback(statusMessage, "");
    });
    public Task AcceptPairingAsync() => RunPairingAsync(async () =>
    {
        var candidate = pendingPairing ?? throw new InvalidOperationException("请先导入并核对配对资料。");
        if (snapshot.IsRunning) throw new InvalidOperationException("请先停止共享，再更换配对设备。");
        SetPairingFeedback("正在确认配对，请留意本机钥匙串和 Windows 的确认提示…", "");
        await service.AcceptPairingAsync(candidate.Id);
        WindowsHost = candidate.Host; WindowsName = candidate.WindowsName; MacName = candidate.MacName;
        pendingPairing = null;
        ApplySnapshot(await service.RefreshAsync());
        statusMessage = "配对已确认；开始共享后可使用联合切换。";
        SetPairingFeedback(statusMessage, "");
    });
    public void RejectPairing()
    {
        if (busy) return;
        pendingPairing = null;
        statusMessage = "未接受此次配对资料。";
        SetPairingFeedback(statusMessage, "");
    }
    public Task ConfirmPeerAsync(bool accept) => RunAsync(async () =>
    {
        var fingerprint = snapshot.PendingPeerFingerprint;
        if (fingerprint.Length == 0) throw new InvalidOperationException("当前没有待确认的键鼠连接指纹。");
        await service.ConfirmPeerAsync(fingerprint, accept);
        ApplySnapshot(await service.RefreshAsync());
        statusMessage = accept ? "已保存你确认的指纹，正在等待重新连接。" : "已拒绝此次键鼠连接。";
    });
    public Task SwitchAsync(string target) => RunAsync(async () =>
    {
        lastSwitchMessage = null;
        if (!snapshot.ControlConnected || target == MacName && (!snapshot.IsRunning || !snapshot.IsConnected)) throw new InvalidOperationException("请先建立已确认的联合切换通道；切到 Mac 还需要键鼠连接就绪。");
        if (target != WindowsName && target != MacName) throw new ArgumentException("只能切到已配置的 Windows 或 Mac。");
        var result = await service.SwitchAsync(target);
        if (result.ActiveComputer.Length > 0) snapshot = snapshot with { ActiveComputer = result.ActiveComputer };
        if (result.InputTransferred && result.VideoStatus == "acceptedUnverified")
            statusMessage = "键鼠已交接；显示器已接受切换命令，画面尚待你确认。";
        else if (result.Ok && result.VideoStatus == "disabled")
            statusMessage = "键鼠已切换；画面联动关闭，显示器输入保持原状。";
        else if (result.Ok && result.VideoStatus == "verified")
            statusMessage = "键鼠与显示器输入均已确认。";
        else { errorMessage = result.Error.Length > 0 ? result.Error : "联合切换未全部完成。"; statusMessage = result.InputTransferred ? "键鼠已交接，视频切换需要检查。" : "键鼠尚未交接，未确认视频切换。"; }
        lastSwitchMessage = (statusMessage, errorMessage);
    });

    private void SetPairingFeedback(string status, string error)
    {
        pairingStatusMessage = status; pairingErrorMessage = error;
        NotifyAll();
    }

    private Task RunPairingAsync(Func<Task> operation) => RunAsync(async () =>
    {
        // Invoke the operation immediately: the service validates AppKit currentEvent
        // synchronously at the final approval boundary, before its first asynchronous wait.
        try { await operation(); }
        catch (Exception failure)
        {
            SetPairingFeedback(failure is OperationCanceledException ? "配对等待已取消或超时。" : "配对未完成，请查看原因。", failure.Message);
            throw;
        }
    });

    private async Task RunAsync(Func<Task> operation)
    {
        if (disposed || !await gate.WaitAsync(0)) return;
        busy = true; errorMessage = ""; NotifyAll();
        try { await operation(); }
        catch (OperationCanceledException) { statusMessage = "操作已取消。"; }
        catch (Exception error) { errorMessage = error.Message; statusMessage = "操作未完成，请按提示检查。"; }
        finally { busy = false; gate.Release(); NotifyAll(); }
    }
    private void OnServiceChanged(MacLinkSnapshot value) => dispatch(() => { if (!disposed) { ApplySnapshot(value); NotifyAll(); } });
    private void ApplySnapshot(MacLinkSnapshot value, bool replaceSettings = false)
    {
        // The dispatcher can deliver callbacks in a different order from the
        // service. Legacy services that never assign a version remain supported.
        if (snapshot.LocalSnapshotVersion > 0 &&
            (value.LocalSnapshotVersion == 0 || value.LocalSnapshotVersion < snapshot.LocalSnapshotVersion)) return;
        if (value.ActiveComputer.Length > 0 && snapshot.ActiveComputer != value.ActiveComputer) lastSwitchMessage = null;
        snapshot = value;
        if (replaceSettings || !initialized)
        {
            WindowsHost = value.Settings.WindowsHost; WindowsName = value.Settings.WindowsName;
            MacName = value.Settings.MacName; EnginePath = value.Settings.EnginePath; initialized = true;
        }
        if (value.IsRunning && value.IsConnected && value.ControlConnected && value.Error.Length == 0 && lastSwitchMessage is { } outcome)
        { statusMessage = outcome.Status; errorMessage = outcome.Error; }
        else
        {
            statusMessage = value.StatusMessage; errorMessage = value.Error;
            if (!value.IsRunning || !value.IsConnected || !value.ControlConnected) lastSwitchMessage = null;
        }
    }
    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    { if (field == value) return; field = value; PropertyChanged?.Invoke(this, new(name)); }
    private void NotifyAll() => PropertyChanged?.Invoke(this, new(null));
    public void Dispose() { disposed = true; service.Changed -= OnServiceChanged; }
}
