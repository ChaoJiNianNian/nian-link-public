namespace DesktopToolbox.DeskLink;

public sealed partial class PeerInvitationStore
{
    // An old completion is usable only while the actual active invitation still
    // agrees with it and the new TLS connection authenticates the same devices.
    internal async Task SendHistoricalCompletionAsync(PairingTlsConsentSession.DeliveryLease lease,
        string expectedCommitment, Func<CancellationToken, Task> send, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var before = await CaptureHistoricalCompletionLockedAsync(lease, expectedCommitment, ct).ConfigureAwait(false);
            var after = await CaptureHistoricalCompletionLockedAsync(lease, expectedCommitment, ct).ConfigureAwait(false);
            if (before != after) throw new InvalidOperationException("Historical completion evidence changed.");
            lease.EnsureCurrent(ct);
            await send(ct).ConfigureAwait(false);
            lease.EnsureCurrent(ct);
        }
        finally { gate.Release(); }
    }

    private async Task<PairingHistoricalSnapshot> CaptureHistoricalCompletionLockedAsync(
        PairingTlsConsentSession.DeliveryLease lease, string expectedCommitment, CancellationToken ct)
    {
            ObjectDisposedException.ThrowIf(disposed, this); lease.EnsureCurrent(ct);
            if (lease.IsHost || Volatile.Read(ref unsafePromotion) != 0)
                throw new InvalidOperationException("Historical completion is unavailable.");
            RejectExistingFinalizationEvidence(); RejectLinks(file);
            var activeBytes = await ReadReadyEvidenceFileAsync(file, ct).ConfigureAwait(false);
            var active = ParseProtectedInvitation(activeBytes);
            if (active != Volatile.Read(ref invitation)) throw new InvalidOperationException("The active invitation changed.");
            var paths = Directory.EnumerateFileSystemEntries(directory, "peer-completed-*.bin").ToArray();
            if (paths.Length > 64) throw new InvalidDataException("Pairing completion history is invalid.");
            PairingHistoricalSnapshot? match = null;
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested(); RejectLinks(path);
                var bytes = await ReadReadyEvidenceFileAsync(path, ct).ConfigureAwait(false);
                var evidence = ParseFinalEvidence(bytes);
                if (!string.Equals(path, CompletedPath(evidence.Identity.TransactionId), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Completion record name is invalid.");
                var binding = new PairingHistoricalBinding(evidence.Identity, evidence.StageId, evidence.Slot,
                    evidence.DataPortBase, evidence.Host, evidence.ControlPort, evidence.KeyboardPort, evidence.DataPort, evidence.InvitationDigest);
                if (!SameCompletionDigest(binding.Commitment(), expectedCommitment)) continue;
                if (match is not null) throw new InvalidDataException("Completion history is ambiguous.");
                PairingHistoricalRecoveryRules.RequireFreshConfirmedIdentity(lease, evidence.Identity, evidence.Host);
                ValidatePending(evidence.Identity, active, evidence.InvitationDigest);
                if (active.Host != evidence.Host || active.Port != evidence.ControlPort || active.KeyboardPort != evidence.KeyboardPort ||
                    active.DataPort != evidence.DataPort)
                    throw new InvalidOperationException("The current invitation differs from completed pairing.");
                var hash = HashFinal(bytes);
                match = new(this, PairingRecoveryPhase.CommittedAwaitingPeer, binding, hash, hash, HashFinal(activeBytes), hash);
            }
            lease.EnsureCurrent(ct);
            return match ?? throw new InvalidOperationException("No matching completed transaction exists.");
    }

    private static bool SameCompletionDigest(string first, string second) => DigestMatches(first, second);
}
