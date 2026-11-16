namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    // Serialize the committed/applied proof write with revoke and vault changes.
    // The callback writes only the current authenticated session's protocol frame.
    internal async Task SendHistoricalCommittedFrameAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalSnapshot reviewed, bool requireApplied, Func<CancellationToken, Task> send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease); ArgumentNullException.ThrowIfNull(reviewed); ArgumentNullException.ThrowIfNull(send);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); lease.EnsureCurrent(ct);
            await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
            var current = await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
            if (!ReferenceEquals(reviewed.Owner, this) || reviewed != current ||
                reviewed.Phase != PairingRecoveryPhase.CommittedAwaitingPeer)
                throw new InvalidOperationException("Historical pairing evidence changed.");
            var id = reviewed.Binding.Identity;
            var related = vaultSnapshot.Transactions.Where(x => x.TransactionId == id.TransactionId ||
                x.StageId == reviewed.Binding.StageId || x.SessionId == id.SessionId || x.OfferNonce == id.OfferNonce).ToArray();
            if (related.Length != 1 || (requireApplied ? related[0].State != "applied" :
                related[0].State is not ("committed" or "applied")))
                throw new InvalidOperationException("Historical pairing is no longer committed.");
            lease.EnsureCurrent(ct);
            await send(ct).ConfigureAwait(false);
            lease.EnsureCurrent(ct);
        }
        finally { gate.Release(); }
    }
}
