namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    internal async Task<bool> IsHistoricalFinishedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalSnapshot reviewed, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); lease.EnsureCurrent(ct);
            await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
            var current = await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
            if (!ReferenceEquals(reviewed.Owner, this) || reviewed != current || reviewed.Phase != PairingRecoveryPhase.CommittedAwaitingPeer)
                throw new InvalidOperationException("Historical pairing evidence changed.");
            var id = reviewed.Binding.Identity;
            var related = vaultSnapshot.Transactions.Where(x => x.TransactionId == id.TransactionId || x.StageId == reviewed.Binding.StageId ||
                x.SessionId == id.SessionId || x.OfferNonce == id.OfferNonce).ToArray();
            if (related.Length != 1 || related[0].State is not ("committed" or "applied" or "finished"))
                throw new InvalidOperationException("Historical pairing state is invalid.");
            lease.EnsureCurrent(ct);
            return related[0].State == "finished";
        }
        finally { gate.Release(); }
    }

    internal async Task SendFinishedFrameAsync(PairingTlsConsentSession.DeliveryLease lease, PairingPrepareReceipt receipt,
        Func<CancellationToken, Task> send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); lease.RequireVerifiedHostFinal(this, receipt, ct);
            var owned = lease.GetFrozenHost(ct);
            if (!ReferenceEquals(owned.Vault, this) || owned.StageId != receipt.StageId ||
                owned.Frozen.Slot != receipt.Slot || owned.Frozen.DataPortBase != receipt.DataPortBase ||
                !FixedDigestEquals(owned.Frozen.InvitationDigest, receipt.InvitationDigest))
                throw new InvalidOperationException("The finished pairing belongs to another transaction.");
            await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
            await VerifyPrepareMarkerLockedAsync(lease, receipt, ct, allowCommitted: true).ConfigureAwait(false);
            var pending = await ReadPendingGrantAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The finished pairing evidence is unavailable.");
            var id = lease.Identity;
            var related = vaultSnapshot.Transactions.Where(x => x.TransactionId == id.TransactionId || x.StageId == receipt.StageId ||
                x.SessionId == id.SessionId || x.OfferNonce == id.OfferNonce).ToArray();
            if (related.Length != 1 || related[0].State != "finished" || !MatchesCommittedRecovery(related[0], pending))
                throw new InvalidOperationException("The finished pairing no longer matches current trust.");
            lease.RequireVerifiedHostFinal(this, receipt, ct);
            await send(ct).ConfigureAwait(false);
            lease.RequireVerifiedHostFinal(this, receipt, ct);
        }
        finally { gate.Release(); }
    }

    internal async Task SendHistoricalFinishedFrameAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalSnapshot reviewed, Func<CancellationToken, Task> send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); lease.EnsureCurrent(ct);
            await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
            var current = await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
            if (!ReferenceEquals(reviewed.Owner, this) || reviewed != current || reviewed.Phase != PairingRecoveryPhase.CommittedAwaitingPeer)
                throw new InvalidOperationException("Historical finished evidence changed.");
            var id = reviewed.Binding.Identity;
            var related = vaultSnapshot.Transactions.Where(x => x.TransactionId == id.TransactionId || x.StageId == reviewed.Binding.StageId ||
                x.SessionId == id.SessionId || x.OfferNonce == id.OfferNonce).ToArray();
            if (related.Length != 1 || related[0].State != "finished")
                throw new InvalidOperationException("Historical pairing is no longer finished.");
            lease.EnsureCurrent(ct);
            await send(ct).ConfigureAwait(false);
            lease.EnsureCurrent(ct);
        }
        finally { gate.Release(); }
    }
}
