using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace DesktopToolbox.Windows.DeskSwitch;

public sealed partial class IntegratedDeskSwitchService
{
    private Task previewWatch = Task.CompletedTask;
    private long previewPeerObservedAt;
    private bool RecentPreviewPeer => Volatile.Read(ref previewPeerObservedAt) is var stamp && stamp > 0 &&
        System.Diagnostics.Stopwatch.GetElapsedTime(stamp) <= TimeSpan.FromSeconds(3);
    public bool PreviewHandoffSupported
    {
        get
        {
            var current = CurrentSessionState;
            return coordinator is not null && Volatile.Read(ref stopping) == 0 &&
                current.ProcessState == DeskLinkProcessState.Running && current.NativeControlAvailable && RecentPreviewPeer;
        }
    }
    public string PreviewReturnShortcut => "Ctrl+Alt+F11";
    public string PreviewHandoffHint => !IsRunning ? "请先启动键鼠共享，并确认 Mac 已连接。" :
        !RecentPreviewPeer ? "请打开新版 Mac 互联客户端，等待仅键鼠操作通道就绪。" :
        !PreviewHandoffSupported ? "正在等待键鼠共享就绪。" :
        "将键鼠交给 Mac，当前屏幕保持 Windows；按 Ctrl+Alt+F11 返回。";

    public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken token = default) =>
        BeginInputPreviewAsync(false, token);
    public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken token = default) =>
        InvokeInputPreviewAsync((handoff, computer, ct) => handoff.RenewPreviewAsync(id, computer, ct), false, token);
    public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken token = default) =>
        InvokeInputPreviewAsync((handoff, _, ct) => handoff.EndPreviewAsync(id, ct), false, token);
    public async Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var current = coordinator;
        if (current is not null) return await current.ReadPreviewAsync(token);
        if (IsRunning) throw new InvalidOperationException("键鼠共享正在恢复，尚未确认归还；请使用原返回快捷键。");
        return new() { Phase = DeskflowPreviewPhase.Viewing, ReturnShortcut = PreviewReturnShortcut, Message = "键鼠共享已停止，仅观看画面。" };
    }

    private async Task<DeskflowPreviewState> InvokeInputPreviewAsync(
        Func<UnifiedSwitchCoordinator, string, CancellationToken, Task<DeskflowPreviewState>> action,
        bool remoteViewer, CancellationToken token)
    {
        var current = coordinator;
        var focus = focusLifetime;
        var configured = settings;
        if (Volatile.Read(ref stopping) != 0 || Volatile.Read(ref disposed) != 0 ||
            current is null || focus is null || configured is null)
            throw new InvalidOperationException("键鼠共享未就绪，请先开始共享。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, focus.Token);
        var result = await action(current, remoteViewer ? configured.RemoteName : configured.LocalName, linked.Token);
        Publish();
        return result;
    }

    private Task<DeskflowPreviewState> BeginInputPreviewAsync(bool remoteViewer, CancellationToken token)
    {
        // An old Mac ignores suppression metadata and could still send a delayed display write.
        // Only a fresh, authenticated state request advertising this capability admits a start.
        if (!RecentPreviewPeer) throw new InvalidOperationException("请先打开新版 Mac 互联客户端；旧版不能保证只交接键鼠。");
        return InvokeInputPreviewAsync((handoff, computer, ct) => handoff.BeginPreviewAsync(computer, ct), remoteViewer, token);
    }

    private async Task WatchPreviewExpiryAsync(UnifiedSwitchCoordinator handoff, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (!ReferenceEquals(coordinator, handoff)) return;
                try
                {
                    var before = await handoff.ReadPreviewAsync(token);
                    var after = await handoff.ExpirePreviewAsync(token);
                    if (before != after) Publish();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    lastError = "仅键鼠操作自动归还检查失败：" + error.Message;
                    Publish();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            // A failed return stays visible; never restart the input core or send a display command here.
            lastError = "仅键鼠操作自动归还检查失败：" + error.Message;
            Publish();
        }
    }

    /// <summary>The authenticated paired endpoint always represents the configured Mac viewer.</summary>
    private sealed class RemotePreviewEndpoint(IntegratedDeskSwitchService owner) : IDeskflowPreviewControl
    {
        public bool PreviewHandoffSupported => owner.PreviewHandoffSupported;
        public string PreviewReturnShortcut => "Ctrl+Alt+F12";
        public string PreviewHandoffHint => owner.PreviewHandoffHint;
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken token = default) =>
            owner.BeginInputPreviewAsync(true, token);
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken token = default) =>
            owner.InvokeInputPreviewAsync((handoff, computer, ct) => handoff.RenewPreviewAsync(id, computer, ct), true, token);
        public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken token = default) => owner.EndPreviewAsync(id, token);
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken token = default) => owner.ReadPreviewAsync(token);
    }
}
