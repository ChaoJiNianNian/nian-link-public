namespace DesktopToolbox.DeskLink;

internal sealed partial class PairingTlsConsentSession
{
    private static readonly string[] RecoveryQueryFields = ["version", "type", "role", "sessionId", "transactionId", "offerNonce", "senderChallenge", "receiverChallenge", "commitment"];

    internal sealed partial class DeliveryLease
    {
        internal async Task<PairingHistoricalRecoveryResult> CompareHistoricalRecoveryAsync(MultiDeviceCredentialVault vault,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(vault);
            ClaimRecoveryOperation(host: true, ct);
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                var before = await vault.CaptureHistoricalRecoveryAsync(this, deadline.Token).ConfigureAwait(false);
                var commitment = before.Binding.Commitment();
                await owner.WriteRecoveryFrameAsync("recovery-query", commitment, deadline.Token).ConfigureAwait(false);
                using var reply = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false);
                owner.ValidateDeliveryHeader(reply.RootElement, "recovery-match", "peer", RecoveryQueryFields);
                if (!SameDeliveryDigest(reply.RootElement.GetProperty("commitment").GetString() ?? "", commitment))
                    throw new InvalidDataException("Historical pairing evidence did not match.");
                var after = await vault.CaptureHistoricalRecoveryAsync(this, deadline.Token).ConfigureAwait(false);
                if (before != after) throw new InvalidOperationException("Historical host pairing evidence changed.");
                EnsureCurrent(deadline.Token);
                return new(true, before.Phase);
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }

        internal async Task<PairingHistoricalRecoveryResult> AnswerHistoricalRecoveryAsync(PeerInvitationStore store,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(store);
            ClaimRecoveryOperation(host: false, ct);
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                var before = await store.CaptureHistoricalRecoveryAsync(this, deadline.Token).ConfigureAwait(false);
                using var query = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false);
                owner.ValidateDeliveryHeader(query.RootElement, "recovery-query", "host", RecoveryQueryFields);
                var commitment = before.Binding.Commitment();
                if (!SameDeliveryDigest(query.RootElement.GetProperty("commitment").GetString() ?? "", commitment))
                    throw new InvalidDataException("Historical pairing evidence did not match.");
                var after = await store.CaptureHistoricalRecoveryAsync(this, deadline.Token).ConfigureAwait(false);
                if (before != after) throw new InvalidOperationException("Historical peer pairing evidence changed.");
                EnsureCurrent(deadline.Token);
                await owner.WriteRecoveryFrameAsync("recovery-match", commitment, deadline.Token).ConfigureAwait(false);
                EnsureCurrent(deadline.Token);
                return new(true, before.Phase);
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }

        private void ClaimRecoveryOperation(bool host, CancellationToken ct)
        {
            EnsureCurrent(ct);
            if (IsHost != host || Interlocked.CompareExchange(ref prepareStarted, 1, 0) != 0)
                throw new InvalidOperationException("Historical recovery is unavailable for this connection.");
            if (host && Interlocked.CompareExchange(ref stageStarted, 1, 0) != 0)
                throw new InvalidOperationException("This connection already attempted credential delivery.");
            owner.BeginDeliveryOperation(this, ct);
        }
    }

    private Task WriteRecoveryFrameAsync(string type, string commitment, CancellationToken ct) =>
        WriteDeliveryFrameAsync(new { version = 1, type, role, sessionId = identity.SessionId,
            transactionId = identity.TransactionId, offerNonce = identity.OfferNonce,
            senderChallenge = challenge, receiverChallenge = remoteChallenge, commitment }, ct);
}
