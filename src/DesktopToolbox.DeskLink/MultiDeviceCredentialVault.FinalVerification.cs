namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    // Before FINAL is sent, recheck the current protected evidence and the
    // exact applied transaction under the vault gate. A stale or revoked grant
    // must never authorize finalization of the peer's pending invitation.
    internal async Task VerifyAppliedForFinalAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingPrepareReceipt receipt, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lease.RequireVerifiedHostApplied(this, receipt, ct);
            await VerifyCommittedDeliveryLockedAsync(lease, receipt, ct).ConfigureAwait(false);
            var id = lease.Identity;
            var related = vaultSnapshot.Transactions.Where(x => x.TransactionId == id.TransactionId ||
                x.StageId == receipt.StageId || x.SessionId == id.SessionId || x.OfferNonce == id.OfferNonce).ToArray();
            if (related.Length != 1 || related[0].State != "applied")
                throw new InvalidOperationException("The current pairing is not applied.");
            lease.RequireVerifiedHostApplied(this, receipt, ct);
        }
        finally { gate.Release(); }
    }
}
