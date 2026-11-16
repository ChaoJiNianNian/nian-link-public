namespace DesktopToolbox.DeskLink;

public sealed partial class PeerInvitationStore
{
    internal async Task RetireCompletedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalBinding binding, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lease.RequireVerifiedPeerHostFinished(this, binding, ct);
            var path = CompletedPath(binding.Identity.TransactionId);
            RejectLinks(path);
            if (!File.Exists(path))
            {
                var retired = await ReadRetiredAsync(ct).ConfigureAwait(false);
                if (!retired.Contains(RetiredOf(binding, RetiredCompleted)))
                    throw new InvalidOperationException("Completed evidence is unavailable or differs from this transaction.");
                lease.RequireVerifiedPeerHostFinished(this, binding, ct);
                return;
            }
            var activeBytes = await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false);
            var active = ParseProtectedInvitation(activeBytes);
            if (active != Volatile.Read(ref invitation)) throw new InvalidOperationException("The active invitation changed.");
            ValidatePending(binding.Identity, active, binding.InvitationDigest);
            if (active.Host != binding.Host || active.Port != binding.ControlPort || active.KeyboardPort != binding.KeyboardPort || active.DataPort != binding.DataPort)
                throw new InvalidOperationException("The completed invitation changed.");
            var cipher = await ReadReadyEvidenceFileAsync(path, ct).ConfigureAwait(false);
            var record = ParseFinalEvidence(cipher);
            var actual = new PairingHistoricalBinding(record.Identity, record.StageId, record.Slot, record.DataPortBase,
                record.Host, record.ControlPort, record.KeyboardPort, record.DataPort, record.InvitationDigest);
            if (actual != binding) throw new InvalidOperationException("Completed evidence belongs to another transaction.");
            await CompactAbortedHistoryLockedAsync(ct, (path, cipher, RetiredOf(binding, RetiredCompleted)),
                () => lease.RequireVerifiedPeerHostFinished(this, binding, ct)).ConfigureAwait(false);
            lease.RequireVerifiedPeerHostFinished(this, binding, ct);
        }
        finally { gate.Release(); }
    }
}
