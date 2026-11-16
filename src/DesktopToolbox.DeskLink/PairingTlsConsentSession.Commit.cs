namespace DesktopToolbox.DeskLink;

internal sealed partial class PairingTlsConsentSession
{
    internal sealed partial class DeliveryLease
    {
        private bool hostCommitted;
        // Only the authenticated, confirmed connection that received READY can commit.
        internal async Task CommitVerifiedReadyAsync(CancellationToken ct = default)
        {
            var (vault, _, _) = GetFrozenHost(ct);
            owner.BeginDeliveryOperation(this, ct);
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                await vault.CommitVerifiedReadyAsync(this, deadline.Token).ConfigureAwait(false);
                lock (owner.gate) { owner.CheckDelivery(this, deadline.Token); hostCommitted = true; }
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }

        // Linearize publication with session cancellation and expiry. The caller owns
        // the vault gate and has already validated both protected files.
        internal void PublishCommittedVault(string temporary, string destination, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                File.Move(temporary, destination, true);
            }
        }
    }
}
