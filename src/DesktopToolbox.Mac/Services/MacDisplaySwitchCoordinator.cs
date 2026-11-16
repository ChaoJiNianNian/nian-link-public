using DesktopToolbox.DeskLink;

namespace DesktopToolbox.Mac.Services;

public sealed record MacLocalDisplaySwitchResult(bool Attempted, string VideoStatus, string? Error)
{
    public bool NeedsAttention => VideoStatus is "failed" or "canceled" or "notAttempted";
}

/// <summary>Serializes Mac-side video writes after actual Windows native focus confirmation.</summary>
public sealed class MacDisplaySwitchCoordinator : IDisposable
{
    private readonly object sync = new();
    private readonly Func<DeskLinkDisplayBinding, CancellationToken, Task<MacLocalDisplaySwitchResult>> write;
    private readonly TimeSpan timeout;
    private readonly HashSet<(int ProcessId, long Revision)> attempted = [];
    private readonly HashSet<int> retiredProcesses = [];
    private readonly Dictionary<int,long> displaySuppressionThrough = [];
    private DeskLinkDisplayBinding? binding;
    private string windowsName = "";
    private string macName = "";
    private int? processId;
    private long revision;
    private string? activeComputer;
    private Task<MacLocalDisplaySwitchResult>? inFlight;
    private CancellationTokenSource? inFlightCancellation;
    private bool disposed;

    public MacDisplaySwitchCoordinator(Func<DeskLinkDisplayBinding, CancellationToken, Task<MacLocalDisplaySwitchResult>> write, TimeSpan? timeout = null)
    {
        this.write = write;
        this.timeout = timeout ?? TimeSpan.FromSeconds(7);
        if (this.timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public bool IsWriteInFlight { get { lock (sync) return inFlight is not null; } }

    /// <summary>A locally requested input-only operation must never turn its observed focus into video intent.</summary>
    public void SuppressThrough(DeskLinkState? state)
    {
        if(state is not{ProcessState:DeskLinkProcessState.Running,NativeControlAvailable:true,ProcessId:>0,Revision:>=0})return;
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var pid=state.ProcessId.Value;
            var old=displaySuppressionThrough.TryGetValue(pid,out var value)?value:-1;
            displaySuppressionThrough[pid]=Math.Max(old,Math.Max(state.Revision,state.DisplaySuppressedThroughRevision));
        }
    }

    public void Configure(DeskLinkDisplayBinding? value, string windowsComputer, string macComputer)
    {
        value?.Validate();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            binding = value; windowsName = windowsComputer; macName = macComputer;
            processId = null; activeComputer = null; revision = 0;
            attempted.Clear(); retiredProcesses.Clear(); displaySuppressionThrough.Clear();
        }
    }

    public void ResetBaseline()
    {
        lock (sync) { processId = null; activeComputer = null; revision = 0; }
    }

    public Task<MacLocalDisplaySwitchResult?> ObserveAsync(DeskLinkState? confirmedState, CancellationToken cancellationToken = default) =>
        EvaluateAsync(confirmedState, explicitlyRequested: false, cancellationToken);

    public async Task<MacLocalDisplaySwitchResult> ReturnAfterConfirmedFocusAsync(DeskLinkState? confirmedState, CancellationToken cancellationToken = default) =>
        await EvaluateAsync(confirmedState, explicitlyRequested: true, cancellationToken) ?? new(false, "notAttempted", "Windows 键鼠控制权尚未确认，未发送 Mac 切屏命令。");

    /// <summary>Requires a successful ordinary paired switch and a fresh matching native state, not a focus poll or preview return.</summary>
    public async Task<MacLocalDisplaySwitchResult> ReturnAfterConfirmedExplicitSwitchAsync(DeskLinkState? confirmedState, CancellationToken cancellationToken = default) =>
        await EvaluateAsync(confirmedState, explicitlyRequested: true, cancellationToken, confirmedOrdinarySwitch: true) ?? new(false, "notAttempted", "Windows 键鼠控制权尚未确认，未发送 Mac 切屏命令。");

    private async Task<MacLocalDisplaySwitchResult?> EvaluateAsync(DeskLinkState? state, bool explicitlyRequested, CancellationToken cancellationToken, bool confirmedOrdinarySwitch = false)
    {
        Task<MacLocalDisplaySwitchResult> work;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (binding is not { Enabled: true }) return explicitlyRequested ? new(false, "disabled", "此配对尚未启用 Mac 本地回切，请从 Windows 重新导出并确认配对。") : null;
            if (state is not { ProcessState: DeskLinkProcessState.Running, NativeControlAvailable: true, ProcessId: > 0, Revision: >= 0 } ||
                (state.ActiveComputer != windowsName && state.ActiveComputer != macName))
            {
                processId = null; activeComputer = null; revision = 0;
                return explicitlyRequested ? new(false, "notAttempted", "未取得 Windows 原生控制权确认，未发送切屏命令。") : null;
            }
            var currentPid = state.ProcessId.Value;
            if (retiredProcesses.Contains(currentPid)) return null;
            var suppressionThrough=displaySuppressionThrough.TryGetValue(currentPid,out var remembered)?remembered:-1;
            suppressionThrough=Math.Max(suppressionThrough,state.DisplaySuppressedThroughRevision);
            if(state.DisplaySuppressed)suppressionThrough=Math.Max(suppressionThrough,state.Revision);
            displaySuppressionThrough[currentPid]=suppressionThrough;
            var suppressed=state.DisplaySuppressed||state.InputPreview?.HasSession==true||!confirmedOrdinarySwitch&&state.Revision<=suppressionThrough;
            var initial = processId != currentPid;
            if (initial)
            {
                if (processId is { } previousPid) retiredProcesses.Add(previousPid);
                processId = currentPid; revision = state.Revision; activeComputer = state.ActiveComputer;
                if (!explicitlyRequested) return null;
            }
            else if (state.Revision < revision || state.Revision == revision && state.ActiveComputer != activeComputer)
                return suppressed&&explicitlyRequested?Suppressed():null;
            // An authenticated explicit intent can advance past a Mac revision between polls.
            var newWindowsIntent = !initial && state.ActiveComputer == windowsName && state.Revision > revision;
            revision = state.Revision; activeComputer = state.ActiveComputer;
            if(suppressed)return explicitlyRequested?Suppressed():null;
            if (state.ActiveComputer != windowsName || !explicitlyRequested && !newWindowsIntent) return null;
            var key = (currentPid, state.Revision);
            if (!explicitlyRequested && attempted.Contains(key)) return null;
            if (inFlight is not null) return new(false, "notAttempted", "上一条 Mac 显示命令尚未返回，未排队重复切换；请稍后重试。");
            if (attempted.Count >= 128) attempted.RemoveWhere(item => item.ProcessId != currentPid || item.Revision < state.Revision - 64);
            if (retiredProcesses.Count > 128) retiredProcesses.Clear();
            attempted.Add(key);
            var capturedBinding = binding;
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            inFlightCancellation = deadline;
            // Keep ownership until the real native task finishes, even when its caller times out.
            // An entered IOKit transaction cannot be forcibly cancelled by a managed token.
            Task<MacLocalDisplaySwitchResult>? owned = null;
            owned = Task.Run(async () =>
            {
                try { return (await write(capturedBinding, deadline.Token)) with { Attempted = true }; }
                catch (OperationCanceledException) { return new(true, cancellationToken.IsCancellationRequested ? "canceled" : "failed", "Mac 显示命令未能在限时内确认；未安排自动重试，请检查实际画面。"); }
                catch (Exception error) { return new(true, "failed", "Mac 显示切换失败：" + error.Message); }
                finally
                {
                    lock (sync) { if (ReferenceEquals(inFlight, owned)) { inFlight = null; inFlightCancellation = null; } }
                    deadline.Dispose();
                }
            }, CancellationToken.None);
            work = inFlight = owned;
        }
        try { return await work.WaitAsync(timeout, cancellationToken); }
        catch (TimeoutException) { return new(true, "failed", "Mac 显示命令仍未返回，实际画面尚未确认；不会自动排队重试。"); }
        catch (OperationCanceledException) { return new(true, "canceled", "Mac 显示切换请求已取消；已进入的原生命令可能尚未返回，请检查实际画面。"); }
    }

    private static MacLocalDisplaySwitchResult Suppressed()=>new(false,"suppressed","预览操作仅交接键鼠，显示器输入保持不变。");

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            inFlightCancellation?.Cancel();
        }
    }
}
