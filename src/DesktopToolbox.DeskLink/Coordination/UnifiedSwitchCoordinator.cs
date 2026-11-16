namespace DesktopToolbox.DeskLink.Coordination;

public interface IKeyboardHandoffControl
{
    Task<NativeControlReply> GetStateAsync(CancellationToken cancellationToken);
    Task<NativeControlReply> SwitchAsync(string computer, CancellationToken cancellationToken);
}
public sealed record DisplaySwitchOutcome(string Status, string? Error = null);
public interface IUnifiedDisplayControl
{
    Task<DisplaySwitchOutcome> ApplyAsync(string computer, CancellationToken cancellationToken);
}

/// <summary>One ordered path for buttons, paired requests and native focus events.</summary>
public sealed partial class UnifiedSwitchCoordinator : IAsyncDisposable
{
    private readonly IKeyboardHandoffControl keyboard;
    private readonly IUnifiedDisplayControl display;
    private readonly string localName;
    private readonly string remoteName;
    private readonly bool linkDisplay;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private long lastHandledRevision = -1;
    private long highestObservedRevision = -1;
    private int disposed;

    public UnifiedSwitchCoordinator(IKeyboardHandoffControl keyboard, IUnifiedDisplayControl display,
        string localName, string remoteName, bool linkDisplay, TimeProvider? clock = null)
    {
        this.keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        this.display = display ?? throw new ArgumentNullException(nameof(display));
        if (string.IsNullOrWhiteSpace(localName) || string.IsNullOrWhiteSpace(remoteName) ||
            localName.Length > 64 || remoteName.Length > 64 ||
            string.Equals(localName, remoteName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请设置两个不同的有效电脑名称。");
        this.localName = localName;
        this.remoteName = remoteName;
        this.linkDisplay = linkDisplay;
        previewClock = clock ?? TimeProvider.System;
    }

    public DeskLinkSwitchResult? LastResult { get; private set; }
    public event EventHandler<DeskLinkSwitchResult>? ResultChanged;

    public async Task<DeskLinkSwitchResult> SwitchAsync(string computer, CancellationToken cancellationToken = default)
    {
        var target = CanonicalTarget(computer);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(linked.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (CurrentPreview.HasSession && (await ReturnPreviewInsideAsync()).HasSession)
                return Publish(Failed(target, null, "预览键鼠尚未确认归还，未执行新的切换。"));
            NativeControlReply before;
            try { before = await keyboard.GetStateAsync(linked.Token); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
            { return Publish(Failed(target, null, "无法读取键鼠共享状态：" + error.Message)); }
            if (!before.Ok)
                return Publish(Failed(target, before.ActiveComputer, before.Error ?? "键鼠共享服务尚未就绪。"));
            if (target == remoteName && !before.ConnectedComputers.Any(name => Same(name, remoteName)))
                return Publish(Failed(target, before.ActiveComputer, "Mac 尚未连接，未切换键鼠或画面。"));

            NativeControlReply reply;
            try { reply = await keyboard.SwitchAsync(target, linked.Token); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
            { return Publish(Failed(target, null, "键鼠交接没有得到确认：" + error.Message)); }
            if (!reply.Ok || !Same(reply.ActiveComputer, target))
                return Publish(Failed(target, reply.ActiveComputer, reply.Error ?? "键鼠控制目标未得到确认，未切换画面。"));
            ObserveRevision(reply.Revision);
            if (reply.Revision < Volatile.Read(ref highestObservedRevision))
                return Publish(Failed(target, null, "控制目标已被较新的操作改变，本次没有切换画面。"));

            // Explicit buttons may repair a failed or manually changed display at the same focus revision.
            ClearInputPreviewIntent();
            return await ApplyDisplayAsync(target, reply.Revision, linked.Token);
        }
        finally { gate.Release(); }
    }

    public async Task<DeskLinkSwitchResult?> OnFocusChangedAsync(FocusChangedEvent change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var target = CanonicalTarget(change.ActiveComputer);
        if (change.Revision < 0) throw new ArgumentException("控制状态版本无效。", nameof(change));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ObserveRevision(change.Revision);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(linked.Token);
        try
        {
            if (await SuppressPreviewFocusAsync(change, linked.Token)) return LastResult;
            if (change.Revision <= lastHandledRevision || change.Revision < Volatile.Read(ref highestObservedRevision))
                return LastResult;
            return await ApplyDisplayAsync(target, change.Revision, linked.Token);
        }
        finally { gate.Release(); }
    }

    private async Task<DeskLinkSwitchResult> ApplyDisplayAsync(string target, long revision, CancellationToken token)
    {
        lastHandledRevision = Math.Max(lastHandledRevision, revision);
        if (!linkDisplay)
            return Publish(new() { Ok = true, Target = target, ActiveComputer = target, InputTransferred = true, VideoStatus = "disabled" });

        DisplaySwitchOutcome outcome;
        try { outcome = await display.ApplyAsync(target, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Publish(new() { Target = target, ActiveComputer = target, InputTransferred = true, VideoStatus = "canceled", Error = "键鼠已交接，画面操作已取消。" });
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        { outcome = new("failed", error.Message); }

        if (outcome.Status is not ("verified" or "acceptedUnverified" or "failed"))
            outcome = new("failed", "显示器返回了无法确认的结果。");
        return Publish(new()
        {
            Ok = outcome.Status == "verified",
            Target = target,
            ActiveComputer = target,
            InputTransferred = true,
            VideoStatus = outcome.Status,
            Error = outcome.Status == "verified" ? null : outcome.Error ??
                (outcome.Status == "acceptedUnverified" ? "键鼠已交接，显示器已接受命令，但无法读取实际输入源确认，请以画面为准。" : "键鼠已交接，显示器切换未完成。")
        });
    }

    private DeskLinkSwitchResult Publish(DeskLinkSwitchResult result)
    {
        LastResult = result;
        ResultChanged?.Invoke(this, result);
        return result;
    }

    private static DeskLinkSwitchResult Failed(string target, string? active, string error) =>
        new() { Target = target, ActiveComputer = active, InputTransferred = false, VideoStatus = "notAttempted", Error = error };

    private string CanonicalTarget(string? computer) =>
        Same(computer, localName) ? localName : Same(computer, remoteName) ? remoteName :
        throw new ArgumentException("只能切到已配置的 Windows 或 Mac。", nameof(computer));

    private static bool Same(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private void ObserveRevision(long revision)
    {
        long observed;
        do
        {
            observed = Volatile.Read(ref highestObservedRevision);
            if (revision <= observed) return;
        } while (Interlocked.CompareExchange(ref highestObservedRevision, revision, observed) != observed);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync();
        await gate.WaitAsync();
        try { if (CurrentPreview.HasSession) await ReturnPreviewInsideAsync(); }
        finally { gate.Release(); }
        gate.Dispose();
        lifetime.Dispose();
    }

#if NIAN_FENCE_RESEARCH
    // Only for a coordinator exclusively owned by the manager gate. Unlike
    // DisposeAsync this never invokes the old preview's ReturnPreviewInsideAsync.
    internal async Task AbandonPreviewWithoutReturnForResearchAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Dispose();
        }
        finally { gate.Release(); }
    }
#endif
}
