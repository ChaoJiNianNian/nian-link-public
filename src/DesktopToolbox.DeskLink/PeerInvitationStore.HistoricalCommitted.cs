namespace DesktopToolbox.DeskLink;

public sealed partial class PeerInvitationStore
{
    internal async Task<PairingHistoricalSnapshot> PromoteHistoricalCommittedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalSnapshot reviewed, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lease.RequireVerifiedHistoricalPeerCommit(this, reviewed, ct);
            var current = await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
            if (reviewed != current) throw new InvalidOperationException("Historical peer evidence changed.");
            if (current.PromotionHash is not null) return current;
            var pending = await ReadPendingAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Historical peer invitation is unavailable.");
            var readyBytes = await ReadReadyEvidenceFileAsync(ReadyEvidencePath, ct).ConfigureAwait(false);
            var ready = ParsePromotionReady(readyBytes);
            var binding = reviewed.Binding;
            var receipt = new PairingPrepareReceipt(binding.StageId, binding.Slot, binding.DataPortBase, binding.InvitationDigest);
            if (pending.Identity != binding.Identity || ready.Identity != binding.Identity || ready.StageId != receipt.StageId ||
                ready.Slot != receipt.Slot || ready.DataPortBase != receipt.DataPortBase ||
                !DigestMatches(ready.InvitationDigest, receipt.InvitationDigest))
                throw new InvalidOperationException("Historical peer receipt changed.");
            await PublishPromotionLockedAsync(lease, binding.Identity, receipt, pending, ready, readyBytes,
                () => lease.RequireVerifiedHistoricalPeerCommit(this, reviewed, ct), ct).ConfigureAwait(false);
            var promoted = await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
            if (promoted.Binding != reviewed.Binding || promoted.PendingHash != reviewed.PendingHash ||
                promoted.MarkerHash != reviewed.MarkerHash || promoted.ActiveHash != reviewed.ActiveHash ||
                promoted.ReservationHash != reviewed.ReservationHash || promoted.PromotionHash is null)
                throw new InvalidOperationException("Historical promotion did not preserve its evidence.");
            return promoted;
        }
        finally { gate.Release(); }
    }

    internal async Task SendHistoricalPromotionAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalSnapshot reviewed, Func<CancellationToken, Task> send, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
            if (current != reviewed || current.PromotionHash is null)
                throw new InvalidOperationException("Historical promotion changed.");
            lease.EnsureCurrent(ct);
            await send(ct).ConfigureAwait(false);
            lease.EnsureCurrent(ct);
        }
        finally { gate.Release(); }
    }

    internal async Task FinalizeHistoricalCommittedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalSnapshot reviewed, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lease.RequireVerifiedHistoricalPeerFinal(this, reviewed, ct);
            var current = await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
            if (current != reviewed || current.PromotionHash is null)
                throw new InvalidOperationException("Historical promotion changed before finalization.");
            var binding = reviewed.Binding;
            var receipt = new PairingPrepareReceipt(binding.StageId, binding.Slot, binding.DataPortBase, binding.InvitationDigest);
            await FinalizeCommittedLockedAsync(lease, binding.Identity, receipt,
                () => lease.RequireVerifiedHistoricalPeerFinal(this, reviewed, ct), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
