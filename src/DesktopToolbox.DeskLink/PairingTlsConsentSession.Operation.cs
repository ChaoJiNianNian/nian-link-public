namespace DesktopToolbox.DeskLink;

internal sealed partial class PairingTlsConsentSession
{
    private static readonly string[] OperationFields = ["version", "type", "role", "sessionId", "transactionId", "offerNonce", "senderChallenge", "receiverChallenge", "operation"];

    internal sealed partial class DeliveryLease
    {
        private int operationStarted;

        internal async Task SendPairingOperationAsync(bool recovery, CancellationToken ct = default)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || stageStarted != 0 || prepareStarted != 0 || Interlocked.CompareExchange(ref operationStarted, 1, 0) != 0)
                    throw new InvalidOperationException("Pairing operation selection is unavailable.");
                owner.activeOperations++;
            }
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                await owner.WriteDeliveryFrameAsync(new { version = 1, type = "operation", role = owner.role,
                    sessionId = Identity.SessionId, transactionId = Identity.TransactionId, offerNonce = Identity.OfferNonce,
                    senderChallenge = owner.challenge, receiverChallenge = owner.remoteChallenge,
                    operation = recovery ? "recover" : "new" }, deadline.Token).ConfigureAwait(false);
                owner.CheckDelivery(this, deadline.Token);
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }

        internal async Task<bool> ReceivePairingOperationAsync(CancellationToken ct = default)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (IsHost || stageStarted != 0 || prepareStarted != 0 || Interlocked.CompareExchange(ref operationStarted, 1, 0) != 0)
                    throw new InvalidOperationException("Pairing operation selection is unavailable.");
                owner.activeOperations++;
            }
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                using var frame = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false);
                owner.ValidateDeliveryHeader(frame.RootElement, "operation", "host", OperationFields);
                var selected = frame.RootElement.GetProperty("operation").GetString();
                if (selected is not ("new" or "recover")) throw new InvalidDataException("Unknown pairing operation.");
                owner.CheckDelivery(this, deadline.Token);
                return selected == "recover";
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }
    }
}
