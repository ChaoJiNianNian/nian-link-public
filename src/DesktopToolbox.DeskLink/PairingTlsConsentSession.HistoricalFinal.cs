namespace DesktopToolbox.DeskLink;

internal sealed partial class PairingTlsConsentSession
{
    internal sealed partial class DeliveryLease
    {
        private MultiDeviceCredentialVault? recoveredVault;
        private PairingHistoricalSnapshot? verifiedHistoricalFinal;
        private MultiDeviceCredentialVault? historicalAppliedVault;
        private PairingHistoricalSnapshot? verifiedHistoricalApplied;

        internal void RequireVerifiedHistoricalApplied(MultiDeviceCredentialVault vault, PairingHistoricalSnapshot snapshot, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || !ReferenceEquals(historicalAppliedVault, vault) || verifiedHistoricalApplied != snapshot)
                    throw new InvalidOperationException("The historical applied acknowledgement is unavailable.");
            }
        }

        internal void RequireVerifiedHistoricalFinal(MultiDeviceCredentialVault vault, PairingHistoricalSnapshot snapshot, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || !ReferenceEquals(recoveredVault, vault) || verifiedHistoricalFinal != snapshot)
                    throw new InvalidOperationException("The historical final acknowledgement is unavailable.");
            }
        }

    }
}
