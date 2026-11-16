namespace DesktopToolbox.DeskLink.Coordination;

public sealed partial class UnifiedSwitchCoordinator
{
    private readonly TimeProvider previewClock;
    private static readonly TimeSpan PreviewLease = TimeSpan.FromSeconds(8);
    private sealed record PreviewSnapshot(DeskflowPreviewState State, long ThroughRevision = -1, bool InputPreviewIntent = false);
    private PreviewSnapshot previewSnapshot = new(new());
    private long previewLeaseStartedAt, previewCommittedRevision = -1;

    /// <summary>Lifecycle cleanup must not turn an input-only operation into a normal display switch.</summary>
    public bool HasInputPreviewIntent
    {
        get { var snapshot = Volatile.Read(ref previewSnapshot); return snapshot.State.HasSession || snapshot.InputPreviewIntent; }
    }

    public Task<DeskflowPreviewState> BeginPreviewAsync(string viewerComputer, CancellationToken cancellationToken = default)
    {
        var viewer = CanonicalTarget(viewerComputer);
        return WithPreviewGateAsync(token => BeginPreviewInsideAsync(viewer, token), cancellationToken);
    }

    public Task<DeskflowPreviewState> RenewPreviewAsync(string id, string viewerComputer, CancellationToken cancellationToken = default)
    {
        id = PreviewId(id); var viewer = CanonicalTarget(viewerComputer);
        return WithPreviewGateAsync(async token =>
        {
            var current = CurrentPreview;
            if (!current.HasSession || current.Id != id) throw new InvalidOperationException("预览操作已经结束或已被新的操作替代。");
            if (!Same(current.ViewerComputer, viewer)) throw new UnauthorizedAccessException("只有本次预览的观看端可以续期。");
            if (!current.IsActive || PreviewLeaseExpired()) return await ReturnPreviewInsideAsync();
            try
            {
                var observed = await keyboard.GetStateAsync(token); MarkPreviewRevision(observed.Revision);
                if (observed.Ok && observed.Revision >= 0 && observed.Revision >= Volatile.Read(ref highestObservedRevision) && Same(observed.ActiveComputer, current.ViewerComputer)) return CompletePreview(observed.Revision, "键鼠已回到观看端。");
                if (!observed.Ok || !Same(observed.ActiveComputer, current.TargetComputer)) return await ReturnPreviewInsideAsync();
            }
            catch (Exception error) when (HandoffFailure(error)) { return await ReturnPreviewInsideAsync(); }
            previewLeaseStartedAt = previewClock.GetTimestamp();
            CommitPreview(current with { ExpiresAtUtc = previewClock.GetUtcNow() + PreviewLease });
            return CurrentPreview;
        }, cancellationToken);
    }

    public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken cancellationToken = default)
    {
        id = PreviewId(id);
        return WithPreviewGateAsync(_ =>
        {
            if (!CurrentPreview.HasSession) return Task.FromResult(CurrentPreview);
            if (CurrentPreview.Id != id) throw new InvalidOperationException("此归还请求不属于当前预览操作。");
            return ReturnPreviewInsideAsync();
        }, cancellationToken);
    }

    public Task<DeskflowPreviewState> ExpirePreviewAsync(CancellationToken cancellationToken = default) => WithPreviewGateAsync(_ =>
        CurrentPreview.HasSession && PreviewLeaseExpired() ? ReturnPreviewInsideAsync() : Task.FromResult(CurrentPreview), cancellationToken);

    public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken = default) => ExpirePreviewAsync(cancellationToken);

    public DeskLinkState DecorateState(DeskLinkState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // A paired state read may observe a native revision before its queued focus callback.
        // Preserve that revision atomically with the suppression snapshot, without awaiting the handoff gate.
        var snapshot = Volatile.Read(ref previewSnapshot);
        bool Suppressed(PreviewSnapshot value) => value.State.HasSession || value.InputPreviewIntent &&
            (!state.NativeControlAvailable || state.Revision < 0 ||
             !Same(state.ActiveComputer, localName) && !Same(state.ActiveComputer, remoteName) || !RemoteConnected(state.ConnectedComputers));
        if (Suppressed(snapshot)) ObserveRevision(state.Revision);
        while (Suppressed(snapshot) && state.Revision > snapshot.ThroughRevision)
        {
            var next = snapshot with { ThroughRevision = state.Revision };
            var previous = Interlocked.CompareExchange(ref previewSnapshot, next, snapshot);
            if (ReferenceEquals(previous, snapshot)) { snapshot = next; break; }
            snapshot = previous;
        }
        return state with { PreviewHandoffSupported = true, InputPreview = snapshot.State,
            DisplaySuppressed = Suppressed(snapshot), DisplaySuppressedThroughRevision = snapshot.ThroughRevision };
    }

    private DeskflowPreviewState CurrentPreview => Volatile.Read(ref previewSnapshot).State;
    // Reading the immutable snapshot does not expire the lease or alter its
    // revision suppression watermark as DecorateState can. The manager owns
    // this coordinator and serializes its operations before checking it.
    internal DeskflowPreviewState PreviewStateWithoutExpiry => CurrentPreview;
    private bool PreviewLeaseExpired()
    {
        var elapsed = previewClock.GetElapsedTime(previewLeaseStartedAt, previewClock.GetTimestamp());
        return elapsed < TimeSpan.Zero || elapsed >= PreviewLease;
    }

    private async Task<DeskflowPreviewState> WithPreviewGateAsync(Func<CancellationToken, Task<DeskflowPreviewState>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(linked.Token);
        try { linked.Token.ThrowIfCancellationRequested(); return await action(linked.Token); }
        finally { gate.Release(); }
    }

    private async Task<DeskflowPreviewState> BeginPreviewInsideAsync(string viewer, CancellationToken token)
    {
        if (CurrentPreview.HasSession) throw new InvalidOperationException("已有预览操作尚未归还，请先停止当前操作。");
        var target = viewer == localName ? remoteName : localName;
        previewLeaseStartedAt = previewClock.GetTimestamp(); previewCommittedRevision = Volatile.Read(ref highestObservedRevision);
        CommitPreview(new() { Id = Guid.NewGuid().ToString("N"), ViewerComputer = viewer, TargetComputer = target,
            Phase = DeskflowPreviewPhase.Preparing, ReturnShortcut = viewer == localName ? "Ctrl+Alt+F11" : "Ctrl+Alt+F12",
            Message = "正在交接键鼠，显示器保持原输入源。", ExpiresAtUtc = previewClock.GetUtcNow() + PreviewLease });
        try
        {
            var before = await keyboard.GetStateAsync(token); MarkPreviewRevision(before.Revision);
            previewCommittedRevision = Math.Max(previewCommittedRevision, before.Revision);
            if (before.Ok && before.Revision >= 0 && !Same(before.ActiveComputer, viewer))
            {
                const string message = "先把共享键鼠切到当前电脑，再点开始操作。";
                CommitPreview(CurrentPreview with { Id = null, Phase = DeskflowPreviewPhase.Viewing, ExpiresAtUtc = default, Message = message }, before.Revision);
                lastHandledRevision = Math.Max(lastHandledRevision, Volatile.Read(ref previewSnapshot).ThroughRevision);
                Publish(Failed(viewer, before.ActiveComputer, message));
                return CurrentPreview;
            }
            if (!before.Ok || before.Revision < 0 || !before.ConnectedComputers.Any(name => Same(name, remoteName)))
            {
                if (before.Ok && before.Revision >= 0 && Same(before.ActiveComputer, viewer)) return CompletePreview(before.Revision, "键鼠连接尚未就绪，未开始操作。");
                return await ReturnPreviewInsideAsync();
            }
            CommitPreview(CurrentPreview, inputPreviewIntent: true);
            var reply = await keyboard.SwitchAsync(target, token); MarkPreviewRevision(reply.Revision);
            previewCommittedRevision = Math.Max(previewCommittedRevision, reply.Revision);
            if (!reply.Ok || reply.Revision < 0 || !Same(reply.ActiveComputer, target) || reply.Revision < Volatile.Read(ref highestObservedRevision))
                return await ReturnPreviewInsideAsync();
            previewCommittedRevision = reply.Revision; previewLeaseStartedAt = previewClock.GetTimestamp();
            CommitPreview(CurrentPreview with { Phase = DeskflowPreviewPhase.Active, ExpiresAtUtc = previewClock.GetUtcNow() + PreviewLease,
                Message = "键鼠已交接；使用返回快捷键或停止按钮回到观看端。" });
            Publish(InputOnlyResult(target)); return CurrentPreview;
        }
        catch (Exception error) when (HandoffFailure(error)) { return await ReturnPreviewInsideAsync(); }
    }

    private async Task<DeskflowPreviewState> ReturnPreviewInsideAsync()
    {
        var current = CurrentPreview;
        if (!current.HasSession) return current;
        CommitPreview(current with { Phase = DeskflowPreviewPhase.Returning, Message = "正在归还键鼠，显示器保持原输入源。" }, inputPreviewIntent: true);
        // Once a handoff may have reached the core, caller cancellation cannot assert that it did not happen.
        // Use a bounded cleanup request; an uncertain reply keeps Recovery and its suppression marker.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var reply = await keyboard.SwitchAsync(current.ViewerComputer, deadline.Token); MarkPreviewRevision(reply.Revision);
            previewCommittedRevision = Math.Max(previewCommittedRevision, reply.Revision);
            if (reply.Ok && reply.Revision >= 0 && Same(reply.ActiveComputer, current.ViewerComputer) && reply.Revision >= Volatile.Read(ref highestObservedRevision))
                return CompletePreview(reply.Revision, "键鼠已回到观看端，显示器保持原输入源。");
        }
        catch (Exception error) when (HandoffFailure(error)) { }
        previewLeaseStartedAt = previewClock.GetTimestamp();
        CommitPreview(current with { Phase = DeskflowPreviewPhase.Recovery, ExpiresAtUtc = previewClock.GetUtcNow() + PreviewLease,
            Message = "尚未确认键鼠归还，请重试停止或使用返回快捷键；显示联动仍暂停。" });
        Publish(new() { Target = current.ViewerComputer, VideoStatus = "disabled", Error = CurrentPreview.Message });
        return CurrentPreview;
    }

    private DeskflowPreviewState CompletePreview(long revision, string message)
    {
        var current = CurrentPreview;
        CommitPreview(current with { Id = null, Phase = DeskflowPreviewPhase.Viewing, ExpiresAtUtc = default, Message = message }, revision);
        lastHandledRevision = Math.Max(lastHandledRevision, Volatile.Read(ref previewSnapshot).ThroughRevision);
        Publish(InputOnlyResult(current.ViewerComputer)); return CurrentPreview;
    }

    private void CommitPreview(DeskflowPreviewState state, long revision = -1, bool? inputPreviewIntent = null)
    {
        PreviewSnapshot previous, next;
        do
        {
            previous = Volatile.Read(ref previewSnapshot);
            next = new(state, Math.Max(previous.ThroughRevision, Math.Max(revision, Volatile.Read(ref highestObservedRevision))),
                inputPreviewIntent ?? previous.InputPreviewIntent);
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref previewSnapshot, next, previous), previous));
    }

    private void MarkPreviewRevision(long revision)
    {
        ObserveRevision(revision); CommitPreview(CurrentPreview, revision);
    }

    private async Task<bool> SuppressPreviewFocusAsync(FocusChangedEvent change, CancellationToken token)
    {
        var snapshot = Volatile.Read(ref previewSnapshot);
        if (!snapshot.State.HasSession)
        {
            if (change.Revision <= snapshot.ThroughRevision) return true;
            if (!snapshot.InputPreviewIntent) return false;
            // A disconnected secondary also generates a new local focus revision. It is not a user hotkey.
            try
            {
                var current = await keyboard.GetStateAsync(token);
                snapshot = Volatile.Read(ref previewSnapshot);
                if (change.Revision > snapshot.ThroughRevision && current.Ok && RemoteConnected(current.ConnectedComputers) &&
                    current.Revision == change.Revision && Same(current.ActiveComputer, change.ActiveComputer) &&
                    change.Revision >= Volatile.Read(ref highestObservedRevision))
                {
                    ClearInputPreviewIntent();
                    return false;
                }
                ObserveRevision(current.Revision);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (HandoffFailure(error)) { }
            MarkPreviewRevision(change.Revision); lastHandledRevision = Math.Max(lastHandledRevision, change.Revision);
            return true;
        }
        MarkPreviewRevision(change.Revision); lastHandledRevision = Math.Max(lastHandledRevision, change.Revision);
        if (previewCommittedRevision >= 0 && change.Revision > previewCommittedRevision && change.Revision >= Volatile.Read(ref highestObservedRevision) && Same(change.ActiveComputer, snapshot.State.ViewerComputer))
            CompletePreview(change.Revision, "键鼠已通过返回快捷键回到观看端。");
        return true;
    }

    private bool RemoteConnected(IReadOnlyList<string> computers) => computers.Any(name => Same(name, remoteName));

    private void ClearInputPreviewIntent()
    {
        // This confirmed normal intent is not suppressed: do not add its revision to the Mac's watermark.
        PreviewSnapshot previous;
        do { previous = Volatile.Read(ref previewSnapshot); }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref previewSnapshot, previous with { InputPreviewIntent = false }, previous), previous));
    }

    internal static string PreviewId(string id) => Guid.TryParseExact(id, "N", out var parsed)
        ? parsed.ToString("N") : throw new ArgumentException("预览操作标识无效。", nameof(id));
    private static bool HandoffFailure(Exception error) => error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException
        or InvalidOperationException or TimeoutException or OperationCanceledException or NotSupportedException;
    private static DeskLinkSwitchResult InputOnlyResult(string target) => new() { Ok = true, Target = target, ActiveComputer = target, InputTransferred = true, VideoStatus = "disabled" };
}
