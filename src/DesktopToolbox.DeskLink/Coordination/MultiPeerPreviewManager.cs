namespace DesktopToolbox.DeskLink.Coordination;

/// <summary>Serializes a single preview handoff across all devices, using the existing return lease.</summary>
public sealed partial class MultiPeerPreviewManager : IAsyncDisposable
{
    private readonly string host;
    private readonly HashSet<string> peers;
    private readonly IMultiPeerKeyboard keyboard;
    private readonly TimeProvider clock;
    private readonly IUnifiedDisplayControl? display;
    private readonly MultiPeerDisplayIntentGuard displayGuard;
    private readonly SemaphoreSlim gate = new(1, 1);
    private UnifiedSwitchCoordinator? current;
    private bool disposed;

    public MultiPeerPreviewManager(string host, IEnumerable<string> peers, IMultiPeerKeyboard keyboard, TimeProvider? clock = null,
        IUnifiedDisplayControl? display = null)
    {
        DeskflowProfile.ValidateName(host);
        ArgumentNullException.ThrowIfNull(peers);
        this.keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        this.host = host; this.clock = clock ?? TimeProvider.System;
        this.peers = new HashSet<string>(peers, StringComparer.Ordinal);
        if (this.peers.Count is < 1 or > 4 || this.peers.Contains(host)) throw new ArgumentException("One to four distinct peer names are required.");
        foreach (var peer in this.peers) DeskflowProfile.ValidateName(peer);
        this.display = display; displayGuard = new MultiPeerDisplayIntentGuard(host, this.peers);
    }

    public async Task<DeskflowPreviewState> BeginAsync(string viewer, string peer, CancellationToken ct = default)
    {
        if (!peers.Contains(peer) || viewer != host && viewer != peer) throw new UnauthorizedAccessException("The viewer and peer are not an approved pair.");
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
#if NIAN_FENCE_RESEARCH
            RequireNoEmergency();
#endif
            if (current is not null)
            {
                var previous = await ReadCurrentAsync(ct);
                if (previous.HasSession) throw new InvalidOperationException("Return the current preview before starting another.");
                await current.DisposeAsync(); current = null;
            }
            var coordinator = new UnifiedSwitchCoordinator(new ConditionalKeyboard(keyboard, host, peer), new NoDisplay(), host, peer, false, clock);
            current = coordinator;
            try { return Label(await coordinator.BeginPreviewAsync(viewer, ct)); }
            finally { if (coordinator.HasInputPreviewIntent) await RecordPreviewRevisionAsync(); }
        }
        finally { gate.Release(); }
    }

    public async Task<DeskflowPreviewState> RenewAsync(string viewer, string id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
#if NIAN_FENCE_RESEARCH
            RequireNoEmergency();
#endif
            var coordinator = current ?? throw new InvalidOperationException("No preview is active.");
            try { return Label(await coordinator.RenewPreviewAsync(id, viewer, ct)); }
            finally { if (coordinator.HasInputPreviewIntent) await RecordPreviewRevisionAsync(); }
        }
        finally { gate.Release(); }
    }

    public async Task<DeskflowPreviewState> EndAsync(string requester, string id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
#if NIAN_FENCE_RESEARCH
            RequireNoEmergency();
#endif
            var coordinator = current ?? throw new InvalidOperationException("No preview is active.");
            // Validate the owner and ID before a read: reading can expire the
            // current lease and issue a keyboard handoff for a newer session.
            var state = coordinator.PreviewStateWithoutExpiry;
            if (requester != state.ViewerComputer && requester != state.TargetComputer)
                throw new UnauthorizedAccessException("This device is not part of the current preview.");
            if (state.HasSession && state.Id != UnifiedSwitchCoordinator.PreviewId(id))
                throw new InvalidOperationException("此归还请求不属于当前预览操作。");
            await coordinator.ReadPreviewAsync(ct);
            try { return Label(await coordinator.EndPreviewAsync(id, ct)); }
            finally { if (coordinator.HasInputPreviewIntent) await RecordPreviewRevisionAsync(); }
        }
        finally { gate.Release(); }
    }

    public async Task<DeskflowPreviewState> ReadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(disposed, this);
#if NIAN_FENCE_RESEARCH
            RequireNoEmergency();
#endif
            return Label(await ReadCurrentAsync(ct)); }
        finally { gate.Release(); }
    }

    public async Task<DeskflowPreviewState> ReadForDeviceAsync(string device, CancellationToken ct = default)
    {
        DeskflowProfile.ValidateName(device);
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
#if NIAN_FENCE_RESEARCH
            RequireNoEmergency();
#endif
            if (current is null) return new DeskflowPreviewState();
            var state = await ReadCurrentAsync(ct);
            return device != state.ViewerComputer && device != state.TargetComputer
                ? new DeskflowPreviewState() : Label(state);
        }
        finally { gate.Release(); }
    }

    public async Task PrepareNormalSwitchAsync(string requester, CancellationToken ct = default)
    {
        DeskflowProfile.ValidateName(requester);
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
#if NIAN_FENCE_RESEARCH
            RequireNoEmergency();
#endif
            await PrepareNormalSwitchInsideAsync(requester, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<DeskLinkSwitchResult> SwitchNormalAsync(string requester, string target, CancellationToken ct = default)
    {
        if (requester != host && !peers.Contains(requester) || target != host && !peers.Contains(target) ||
            requester != host && target != host && target != requester)
            throw new UnauthorizedAccessException("Switch target is outside the approved device pair.");
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
#if NIAN_FENCE_RESEARCH
            RequireNoEmergency();
#endif
            await PrepareNormalSwitchInsideAsync(requester, ct);
            var state = await keyboard.GetStateAsync(ct);
            if (state.ProcessState != DeskLinkProcessState.Running || !state.NativeControlAvailable || state.Revision < 0 ||
                state.ActiveComputer is null || state.ActiveComputer != host && !peers.Contains(state.ActiveComputer) ||
                requester != host && state.ActiveComputer != host && state.ActiveComputer != requester ||
                target != host && !state.ConnectedComputers.Contains(target, StringComparer.Ordinal))
                throw new InvalidOperationException("The keyboard is unavailable or controlled by another device.");
            var result = await keyboard.SwitchIfCurrentAsync(target, state.ActiveComputer, state.Revision, ct);
            if (!result.Ok || !result.InputTransferred || result.ActiveComputer != target) return result;
            DeskLinkState after;
            try { after = await keyboard.GetStateAsync(ct); }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
            { return result with { Ok = false, InputTransferred = false, ActiveComputer = null, VideoStatus = "notAttempted", Error = "键鼠状态无法确认，未切换画面。" }; }
            if (after.Revision < state.Revision || after.ActiveComputer != target || !after.NativeControlAvailable || after.ProcessState != DeskLinkProcessState.Running)
                return result with { Ok = false, InputTransferred = false, ActiveComputer = after.ActiveComputer, VideoStatus = "notAttempted", Error = "键鼠状态已变化，未切换画面。" };
            displayGuard.RecordHandled(after.Revision);
            if (display is null) return result;
            try
            {
                var outcome = await display.ApplyAsync(target, ct);
                var status = outcome.Status is "verified" or "acceptedUnverified" or "failed" or "disabled" ? outcome.Status : "failed";
                return result with { Ok = status is "verified" or "disabled", VideoStatus = status, Error = outcome.Error };
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
            { return result with { Ok = false, VideoStatus = "failed", Error = "键鼠已交接，但显示器切换未确认。" }; }
        }
        finally { gate.Release(); }
    }

    private async Task PrepareNormalSwitchInsideAsync(string requester, CancellationToken ct)
    {
        if (current is null) return;
        var state = await ReadCurrentAsync(ct);
        if (!state.HasSession) return;
        if (requester != state.ViewerComputer && requester != state.TargetComputer)
            throw new UnauthorizedAccessException("Another device is using preview control.");
        try { state = await current.EndPreviewAsync(state.Id!, ct); }
        finally { if (current.HasInputPreviewIntent) await RecordPreviewRevisionAsync(); }
        if (state.HasSession) throw new InvalidOperationException("Preview keyboard return is not confirmed.");
    }

    public async Task OnFocusChangedAsync(FocusChangedEvent change, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
#if NIAN_FENCE_RESEARCH
            if (emergencyUnknown) return;
#endif
            var preview = await ReadCurrentAsync(ct);
            var state = await keyboard.GetStateAsync(ct);
            if (preview.HasSession) displayGuard.RecordPreview(state.Revision);
            if (displayGuard.Accept(change, state, preview.HasSession) && display is not null)
                await display.ApplyAsync(change.ActiveComputer, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<DeskflowPreviewState> ReadCurrentAsync(CancellationToken ct)
    {
        if (current is null) return new DeskflowPreviewState();
        var before = current.DecorateState(new DeskLinkState()).InputPreview;
        var result = await current.ReadPreviewAsync(ct);
        if (before?.HasSession == true && !result.HasSession && current.HasInputPreviewIntent) await RecordPreviewRevisionAsync();
        return result;
    }

    private async Task RecordPreviewRevisionAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            displayGuard.RecordPreview((await keyboard.GetStateAsync(timeout.Token).WaitAsync(timeout.Token)).Revision);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        { displayGuard.RecordPreview(long.MaxValue); }
    }

    private DeskflowPreviewState Label(DeskflowPreviewState state) => state.ViewerComputer != host && state.HasSession
        ? state with { ReturnShortcut = "请使用归还按钮" } : state;

    public async ValueTask DisposeAsync()
    {
#if NIAN_FENCE_RESEARCH
        using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await gate.WaitAsync(stopDeadline.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("Sharing cleanup is still unconfirmed."); }
#else
        await gate.WaitAsync();
#endif
        try
        {
            if (disposed) return;
            if (display is IDisplayOperationLifetime { HasUnresolvedOperation: true })
                throw new InvalidOperationException("显示器操作尚未确认，请等待完成或确认手动切换后再退出。");
#if NIAN_FENCE_RESEARCH
            if (emergencyUnknown)
            {
                if (current is not null) { await current.AbandonPreviewWithoutReturnForResearchAsync(stopDeadline.Token); current = null; }
                // An old native request could still arrive. Do not let runtime
                // treat the managed cleanup as a completed stop or reopen the
                // display path; repeat calls must retain this failure.
                throw new InvalidOperationException("键鼠归还及清理仍未确认，不能结束共享。");
            }
#endif
            if (current is not null)
            {
                var state = await current.ReadPreviewAsync(CancellationToken.None);
                if (state.HasSession && state.Id is not null) state = await current.EndPreviewAsync(state.Id, CancellationToken.None);
                if (state.HasSession) throw new InvalidOperationException("Preview keyboard return is not confirmed.");
                await current.DisposeAsync(); current = null;
            }
            disposed = true;
        }
        finally { gate.Release(); }
    }

    private sealed class NoDisplay : IUnifiedDisplayControl
    {
        public Task<DisplaySwitchOutcome> ApplyAsync(string computer, CancellationToken cancellationToken) => Task.FromResult(new DisplaySwitchOutcome("disabled"));
    }

    private sealed class ConditionalKeyboard(IMultiPeerKeyboard keyboard, string host, string peer) : IKeyboardHandoffControl
    {
        public async Task<NativeControlReply> GetStateAsync(CancellationToken cancellationToken) => Convert(await keyboard.GetStateAsync(cancellationToken));

        public async Task<NativeControlReply> SwitchAsync(string computer, CancellationToken cancellationToken)
        {
            var before = await keyboard.GetStateAsync(cancellationToken);
            if (before.ActiveComputer != host && before.ActiveComputer != peer)
                throw new InvalidOperationException("Keyboard focus moved to another device.");
            if (before.ActiveComputer is null || before.Revision < 0 || !before.NativeControlAvailable)
                throw new InvalidOperationException("Keyboard state is unavailable.");
            var result = await keyboard.SwitchIfCurrentAsync(computer, before.ActiveComputer, before.Revision, cancellationToken);
            var after = await keyboard.GetStateAsync(cancellationToken);
            var reply = Convert(after);
            return reply with { Ok = reply.Ok && result.Ok && result.InputTransferred && after.ActiveComputer == computer,
                Error = result.Error ?? reply.Error };
        }

        private static NativeControlReply Convert(DeskLinkState state) => new()
        {
            Ok = state.ProcessState == DeskLinkProcessState.Running && state.NativeControlAvailable,
            Revision = state.Revision, ActiveComputer = state.ActiveComputer, ConnectedComputers = state.ConnectedComputers.ToArray(),
            PreviewInputIsolated = state.PreviewInputIsolated, Error = state.LastError
        };
    }
}
