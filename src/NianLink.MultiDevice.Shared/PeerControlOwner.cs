using System;
using System.Threading;
using System.Threading.Tasks;
using DesktopToolbox.DeskLink;

namespace NianLink.MultiDevice;


// A same-process guard only. A transport acknowledgement is not a native queue
// barrier; unknown operations remain locked for this owner's entire lifetime.
internal sealed class PeerControlOwner
{
    private readonly object gate = new();
    private bool uncertain, inFlight, stopping;
    private object? bound;
    private string? activeId;

    internal bool IsUncertain { get { lock (gate) return uncertain; } }
    internal bool HasUnresolvedOperation { get { lock (gate) return uncertain || inFlight || activeId is not null; } }
    internal const string UnconfirmedMessage = "键鼠操作尚未确认，请检查键鼠所在设备；本次尝试不能确认旧请求已经停止。";

    internal void Bind(object identity)
    {
        lock (gate)
        {
            if (bound is not null) throw new InvalidOperationException("旧连接尚未退役。");
            bound = identity ?? throw new ArgumentNullException(nameof(identity));
            stopping = false;
        }
    }
    internal void BeginStopping(object identity)
    {
        lock (gate)
        {
            if (bound is null) return; // A failed resource stop may be retried after retirement.
            RequireBound(identity);
            stopping = true;
            if (inFlight) uncertain = true;
        }
    }
    internal void Retire(object identity)
    {
        lock (gate)
        {
            if (!ReferenceEquals(bound, identity)) return;
            if (inFlight || activeId is not null) uncertain = true;
            bound = null;
        }
    }
    private void RequireBound(object identity)
    { if (!ReferenceEquals(bound, identity)) throw new InvalidOperationException("连接已更换，请使用当前窗口。"); }

    internal void Enter(object identity, bool begin = false, bool remoteSwitch = false, string? renewId = null)
    {
        lock (gate)
        {
            RequireBound(identity);
            if (uncertain || inFlight || stopping) throw new InvalidOperationException(UnconfirmedMessage);
            if ((begin || remoteSwitch) && activeId is not null || renewId is not null && renewId != activeId)
                throw new InvalidOperationException("请先结束当前预览操作。");
            inFlight = true;
        }
    }
    internal void Complete(object identity, string? newActiveId = null, bool updateActive = false)
    {
        lock (gate)
        {
            if (!ReferenceEquals(bound, identity)) { uncertain = true; inFlight = false; throw new InvalidOperationException(UnconfirmedMessage); }
            inFlight = false;
            if (uncertain) throw new InvalidOperationException(UnconfirmedMessage);
            if (updateActive) activeId = newActiveId;
        }
    }
    internal void Fail() { lock (gate) { uncertain = true; inFlight = false; } }
    internal bool EnterReturn(object identity, string? id = null)
    {
        lock (gate)
        {
            RequireBound(identity);
            if (uncertain || inFlight || activeId is not null && id != activeId || id is not null && id != activeId)
            { uncertain = true; return false; }
            inFlight = true;
            return true;
        }
    }
    internal bool IsCurrent(object identity) { lock (gate) return ReferenceEquals(bound, identity) && !stopping; }
}

internal sealed class GuardedPeerControl : IDeskflowPreviewControl
{
    private readonly PeerControlOwner owner;
    private readonly IDeskflowPreviewControl transport;
    private readonly Func<string, CancellationToken, Task<DeskLinkSwitchResult>> switchControl;
    private readonly string host, peer;
    internal GuardedPeerControl(PeerControlOwner owner, IDeskflowPreviewControl transport,
        Func<string, CancellationToken, Task<DeskLinkSwitchResult>> switchControl, string host, string peer)
    { this.owner = owner; this.transport = transport; this.switchControl = switchControl; this.host = host; this.peer = peer; }

    public bool PreviewHandoffSupported => owner.IsCurrent(transport) && !owner.IsUncertain && transport.PreviewHandoffSupported;
    public string PreviewReturnShortcut => transport.PreviewReturnShortcut;
    public string PreviewHandoffHint => !owner.IsCurrent(transport) ? "连接已更换，请使用当前窗口。" : owner.IsUncertain ? PeerControlOwner.UnconfirmedMessage : transport.PreviewHandoffHint;

    public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) =>
        NormalAsync(() => transport.BeginPreviewAsync(ct), state =>
            state.Phase == DeskflowPreviewPhase.Viewing && string.IsNullOrEmpty(state.Id) ||
            state.Phase == DeskflowPreviewPhase.Active && !string.IsNullOrEmpty(state.Id), ct, begin: true,
            active: state => state.Phase == DeskflowPreviewPhase.Active ? state.Id : null);

    public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) =>
        NormalAsync(() => transport.RenewPreviewAsync(id, ct), state =>
            state.Phase == DeskflowPreviewPhase.Active && state.Id == id, ct, renewId: id);

    public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default) =>
        ReturnAsync(() => transport.EndPreviewAsync(id, ct), state =>
            state.Phase == DeskflowPreviewPhase.Viewing && string.IsNullOrEmpty(state.Id), ct, id);

    public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) =>
        NormalAsync(() => transport.ReadPreviewAsync(ct), state => Enum.IsDefined(state.Phase), ct);

    public Task<DeskLinkSwitchResult> SwitchDetailedAsync(string target, CancellationToken ct = default)
    {
        if (!string.Equals(target, host, StringComparison.Ordinal) && !string.Equals(target, peer, StringComparison.Ordinal))
            throw new ArgumentException("请选择已配对的电脑。", nameof(target));
        return target == host ? ReturnAsync(() => switchControl(target, ct), ValidSwitch, ct) :
            NormalAsync(() => switchControl(target, ct), ValidSwitch, ct, remoteSwitch: true);
    }

    private static bool ValidSwitch(DeskLinkSwitchResult result) => result.Ok && result.InputTransferred;
    private async Task<T> NormalAsync<T>(Func<Task<T>> action, Func<T, bool> valid, CancellationToken ct,
        bool begin = false, bool remoteSwitch = false, string? renewId = null, Func<T, string?>? active = null)
    {
        owner.Enter(transport, begin, remoteSwitch, renewId);
        try
        {
            var result = await action().WaitAsync(ct).ConfigureAwait(false);
            if (result is null || !valid(result)) throw new InvalidOperationException(PeerControlOwner.UnconfirmedMessage);
            owner.Complete(transport, active?.Invoke(result), active is not null);
            return result;
        }
        catch { owner.Fail(); throw; }
    }
    private async Task<T> ReturnAsync<T>(Func<Task<T>> action, Func<T, bool> valid, CancellationToken ct, string? id = null)
    {
        var ordinary = owner.EnterReturn(transport, id);
        if (ordinary)
        {
            try
            {
                var result = await action().WaitAsync(ct).ConfigureAwait(false);
                if (result is null || !valid(result)) throw new InvalidOperationException(PeerControlOwner.UnconfirmedMessage);
                owner.Complete(transport, null, id is not null);
                return result;
            }
            catch { owner.Fail(); throw; }
        }
        try { _ = await action().WaitAsync(ct).ConfigureAwait(false); }
        catch (Exception error) { throw new InvalidOperationException(PeerControlOwner.UnconfirmedMessage, error); }
        throw new InvalidOperationException(PeerControlOwner.UnconfirmedMessage);
    }
}
