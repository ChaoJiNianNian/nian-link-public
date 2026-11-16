namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    /// <summary>Finish only the reviewed, already durably aborted transaction's local cleanup.</summary>
    public async Task<bool> CleanupAbortedPairingAsync(PairingRecoveryStatus reviewed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await CleanupAbortedPairingLockedAsync(reviewed, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<bool> CleanupAbortedPairingLockedAsync(PairingRecoveryStatus reviewed, CancellationToken ct)
    {
        ThrowIfDisposed();
        var current = await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
        if (!reviewed.MatchesAbortedCleanup(current)) return false;
        RejectLinks(directory); RejectLinks(PrepareMarkerPath); RejectLinks(PendingGrantPath);
        ct.ThrowIfCancellationRequested();
        // If execution stops after the first deletion, pending plus the aborted
        // ledger still prove the exact transaction and allow the second deletion.
        if (File.Exists(PrepareMarkerPath)) File.Delete(PrepareMarkerPath);
        ct.ThrowIfCancellationRequested();
        File.Delete(PendingGrantPath);
        return true;
    }
}
