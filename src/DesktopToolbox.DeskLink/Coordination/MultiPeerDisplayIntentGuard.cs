namespace DesktopToolbox.DeskLink.Coordination;

/// <summary>Rejects stale or preview-only native focus notifications before any monitor command.</summary>
public sealed class MultiPeerDisplayIntentGuard
{
    private readonly string host;
    private readonly HashSet<string> peers;
    private long suppressedThrough = -1;
    private long appliedThrough = -1;

    public MultiPeerDisplayIntentGuard(string host, IEnumerable<string> peers)
    {
        DeskflowProfile.ValidateName(host);
        this.host = host;
        this.peers = new HashSet<string>(peers ?? throw new ArgumentNullException(nameof(peers)), StringComparer.Ordinal);
        if (this.peers.Count is < 1 or > 4 || this.peers.Contains(host)) throw new ArgumentException("One to four peer names are required.");
        foreach (var peer in this.peers) DeskflowProfile.ValidateName(peer);
    }

    // Callers serialize this guard with the keyboard handoff and display command.
    public void RecordPreview(long revision) { if (revision >= 0) suppressedThrough = Math.Max(suppressedThrough, revision); }
    public void RecordHandled(long revision) { if (revision >= 0) appliedThrough = Math.Max(appliedThrough, revision); }

    public bool Accept(FocusChangedEvent change, DeskLinkState state, bool previewActive)
    {
        ArgumentNullException.ThrowIfNull(change); ArgumentNullException.ThrowIfNull(state);
        if (change.Revision < 0 || change.ActiveComputer != host && !peers.Contains(change.ActiveComputer)) return false;
        if (previewActive) { RecordPreview(change.Revision); return false; }
        if (change.Revision <= suppressedThrough || change.Revision <= appliedThrough ||
            state.ProcessState != DeskLinkProcessState.Running || !state.NativeControlAvailable ||
            state.Revision != change.Revision || state.ActiveComputer != change.ActiveComputer ||
            !state.ConnectedComputers.Any(peers.Contains) ||
            change.ActiveComputer != host && !state.ConnectedComputers.Contains(change.ActiveComputer, StringComparer.Ordinal)) return false;
        appliedThrough = change.Revision;
        return true;
    }
}
