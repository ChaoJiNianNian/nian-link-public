using System.Text.Json;

namespace DesktopToolbox.DeskLink;

internal sealed partial class PairingTlsConsentSession
{
    internal sealed partial class DeliveryLease
    {
        private bool hostRetiredAckVerified;
        private PairingHistoricalSnapshot? historicalRetiredAck;
        private PeerInvitationStore? finishedPeerStore;
        private PairingHistoricalBinding? verifiedPeerHostFinished;

        internal void RequireVerifiedHostRetired(MultiDeviceCredentialVault vault, PairingPrepareReceipt receipt, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || !hostRetiredAckVerified || !ReferenceEquals(stagedVault, vault) || verifiedReady != receipt)
                    throw new InvalidOperationException("This pairing has no verified retirement acknowledgement.");
            }
        }

        internal void RequireVerifiedHistoricalRetired(MultiDeviceCredentialVault vault, PairingHistoricalSnapshot snapshot, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || !hostRetiredAckVerified || !ReferenceEquals(recoveredVault, vault) || historicalRetiredAck != snapshot)
                    throw new InvalidOperationException("Historical retirement acknowledgement is unavailable.");
            }
        }

        internal void RequireVerifiedPeerHostFinished(PeerInvitationStore store, PairingHistoricalBinding binding, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (IsHost || !ReferenceEquals(finishedPeerStore, store) || verifiedPeerHostFinished != binding)
                    throw new InvalidOperationException("Authenticated host completion is unavailable.");
            }
        }

        private async Task SendHostFinishedAndAwaitRetiredAsync(MultiDeviceCredentialVault vault, PairingPrepareReceipt receipt, CancellationToken ct)
        {
            await vault.SendFinishedFrameAsync(this, receipt,
                token => owner.WriteFinalFrameAsync("host-finished", receipt, token), ct).ConfigureAwait(false);
            using var reply = await owner.ReadDeliveryFrameAsync(ct).ConfigureAwait(false);
            owner.ValidateDeliveryHeader(reply.RootElement, "retired-ack", "peer", FinalFields);
            if (ReadDeliveryReceipt(reply.RootElement) != receipt)
                throw new InvalidDataException("Retirement acknowledgement differs from the transaction.");
            lock (owner.gate) { owner.CheckDelivery(this, ct); hostRetiredAckVerified = true; }
            await vault.MarkDeliveryRetiredAsync(this, receipt, ct).ConfigureAwait(false);
            owner.CheckDelivery(this, ct);
        }

        private async Task ReceiveHostFinishedAndRetireAsync(PeerInvitationStore store, PairingPrepareReceipt receipt, CancellationToken ct)
        {
            using var final = await owner.ReadDeliveryFrameAsync(ct).ConfigureAwait(false);
            owner.ValidateDeliveryHeader(final.RootElement, "host-finished", "host", FinalFields);
            if (ReadDeliveryReceipt(final.RootElement) != receipt)
                throw new InvalidDataException("Host completion differs from the transaction.");
            var current = store.Get() ?? throw new InvalidOperationException("Completed invitation is unavailable.");
            var binding = new PairingHistoricalBinding(Identity, receipt.StageId, receipt.Slot, receipt.DataPortBase,
                current.Host, current.Port, current.KeyboardPort, current.DataPort, receipt.InvitationDigest);
            lock (owner.gate) { owner.CheckDelivery(this, ct); finishedPeerStore = store; verifiedPeerHostFinished = binding; }
            await store.RetireCompletedAsync(this, binding, ct).ConfigureAwait(false);
            await owner.WriteFinalFrameAsync("retired-ack", receipt, ct).ConfigureAwait(false);
            owner.CheckDelivery(this, ct);
        }

        private async Task SendHistoricalHostFinishedAndAwaitRetiredAsync(MultiDeviceCredentialVault vault,
            PairingHistoricalSnapshot reviewed, CancellationToken ct)
        {
            var commitment = reviewed.Binding.Commitment();
            await vault.SendHistoricalFinishedFrameAsync(this, reviewed,
                token => owner.WriteHistoricalBindingFrameAsync("recovery-host-finished", reviewed.Binding, commitment, token), ct).ConfigureAwait(false);
            using var reply = await owner.ReadDeliveryFrameAsync(ct).ConfigureAwait(false);
            owner.ValidateDeliveryHeader(reply.RootElement, "recovery-retired-ack", "peer", RecoveryQueryFields);
            if (!SameDeliveryDigest(reply.RootElement.GetProperty("commitment").GetString() ?? "", commitment))
                throw new InvalidDataException("Historical retirement acknowledgement differs.");
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                recoveredVault = vault; historicalRetiredAck = reviewed; hostRetiredAckVerified = true;
            }
            await vault.MarkHistoricalDeliveryRetiredAsync(this, reviewed, ct).ConfigureAwait(false);
            owner.CheckDelivery(this, ct);
        }

        private async Task ReceiveHistoricalHostFinishedAndRetireAsync(PeerInvitationStore store, JsonElement root, CancellationToken ct)
        {
            owner.ValidateDeliveryHeader(root, "recovery-host-finished", "host", HistoricalReadyQueryFields);
            var json = root.GetProperty("binding");
            if (!ExactFields(json, PeerInvitationStore.HistoricalBindingFields) || !ExactFields(json.GetProperty("identity"), HistoricalIdentityFields))
                throw new InvalidDataException("Historical completion binding is invalid.");
            var binding = json.Deserialize<PairingHistoricalBinding>(JsonLineProtocol.Json)
                ?? throw new InvalidDataException("Historical completion binding is empty.");
            PairingHistoricalRecoveryRules.ValidateBinding(binding);
            PairingHistoricalRecoveryRules.RequireFreshConfirmedIdentity(this, binding.Identity, binding.Host);
            var commitment = root.GetProperty("commitment").GetString() ?? "";
            if (!SameDeliveryDigest(binding.Commitment(), commitment)) throw new InvalidDataException("Historical completion commitment differs.");
            lock (owner.gate) { owner.CheckDelivery(this, ct); finishedPeerStore = store; verifiedPeerHostFinished = binding; }
            await store.RetireCompletedAsync(this, binding, ct).ConfigureAwait(false);
            await owner.WriteRecoveryFrameAsync("recovery-retired-ack", commitment, ct).ConfigureAwait(false);
            owner.CheckDelivery(this, ct);
        }

        private async Task ReceiveHistoricalHostFinishedAndRetireAsync(PeerInvitationStore store, CancellationToken ct)
        {
            using var frame = await owner.ReadDeliveryFrameAsync(ct).ConfigureAwait(false);
            await ReceiveHistoricalHostFinishedAndRetireAsync(store, frame.RootElement, ct).ConfigureAwait(false);
        }
    }
}
