namespace DesktopToolbox.DeskLink;

internal sealed partial class PairingTlsConsentSession
{
    internal sealed partial class DeliveryLease
    {
        internal PairingHistoricalBinding GetVerifiedHistoricalBinding(CancellationToken ct = default)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (recoveredVault is null || historicalRetiredAck is null)
                    throw new InvalidOperationException("Historical final acknowledgement is unavailable.");
                RequireVerifiedHistoricalRetired(recoveredVault, historicalRetiredAck, ct);
                return historicalRetiredAck.Binding;
            }
        }

        internal async Task CleanupHistoricalFinishedDeliveryAsync(CancellationToken ct = default)
        {
            MultiDeviceCredentialVault vault;
            PairingHistoricalSnapshot reviewed;
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (recoveredVault is null || historicalRetiredAck is null)
                    throw new InvalidOperationException("Historical final acknowledgement is unavailable.");
                vault = recoveredVault; reviewed = historicalRetiredAck;
                RequireVerifiedHistoricalRetired(vault, reviewed, ct);
                owner.activeOperations++;
            }
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                var status = await vault.GetPairingRecoveryStatusAsync(deadline.Token).ConfigureAwait(false);
                RequireVerifiedHistoricalRetired(vault, reviewed, deadline.Token);
                if (!status.MatchesFinishedDelivery(vault, reviewed.Binding.Identity.TransactionId, reviewed.Binding.StageId) ||
                    !await vault.CleanupAppliedPairingAsync(status, deadline.Token).ConfigureAwait(false))
                    throw new InvalidOperationException("Historical pairing evidence changed before cleanup.");
                owner.CheckDelivery(this, deadline.Token);
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }

        // Only this session's durably finished transaction may be cleaned up.
        // The store rechecks the complete protected evidence under its own gate.
        internal async Task CleanupFinishedDeliveryAsync(CancellationToken ct = default)
        {
            MultiDeviceCredentialVault vault;
            PairingPrepareReceipt receipt;
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || stagedVault is null || verifiedReady is null)
                    throw new InvalidOperationException("A completed pairing is unavailable.");
                vault = stagedVault; receipt = verifiedReady;
                RequireVerifiedHostRetired(vault, receipt, ct);
                owner.activeOperations++;
            }
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                var status = await vault.GetPairingRecoveryStatusAsync(deadline.Token).ConfigureAwait(false);
                RequireVerifiedHostRetired(vault, receipt, deadline.Token);
                if (!status.MatchesFinishedDelivery(vault, Identity.TransactionId, receipt.StageId) ||
                    !await vault.CleanupAppliedPairingAsync(status, deadline.Token).ConfigureAwait(false))
                    throw new InvalidOperationException("The finished pairing evidence changed before cleanup.");
                owner.CheckDelivery(this, deadline.Token);
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }
    }
}
