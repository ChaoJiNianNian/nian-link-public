namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    /// <summary>
    /// Removes only local delivery evidence belonging to a durably retired transaction.
    /// The active credential, ledger and all other devices are retained. A partial deletion can be retried.
    /// </summary>
    public async Task<bool> CleanupAppliedPairingAsync(PairingRecoveryStatus reviewed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
            if (!reviewed.MatchesAppliedCleanup(current)) return false;
            RejectLinks(directory); RejectLinks(PrepareMarkerPath); RejectLinks(PendingGrantPath);
            ct.ThrowIfCancellationRequested();
            // The pending record remains a complete, protected basis for resuming cleanup
            // if deletion or process execution stops after the marker is removed.
            if (File.Exists(PrepareMarkerPath)) File.Delete(PrepareMarkerPath);
            File.Delete(PendingGrantPath);
            return true;
        }
        finally { gate.Release(); }
    }
}
