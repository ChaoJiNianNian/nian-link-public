using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace EmergencyReturn.Research;

// In-process research wrapper only: direct manager, native or other-process calls
// bypass it. The synthetic authorization has no relationship to the real vault.
internal enum ResearchReturn { Unauthorized, Rejected, AlreadyHostUnconfirmed, AttemptedUnconfirmed }

internal sealed class ResearchAuthorization
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> grants = new(StringComparer.Ordinal);
    public void Add(string peer, string token) { lock (gate) grants.Add(peer, token); }
    public bool Revoke(string peer) { lock (gate) return grants.Remove(peer); }
    public bool IsAuthorized(string peer, string token) { lock (gate) return grants.TryGetValue(peer, out var known) && known == token; }
}

internal sealed class EmergencyManagerResearch(MultiPeerPreviewManager manager, IMultiPeerKeyboard keyboard,
    ResearchAuthorization auth, string host, IReadOnlyCollection<string> approved) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<string> approvedPeers = new(approved, StringComparer.Ordinal);
    private bool unconfirmed, disposed;
    private string? owner;
    private DeskflowPreviewState? observedPreview;
    public bool Unconfirmed => Volatile.Read(ref unconfirmed);

    private void RequireNormal()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (unconfirmed) throw new InvalidOperationException("Research attempt remains unconfirmed.");
    }
    public async Task<DeskflowPreviewState> BeginAsync(string peer)
    {
        await gate.WaitAsync();
        try { RequireNormal(); return observedPreview = await manager.BeginAsync(peer, peer); }
        finally { gate.Release(); }
    }
    public async Task<DeskflowPreviewState> RenewAsync(string peer, string id)
    {
        await gate.WaitAsync();
        try { RequireNormal(); return observedPreview = await manager.RenewAsync(peer, id); }
        finally { gate.Release(); }
    }
    public async Task<DeskflowPreviewState> ReadAsync(string peer)
    {
        await gate.WaitAsync();
        try { RequireNormal(); return observedPreview = await manager.ReadForDeviceAsync(peer); }
        finally { gate.Release(); }
    }
    public async Task<DeskLinkSwitchResult> NormalSwitchAsync(string peer, string target)
    {
        await gate.WaitAsync();
        try { RequireNormal(); return await manager.SwitchNormalAsync(peer, target); }
        finally { gate.Release(); }
    }
    public async Task OnFocusAsync(FocusChangedEvent change)
    {
        await gate.WaitAsync();
        try { ObjectDisposedException.ThrowIf(disposed, this); if (!unconfirmed) await manager.OnFocusChangedAsync(change); }
        finally { gate.Release(); }
    }
    public async Task<ResearchReturn> TryReturnAsync(string peer, string token)
    {
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // The approved list is an injected synthetic test assumption, not
            // evidence from the real manager or the real credential vault.
            if (string.Equals(peer, host, StringComparison.OrdinalIgnoreCase) || !approvedPeers.Contains(peer) ||
                !auth.IsAuthorized(peer, token)) return ResearchReturn.Unauthorized;
            if (unconfirmed && owner != peer) return ResearchReturn.Rejected;
            if (observedPreview is { HasSession: true } preview &&
                peer != preview.ViewerComputer && peer != preview.TargetComputer) return ResearchReturn.Rejected;
            DeskLinkState state;
            try { state = await keyboard.GetStateAsync(default); }
            catch { return ResearchReturn.Rejected; }
            if (state.ProcessState != DeskLinkProcessState.Running || !state.NativeControlAvailable || state.Revision < 0 ||
                !state.ConnectedComputers.Contains(peer, StringComparer.Ordinal) ||
                state.ActiveComputer != host && state.ActiveComputer != peer) return ResearchReturn.Rejected;
            // From here neither success nor a failure can prove older commands stopped.
            unconfirmed = true; owner = peer;
            if (state.ActiveComputer == host) return ResearchReturn.AlreadyHostUnconfirmed;
            try
            {
                await keyboard.SwitchIfCurrentAsync(host, state.ActiveComputer!, state.Revision, default);
            }
            catch { /* The action remains unconfirmed, even on a failed response. */ }
            return ResearchReturn.AttemptedUnconfirmed;
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            // Deliberately do not invoke the actual manager's Dispose in an
            // unconfirmed state: current code would attempt an old handoff.
            if (!unconfirmed) await manager.DisposeAsync();
        }
        finally { gate.Release(); }
    }
}
