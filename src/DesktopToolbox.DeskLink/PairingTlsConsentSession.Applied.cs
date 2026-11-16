namespace DesktopToolbox.DeskLink;

internal sealed partial class PairingTlsConsentSession
{
    private static readonly string[] AppliedFields = ["version", "type", "role", "sessionId", "transactionId", "offerNonce", "stageId", "senderChallenge", "receiverChallenge", "slot", "dataPortBase", "digest"];

    internal sealed partial class DeliveryLease
    {
        private int promotionStarted, appliedReceiveStarted;
        private bool hostAppliedVerified;
        private readonly TaskCompletionSource appliedWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void RequireVerifiedHostApplied(MultiDeviceCredentialVault vault, PairingPrepareReceipt receipt, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || !hostAppliedVerified || !ReferenceEquals(stagedVault, vault) || verifiedReady != receipt)
                    throw new InvalidOperationException("This pairing has no verified applied acknowledgement.");
            }
        }

        internal void RequireVerifiedPeerPromotion(PeerInvitationStore store, PairingPrepareReceipt receipt, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (IsHost || !peerCommittedVerified || promotionStarted != 1 ||
                    !ReferenceEquals(preparedPeerStore, store) || preparedPeerReceipt != receipt)
                    throw new InvalidOperationException("This pairing has no verified promotion authority.");
            }
        }

        internal async Task PromoteAndSendAppliedAsync(PeerInvitationStore store, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(store);
            PairingPrepareReceipt receipt;
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (IsHost || !peerCommittedVerified || !ReferenceEquals(preparedPeerStore, store) || preparedPeerReceipt is null)
                    throw new InvalidOperationException("A verified committed pairing is unavailable.");
                if (Interlocked.CompareExchange(ref promotionStarted, 1, 0) != 0)
                    throw new InvalidOperationException("The peer pairing was already promoted.");
                receipt = preparedPeerReceipt; owner.activeOperations++;
            }
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                await store.PromoteCommittedAsync(this, receipt, deadline.Token).ConfigureAwait(false);
                owner.CheckDelivery(this, deadline.Token);
                await owner.WriteDeliveryFrameAsync(new { version = 1, type = "applied", role = owner.role,
                    sessionId = Identity.SessionId, transactionId = Identity.TransactionId, offerNonce = Identity.OfferNonce,
                    stageId = receipt.StageId, senderChallenge = owner.challenge, receiverChallenge = owner.remoteChallenge,
                    slot = receipt.Slot, dataPortBase = receipt.DataPortBase, digest = receipt.InvitationDigest }, deadline.Token).ConfigureAwait(false);
                owner.CheckDelivery(this, deadline.Token);
                appliedWritten.TrySetResult();
            }
            catch (Exception error) { appliedWritten.TrySetException(error); owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }

        internal async Task ReceiveAppliedAsync(CancellationToken ct = default)
        {
            MultiDeviceCredentialVault vault;
            PairingPrepareReceipt receipt;
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || !hostCommitted || committedSendStarted == 0 || verifiedReady is null || stagedVault is null)
                    throw new InvalidOperationException("A committed pairing acknowledgement is unavailable.");
                if (Interlocked.CompareExchange(ref appliedReceiveStarted, 1, 0) != 0)
                    throw new InvalidOperationException("The pairing acknowledgement was already received.");
                receipt = verifiedReady; vault = stagedVault; owner.activeOperations++;
            }
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                // Do not accept an acknowledgement until this session has actually
                // completed its own authenticated COMMITTED write.
                await committedWritten.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                using var frame = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false);
                owner.ValidateDeliveryHeader(frame.RootElement, "applied", "peer", AppliedFields);
                if (ReadDeliveryReceipt(frame.RootElement) != receipt)
                    throw new InvalidDataException("Applied pairing does not match its transaction.");
                await vault.VerifyCommittedDeliveryAsync(this, receipt, deadline.Token).ConfigureAwait(false);
                lock (owner.gate) { owner.CheckDelivery(this, deadline.Token); hostAppliedVerified = true; }
                await vault.MarkDeliveryAppliedAsync(this, receipt, deadline.Token).ConfigureAwait(false);
                owner.CheckDelivery(this, deadline.Token);
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }
    }
}
