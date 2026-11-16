#if NIAN_FENCE_RESEARCH
namespace DesktopToolbox.DeskLink.Coordination;

// Research-only result: neither value proves older native work is drained.
public enum EmergencyReturnOutcome { AlreadyHostUnconfirmed, AttemptedUnconfirmed }

public sealed partial class MultiPeerPreviewManager
{
    private bool emergencyUnknown;
    private string? emergencyOwner;
    private void RequireNoEmergency()
    {
        if (emergencyUnknown) throw new InvalidOperationException("The emergency attempt remains unconfirmed.");
    }
    // Called only with gate held. Snapshot access does not expire a lease or
    // send a keyboard action.
    private void RequireEmergencyParticipant(string requester)
    {
        if (emergencyUnknown && emergencyOwner != requester) throw new UnauthorizedAccessException("Another attempt is unconfirmed.");
        var preview = current?.PreviewStateWithoutExpiry;
        if (preview is { HasSession: true } && requester != preview.ViewerComputer && requester != preview.TargetComputer)
            throw new UnauthorizedAccessException("Requester is outside this preview.");
    }

    // The caller MUST already authenticate the requester. This method alone is
    // not an authorization interface and is absent from ordinary builds.
    public async Task<EmergencyReturnOutcome> TryEmergencyReturnAsync(string requester, CancellationToken ct = default)
        => await TryEmergencyReturnCoreAsync(requester, null, ct);

    // Research-only condition: a revision change after observation rejects the
    // attempt. It does not establish that older native work has drained.
    internal async Task<EmergencyReturnOutcome> TryEmergencyReturnAtRevisionAsync(string requester, long revision, CancellationToken ct = default)
        => await TryEmergencyReturnCoreAsync(requester, revision, ct);

    internal async Task<long> ObserveEmergencyRevisionAsync(string requester, CancellationToken ct = default)
    {
        if (!peers.Contains(requester)) throw new UnauthorizedAccessException("Unknown peer.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await gate.WaitAsync(deadline.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RequireEmergencyParticipant(requester);
            var state = await keyboard.GetStateAsync(deadline.Token).WaitAsync(deadline.Token);
            if (state.ProcessState != DeskLinkProcessState.Running || !state.NativeControlAvailable || state.Revision < 0 ||
                !state.ConnectedComputers.Contains(requester, StringComparer.Ordinal) ||
                state.ActiveComputer != host && state.ActiveComputer != requester)
                throw new InvalidOperationException("Keyboard is unavailable or controlled by another device.");
            return state.Revision;
        }
        finally { gate.Release(); }
    }

    private async Task<EmergencyReturnOutcome> TryEmergencyReturnCoreAsync(string requester, long? expectedRevision, CancellationToken ct)
    {
        if (!peers.Contains(requester)) throw new UnauthorizedAccessException("Unknown peer.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await gate.WaitAsync(deadline.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RequireEmergencyParticipant(requester);
            var state = await keyboard.GetStateAsync(deadline.Token).WaitAsync(deadline.Token);
            if (state.ProcessState != DeskLinkProcessState.Running || !state.NativeControlAvailable || state.Revision < 0 ||
                !state.ConnectedComputers.Contains(requester, StringComparer.Ordinal) ||
                state.ActiveComputer != host && state.ActiveComputer != requester ||
                expectedRevision is not null && state.Revision != expectedRevision)
                throw new InvalidOperationException("Keyboard is unavailable or controlled by another device.");
            deadline.Token.ThrowIfCancellationRequested();
            emergencyUnknown = true;
            emergencyOwner = requester;
            displayGuard.RecordPreview(long.MaxValue);
            if (state.ActiveComputer == host) return EmergencyReturnOutcome.AlreadyHostUnconfirmed;
            try { await keyboard.SwitchIfCurrentAsync(host, state.ActiveComputer!, state.Revision, deadline.Token).WaitAsync(deadline.Token); }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException or ObjectDisposedException) { }
            return EmergencyReturnOutcome.AttemptedUnconfirmed;
        }
        finally { gate.Release(); }
    }
}
#endif
