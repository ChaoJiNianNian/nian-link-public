using System.Text.Json;

namespace DesktopToolbox.DeskLink;

internal sealed partial class PairingTlsConsentSession
{
    private static readonly string[] CommittedFields = ["version", "type", "role", "sessionId", "transactionId", "offerNonce", "stageId", "senderChallenge", "receiverChallenge", "slot", "dataPortBase", "digest"];

    internal sealed partial class DeliveryLease
    {
        private int committedSendStarted, committedReceiveStarted;
        private bool peerCommittedVerified;
        private readonly TaskCompletionSource committedWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task SendCommittedAsync(CancellationToken ct = default)
        {
            MultiDeviceCredentialVault vault;
            PairingPrepareReceipt receipt;
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || !hostCommitted || verifiedReady is null || stagedVault is null)
                    throw new InvalidOperationException("A committed pairing is unavailable.");
                if (Interlocked.CompareExchange(ref committedSendStarted, 1, 0) != 0)
                    throw new InvalidOperationException("The committed pairing was already sent.");
                vault = stagedVault; receipt = verifiedReady; owner.activeOperations++;
            }
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                await vault.VerifyCommittedDeliveryAsync(this, receipt, deadline.Token).ConfigureAwait(false);
                owner.CheckDelivery(this, deadline.Token);
                await owner.WriteDeliveryFrameAsync(new { version = 1, type = "committed", role = owner.role,
                    sessionId = Identity.SessionId, transactionId = Identity.TransactionId, offerNonce = Identity.OfferNonce,
                    stageId = receipt.StageId, senderChallenge = owner.challenge, receiverChallenge = owner.remoteChallenge,
                    slot = receipt.Slot, dataPortBase = receipt.DataPortBase, digest = receipt.InvitationDigest }, deadline.Token).ConfigureAwait(false);
                owner.CheckDelivery(this, deadline.Token);
                committedWritten.TrySetResult();
            }
            catch (Exception error) { committedWritten.TrySetException(error); owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }

        internal async Task<PairingPrepareReceipt> ReceiveCommittedAsync(PeerInvitationStore store, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(store);
            PairingPrepareReceipt receipt;
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (IsHost || !ReferenceEquals(preparedPeerStore, store) || preparedPeerReceipt is null)
                    throw new InvalidOperationException("A staged peer invitation is unavailable.");
                if (Interlocked.CompareExchange(ref committedReceiveStarted, 1, 0) != 0)
                    throw new InvalidOperationException("The committed pairing was already received.");
                receipt = preparedPeerReceipt; owner.activeOperations++;
            }
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                using var frame = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false);
                owner.ValidateDeliveryHeader(frame.RootElement, "committed", "host", CommittedFields);
                if (ReadDeliveryReceipt(frame.RootElement) != receipt)
                    throw new InvalidDataException("Committed pairing does not match its READY receipt.");
                await store.VerifyReadyForCommittedAsync(this, receipt, deadline.Token).ConfigureAwait(false);
                lock (owner.gate) { owner.CheckDelivery(this, deadline.Token); peerCommittedVerified = true; }
                return receipt;
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }
    }
}
