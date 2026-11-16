using System.Text.Json;

namespace DesktopToolbox.DeskLink;

internal sealed partial class PairingTlsConsentSession
{
    private static readonly string[] HistoricalReadyQueryFields = ["version", "type", "role", "sessionId", "transactionId", "offerNonce", "senderChallenge", "receiverChallenge", "commitment", "binding"];
    private static readonly string[] HistoricalIdentityFields = ["sessionId", "offerNonce", "transactionId", "hostName", "peerName", "hostFingerprint", "peerFingerprint", "expiresAtUtc"];
    internal sealed partial class DeliveryLease
    {
        private PeerInvitationStore? historicalPeerStore;
        private PairingHistoricalSnapshot? verifiedHistoricalPeerCommit, verifiedHistoricalPeerFinal;
        private MultiDeviceCredentialVault? historicalReadyVault;
        private PairingHistoricalSnapshot? verifiedHistoricalReady;
        private MultiDeviceCredentialVault? historicalAbsentVault;
        private PairingHistoricalSnapshot? verifiedHistoricalAbsent;

        internal void RequireVerifiedHistoricalAbsent(MultiDeviceCredentialVault vault, PairingHistoricalSnapshot snapshot, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || !ReferenceEquals(historicalAbsentVault, vault) || verifiedHistoricalAbsent != snapshot)
                    throw new InvalidOperationException("Historical absence proof is unavailable.");
            }
        }

        internal void RequireVerifiedHistoricalReady(MultiDeviceCredentialVault vault, PairingHistoricalSnapshot snapshot, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || !ReferenceEquals(historicalReadyVault, vault) || verifiedHistoricalReady != snapshot)
                    throw new InvalidOperationException("Historical READY acknowledgement is unavailable.");
            }
        }

        internal void RequireVerifiedHistoricalPeerCommit(PeerInvitationStore store, PairingHistoricalSnapshot snapshot, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (IsHost || !ReferenceEquals(historicalPeerStore, store) || verifiedHistoricalPeerCommit != snapshot)
                    throw new InvalidOperationException("Historical host commit is unavailable.");
            }
        }

        internal void RequireVerifiedHistoricalPeerFinal(PeerInvitationStore store, PairingHistoricalSnapshot snapshot, CancellationToken ct)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (IsHost || !ReferenceEquals(historicalPeerStore, store) || verifiedHistoricalPeerFinal != snapshot)
                    throw new InvalidOperationException("Historical final authorization is unavailable.");
            }
        }

        internal async Task<bool> ResumeHistoricalCommittedAsync(MultiDeviceCredentialVault vault, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(vault);
            ClaimRecoveryOperation(host: true, ct);
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                var before = await vault.CaptureHistoricalRecoveryAsync(this, deadline.Token).ConfigureAwait(false);
                if (before.Phase == PairingRecoveryPhase.CommittedAwaitingPeer &&
                    await vault.IsHistoricalFinishedAsync(this, before, deadline.Token).ConfigureAwait(false))
                {
                    await SendHistoricalHostFinishedAndAwaitRetiredAsync(vault, before, deadline.Token).ConfigureAwait(false);
                    return true;
                }
                if (before.Phase == PairingRecoveryPhase.AwaitingPeer)
                {
                    var readyCommitment = before.Binding.Commitment();
                    await owner.WriteHistoricalBindingFrameAsync("recovery-ready-query", before.Binding, readyCommitment, deadline.Token).ConfigureAwait(false);
                    using (var ready = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false))
                    {
                        var absent = ready.RootElement.GetProperty("type").GetString() == "recovery-absent";
                        owner.ValidateDeliveryHeader(ready.RootElement, absent ? "recovery-absent" : "recovery-ready", "peer", RecoveryQueryFields);
                        if (!SameDeliveryDigest(ready.RootElement.GetProperty("commitment").GetString() ?? "", readyCommitment))
                            throw new InvalidDataException("Historical READY differs from the transaction.");
                        if (absent)
                        {
                            lock (owner.gate)
                            {
                                owner.CheckDelivery(this, deadline.Token);
                                historicalAbsentVault = vault; verifiedHistoricalAbsent = before;
                            }
                            await vault.AbortHistoricalAbsentAsync(this, before, deadline.Token).ConfigureAwait(false);
                            owner.CheckDelivery(this, deadline.Token);
                            return false;
                        }
                    }
                    lock (owner.gate)
                    {
                        owner.CheckDelivery(this, deadline.Token);
                        historicalReadyVault = vault; verifiedHistoricalReady = before;
                    }
                    await vault.CommitHistoricalReadyAsync(this, before, deadline.Token).ConfigureAwait(false);
                    var committed = await vault.CaptureHistoricalRecoveryAsync(this, deadline.Token).ConfigureAwait(false);
                    if (committed.Phase != PairingRecoveryPhase.CommittedAwaitingPeer || committed.Binding != before.Binding ||
                        committed.PendingHash != before.PendingHash || committed.MarkerHash != before.MarkerHash)
                        throw new InvalidOperationException("Historical READY changed during commit.");
                    before = committed;
                }
                if (before.Phase != PairingRecoveryPhase.CommittedAwaitingPeer)
                    throw new InvalidOperationException("Historical commit is unavailable.");
                var commitment = before.Binding.Commitment();
                await vault.SendHistoricalCommittedFrameAsync(this, before, false,
                    token => owner.WriteRecoveryFrameAsync("recovery-committed", commitment, token), deadline.Token).ConfigureAwait(false);
                using (var reply = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false))
                {
                    owner.ValidateDeliveryHeader(reply.RootElement, "recovery-promoted", "peer", RecoveryQueryFields);
                    if (!SameDeliveryDigest(reply.RootElement.GetProperty("commitment").GetString() ?? "", commitment))
                        throw new InvalidDataException("Historical promotion differs from the transaction.");
                }
                lock (owner.gate)
                {
                    owner.CheckDelivery(this, deadline.Token);
                    historicalAppliedVault = vault; verifiedHistoricalApplied = before;
                }
                await vault.MarkHistoricalDeliveryAppliedAsync(this, before, deadline.Token).ConfigureAwait(false);
                var applied = await vault.CaptureHistoricalRecoveryAsync(this, deadline.Token).ConfigureAwait(false);
                if (applied.Binding != before.Binding || applied.PendingHash != before.PendingHash || applied.MarkerHash != before.MarkerHash ||
                    applied.Phase != PairingRecoveryPhase.CommittedAwaitingPeer)
                    throw new InvalidOperationException("Historical commit changed while applying.");
                await vault.SendHistoricalCommittedFrameAsync(this, applied, true,
                    token => owner.WriteRecoveryFrameAsync("recovery-final", commitment, token), deadline.Token).ConfigureAwait(false);
                using (var reply = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false))
                {
                    owner.ValidateDeliveryHeader(reply.RootElement, "recovery-final-ack", "peer", RecoveryQueryFields);
                    if (!SameDeliveryDigest(reply.RootElement.GetProperty("commitment").GetString() ?? "", commitment))
                        throw new InvalidDataException("Historical final acknowledgement differs from the transaction.");
                }
                lock (owner.gate)
                {
                    owner.CheckDelivery(this, deadline.Token);
                    recoveredVault = vault; verifiedHistoricalFinal = applied;
                }
                await vault.MarkHistoricalDeliveryFinishedAsync(this, applied, deadline.Token).ConfigureAwait(false);
                var finished = await vault.CaptureHistoricalRecoveryAsync(this, deadline.Token).ConfigureAwait(false);
                if (finished.Binding != applied.Binding || finished.PendingHash != applied.PendingHash || finished.MarkerHash != applied.MarkerHash)
                    throw new InvalidOperationException("Historical completion changed before retirement.");
                await SendHistoricalHostFinishedAndAwaitRetiredAsync(vault, finished, deadline.Token).ConfigureAwait(false);
                owner.CheckDelivery(this, deadline.Token);
                return true;
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }

        internal async Task<bool> AnswerHistoricalCommittedAsync(PeerInvitationStore store, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(store);
            ClaimRecoveryOperation(host: false, ct);
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                using var initial = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false);
                var initialType = initial.RootElement.GetProperty("type").GetString();
                if (initialType == "recovery-host-finished")
                {
                    await ReceiveHistoricalHostFinishedAndRetireAsync(store, initial.RootElement, deadline.Token).ConfigureAwait(false);
                    return true;
                }
                var isReadyQuery = initialType == "recovery-ready-query";
                owner.ValidateDeliveryHeader(initial.RootElement, isReadyQuery ? "recovery-ready-query" : "recovery-committed", "host", isReadyQuery ? HistoricalReadyQueryFields : RecoveryQueryFields);
                var commitment = initial.RootElement.GetProperty("commitment").GetString() ?? "";
                if (isReadyQuery)
                {
                    var bindingJson = initial.RootElement.GetProperty("binding");
                    if (!ExactFields(bindingJson, PeerInvitationStore.HistoricalBindingFields) ||
                        !ExactFields(bindingJson.GetProperty("identity"), HistoricalIdentityFields))
                        throw new InvalidDataException("Historical request fields are invalid.");
                    var binding = bindingJson.Deserialize<PairingHistoricalBinding>(JsonLineProtocol.Json)
                        ?? throw new InvalidDataException("Historical request is empty.");
                    PairingHistoricalRecoveryRules.ValidateBinding(binding);
                    if (!SameDeliveryDigest(binding.Commitment(), commitment))
                        throw new InvalidDataException("Historical request commitment differs.");
                    var peerHasReady = true;
                    await store.SendHistoricalReadyOrAbsentAsync(this, binding,
                        (ready, token) => { peerHasReady = ready; return owner.WriteRecoveryFrameAsync(ready ? "recovery-ready" : "recovery-absent", commitment, token); }, deadline.Token).ConfigureAwait(false);
                    if (!peerHasReady) return false;
                    using var committed = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false);
                    owner.ValidateDeliveryHeader(committed.RootElement, "recovery-committed", "host", RecoveryQueryFields);
                    if (!SameDeliveryDigest(committed.RootElement.GetProperty("commitment").GetString() ?? "", commitment))
                        throw new InvalidDataException("Historical commit differs from READY.");
                }
                // A durable completion record is selected only when no pending
                // transaction exists. Evidence errors never fall back to it.
                var localState = await store.GetPairingRecoveryStatusAsync(deadline.Token).ConfigureAwait(false);
                if (localState.Phase is PairingRecoveryPhase.None or PairingRecoveryPhase.CompletedAwaitingHost)
                {
                    await store.SendHistoricalCompletionAsync(this, commitment,
                        token => owner.WriteRecoveryFrameAsync("recovery-promoted", commitment, token), deadline.Token).ConfigureAwait(false);
                    using var completedFinal = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false);
                    owner.ValidateDeliveryHeader(completedFinal.RootElement, "recovery-final", "host", RecoveryQueryFields);
                    if (!SameDeliveryDigest(completedFinal.RootElement.GetProperty("commitment").GetString() ?? "", commitment))
                        throw new InvalidDataException("Historical final differs from completed evidence.");
                    await store.SendHistoricalCompletionAsync(this, commitment,
                        token => owner.WriteRecoveryFrameAsync("recovery-final-ack", commitment, token), deadline.Token).ConfigureAwait(false);
                    await ReceiveHistoricalHostFinishedAndRetireAsync(store, deadline.Token).ConfigureAwait(false);
                    owner.CheckDelivery(this, deadline.Token);
                    return true;
                }
                var before = await store.CaptureHistoricalRecoveryAsync(this, deadline.Token).ConfigureAwait(false);
                if (!SameDeliveryDigest(before.Binding.Commitment(), commitment))
                    throw new InvalidDataException("Historical host commit differs from peer evidence.");
                lock (owner.gate)
                {
                    owner.CheckDelivery(this, deadline.Token);
                    historicalPeerStore = store; verifiedHistoricalPeerCommit = before;
                }
                var promoted = await store.PromoteHistoricalCommittedAsync(this, before, deadline.Token).ConfigureAwait(false);
                await store.SendHistoricalPromotionAsync(this, promoted,
                    token => owner.WriteRecoveryFrameAsync("recovery-promoted", commitment, token), deadline.Token).ConfigureAwait(false);
                using var final = await owner.ReadDeliveryFrameAsync(deadline.Token).ConfigureAwait(false);
                owner.ValidateDeliveryHeader(final.RootElement, "recovery-final", "host", RecoveryQueryFields);
                if (!SameDeliveryDigest(final.RootElement.GetProperty("commitment").GetString() ?? "", commitment))
                    throw new InvalidDataException("Historical final differs from peer evidence.");
                lock (owner.gate) { owner.CheckDelivery(this, deadline.Token); verifiedHistoricalPeerFinal = promoted; }
                await store.FinalizeHistoricalCommittedAsync(this, promoted, deadline.Token).ConfigureAwait(false);
                await store.SendHistoricalCompletionAsync(this, commitment,
                    token => owner.WriteRecoveryFrameAsync("recovery-final-ack", commitment, token), deadline.Token).ConfigureAwait(false);
                await ReceiveHistoricalHostFinishedAndRetireAsync(store, deadline.Token).ConfigureAwait(false);
                owner.CheckDelivery(this, deadline.Token);
                return true;
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }
    }

    private Task WriteHistoricalBindingFrameAsync(string type, PairingHistoricalBinding binding, string commitment, CancellationToken ct) =>
        WriteDeliveryFrameAsync(new { version = 1, type, role, sessionId = identity.SessionId,
            transactionId = identity.TransactionId, offerNonce = identity.OfferNonce,
            senderChallenge = challenge, receiverChallenge = remoteChallenge, commitment, binding }, ct);
}
