using System.Security.Cryptography;

namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    internal async Task<PairingHistoricalSnapshot> CaptureHistoricalRecoveryAsync(
        PairingTlsConsentSession.DeliveryLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<PairingHistoricalSnapshot> CaptureHistoricalRecoveryLockedAsync(
        PairingTlsConsentSession.DeliveryLease lease, CancellationToken ct)
    {
            ThrowIfDisposed(); lease.EnsureCurrent(ct);
            if (!lease.IsHost) throw new InvalidOperationException("Only the host can inspect its recovery evidence.");
            var state = await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
            if (state.Phase is not (PairingRecoveryPhase.AwaitingPeer or PairingRecoveryPhase.CommittedAwaitingPeer))
                throw new InvalidOperationException("Historical host pairing evidence is unavailable.");
            var snapshot = await ReadPendingGrantSnapshotAsync(ct).ConfigureAwait(false);
            var pending = snapshot.Document;
            if (pending?.Frozen is null || snapshot.Ciphertext is null)
                throw new InvalidOperationException("Historical host pairing evidence is incomplete.");
            var frozen = pending.Frozen;
            PairingHistoricalRecoveryRules.RequireFreshConfirmedIdentity(lease, pending.Identity, frozen.Host);
            var binding = new PairingHistoricalBinding(pending.Identity, pending.StageId, pending.Slot,
                frozen.DataPortBase, frozen.Host, frozen.ControlPort, frozen.KeyboardPort, frozen.DataPort, frozen.InvitationDigest);
            var marker = await ReadBoundedVaultFileAsync(PrepareMarkerPath, ct).ConfigureAwait(false);
            var active = File.Exists(file) ? await ReadBoundedVaultFileAsync(file, ct, MaxVaultFileBytes).ConfigureAwait(false) : null;
            lease.EnsureCurrent(ct);
            return new(this, state.Phase, binding, Convert.ToHexString(SHA256.HashData(snapshot.Ciphertext)),
                Convert.ToHexString(SHA256.HashData(marker)), active is null ? null : Convert.ToHexString(SHA256.HashData(active)));
    }
}
