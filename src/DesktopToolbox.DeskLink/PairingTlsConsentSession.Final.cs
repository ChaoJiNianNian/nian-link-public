namespace DesktopToolbox.DeskLink;

internal sealed partial class PairingTlsConsentSession
{
    private static readonly string[] FinalFields = ["version", "type", "role", "sessionId", "transactionId", "offerNonce", "stageId", "senderChallenge", "receiverChallenge", "slot", "dataPortBase", "digest"];

    internal sealed partial class DeliveryLease
    {
        private int finalSendStarted, finalReceiveStarted;
        private bool hostFinalAckVerified, peerFinalVerified;

        internal void RequireVerifiedHostFinal(MultiDeviceCredentialVault vault, PairingPrepareReceipt receipt, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || !hostFinalAckVerified || !ReferenceEquals(stagedVault, vault) || verifiedReady != receipt)
                    throw new InvalidOperationException("This pairing has no verified final acknowledgement.");
            }
        }

        internal void RequireVerifiedPeerFinal(PeerInvitationStore store, PairingPrepareReceipt receipt, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (IsHost || !peerFinalVerified || !ReferenceEquals(preparedPeerStore, store) || preparedPeerReceipt != receipt)
                    throw new InvalidOperationException("This pairing has no verified final message.");
            }
        }

        internal async Task SendFinalAndAwaitAckAsync(CancellationToken ct = default)
        {
            MultiDeviceCredentialVault vault; PairingPrepareReceipt receipt;
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || !hostAppliedVerified || stagedVault is null || verifiedReady is null ||
                    Interlocked.CompareExchange(ref finalSendStarted, 1, 0) != 0)
                    throw new InvalidOperationException("A verified applied pairing is unavailable.");
                vault = stagedVault; receipt = verifiedReady; owner.activeOperations++;
            }
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                await vault.VerifyAppliedForFinalAsync(this, receipt, deadline.Token).ConfigureAwait(false);
                await owner.WriteFinalFrameAsync("final", receipt, deadline.Token).ConfigureAwait(false);
                using var frame = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false);
                owner.ValidateDeliveryHeader(frame.RootElement, "final-ack", "peer", FinalFields);
                if (ReadDeliveryReceipt(frame.RootElement) != receipt)
                    throw new InvalidDataException("Final acknowledgement differs from the pairing.");
                lock (owner.gate) { owner.CheckDelivery(this, deadline.Token); hostFinalAckVerified = true; }
                await vault.MarkDeliveryFinishedAsync(this, receipt, deadline.Token).ConfigureAwait(false);
                await SendHostFinishedAndAwaitRetiredAsync(vault, receipt, deadline.Token).ConfigureAwait(false);
                owner.CheckDelivery(this, deadline.Token);
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }

        internal async Task ReceiveFinalAndFinalizeAsync(PeerInvitationStore store, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(store);
            PairingPrepareReceipt receipt;
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (IsHost || !peerCommittedVerified || !ReferenceEquals(preparedPeerStore, store) || preparedPeerReceipt is null ||
                    Interlocked.CompareExchange(ref finalReceiveStarted, 1, 0) != 0)
                    throw new InvalidOperationException("The promoted pairing is unavailable.");
                receipt = preparedPeerReceipt; owner.activeOperations++;
            }
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                await appliedWritten.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                using var frame = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false);
                owner.ValidateDeliveryHeader(frame.RootElement, "final", "host", FinalFields);
                if (ReadDeliveryReceipt(frame.RootElement) != receipt)
                    throw new InvalidDataException("Final pairing differs from the prepared invitation.");
                lock (owner.gate) { owner.CheckDelivery(this, deadline.Token); peerFinalVerified = true; }
                await store.FinalizeCommittedAsync(this, receipt, deadline.Token).ConfigureAwait(false);
                owner.CheckDelivery(this, deadline.Token);
                await owner.WriteFinalFrameAsync("final-ack", receipt, deadline.Token).ConfigureAwait(false);
                await ReceiveHostFinishedAndRetireAsync(store, receipt, deadline.Token).ConfigureAwait(false);
                owner.CheckDelivery(this, deadline.Token);
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }

        internal void PublishPeerFinalEvidence(string temporary, string destination, CancellationToken ct)
        {
            lock (owner.gate) { owner.CheckDelivery(this, ct); File.Move(temporary, destination, false); }
        }
    }

    private Task WriteFinalFrameAsync(string type, PairingPrepareReceipt receipt, CancellationToken ct) =>
        WriteDeliveryFrameAsync(new { version = 1, type, role, sessionId = identity.SessionId,
            transactionId = identity.TransactionId, offerNonce = identity.OfferNonce, stageId = receipt.StageId,
            senderChallenge = challenge, receiverChallenge = remoteChallenge, slot = receipt.Slot,
            dataPortBase = receipt.DataPortBase, digest = receipt.InvitationDigest }, ct);
}
