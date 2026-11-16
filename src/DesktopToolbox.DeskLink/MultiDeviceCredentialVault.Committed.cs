namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    // A sent COMMITTED message must correspond to this vault's actual committed
    // transaction and currently active device, never just to an in-memory flag.
    internal async Task VerifyCommittedDeliveryAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingPrepareReceipt receipt, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await VerifyCommittedDeliveryLockedAsync(lease, receipt, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task VerifyCommittedDeliveryLockedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingPrepareReceipt receipt, CancellationToken ct)
    {
        ThrowIfDisposed(); lease.EnsureCurrent(ct);
        var owned = lease.GetFrozenHost(ct);
        if (!ReferenceEquals(owned.Vault, this) || owned.StageId != receipt.StageId ||
            owned.Frozen.Slot != receipt.Slot || owned.Frozen.DataPortBase != receipt.DataPortBase ||
            !FixedDigestEquals(owned.Frozen.InvitationDigest, receipt.InvitationDigest))
            throw new InvalidOperationException("Committed delivery belongs to another pairing.");
        await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
        await VerifyPrepareMarkerLockedAsync(lease, receipt, ct, allowCommitted: true).ConfigureAwait(false);
        var pending = await ReadPendingGrantAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Committed pairing evidence is unavailable.");
        var related = vaultSnapshot.Transactions.Where(x => x.TransactionId == lease.Identity.TransactionId ||
            x.StageId == receipt.StageId || x.SessionId == lease.Identity.SessionId || x.OfferNonce == lease.Identity.OfferNonce).ToArray();
        if (related.Length != 1 || !MatchesCommittedRecovery(related[0], pending))
            throw new InvalidOperationException("Committed pairing is no longer active.");
        lease.EnsureCurrent(ct);
    }
}
