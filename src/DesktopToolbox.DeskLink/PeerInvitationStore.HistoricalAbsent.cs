using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class PeerInvitationStore
{
    private static readonly string[] AbortEvidenceFields = ["version", "binding"];
    internal static readonly string[] HistoricalBindingFields = ["identity", "stageId", "slot", "dataPortBase", "host", "controlPort", "keyboardPort", "dataPort", "invitationDigest"];
    private sealed record PeerAbortedEvidence(int Version, PairingHistoricalBinding Binding);
    private string AbortedPath(Guid id) => Path.Combine(directory, "peer-aborted-" + id.ToString("N") + ".bin");
    // Internal fault injection for restart-boundary tests; product callers never set it.
    internal Action<string>? AbortCheckpointForTesting { get; set; }

    private PeerAbortedEvidence ParseAbortedEvidence(byte[] encrypted)
    {
        var plain = protector.Unprotect(encrypted);
        try
        {
            if (plain.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Abort evidence is invalid.");
            using var json = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(plain), new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateFields(json.RootElement); RequireExactFields(json.RootElement, AbortEvidenceFields);
            var bindingJson = json.RootElement.GetProperty("binding");
            RequireExactFields(bindingJson, HistoricalBindingFields); RequireExactFields(bindingJson.GetProperty("identity"), IdentityFields);
            var result = json.RootElement.Deserialize<PeerAbortedEvidence>(PendingJson) ?? throw new InvalidDataException("Abort evidence is empty.");
            if (result.Version != 1) throw new InvalidDataException("Abort evidence version is invalid.");
            PairingHistoricalRecoveryRules.ValidateBinding(result.Binding);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task<PeerAbortedEvidence[]> ReadAbortHistoryAsync(CancellationToken ct)
    {
        var paths = Directory.EnumerateFileSystemEntries(directory, "peer-aborted-*.bin").ToArray();
        if (paths.Length > 64) throw new InvalidDataException("Abort history is invalid.");
        var records = new List<PeerAbortedEvidence>(paths.Length);
        foreach (var path in paths)
        {
            RejectLinks(path);
            var value = ParseAbortedEvidence(await ReadReadyEvidenceFileAsync(path, ct).ConfigureAwait(false));
            if (!string.Equals(path, AbortedPath(value.Binding.Identity.TransactionId), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Abort evidence name is invalid.");
            records.Add(value);
        }
        return records.ToArray();
    }

    private async Task RejectAbortedHistoricalIdentityAsync(PairingSessionIdentity identity, CancellationToken ct)
    {
        var records = await ReadAbortHistoryAsync(ct).ConfigureAwait(false);
        var retired = await ReadRetiredAsync(ct).ConfigureAwait(false);
        if (ConflictsWithRetired(retired, identity))
            throw new InvalidOperationException("This historical pairing has been safely retired.");
        if (records.Any(x => x.Binding.Identity.TransactionId == identity.TransactionId ||
            x.Binding.Identity.SessionId == identity.SessionId || x.Binding.Identity.OfferNonce == identity.OfferNonce))
            throw new InvalidOperationException("This historical pairing has been safely aborted.");
    }

    // Called with the store gate held before staging and before sending READY.
    // Both possible terminal outcomes need a durable history slot.
    private async Task EnsurePairingHistoryCapacityAsync(CancellationToken ct)
    {
        if (!await HasPairingHistoryCapacityAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("配对记录已满，暂时不能开始新的配对；请等待记录清理功能。");
    }

    private async Task<bool> HasPairingHistoryCapacityAsync(CancellationToken ct)
    {
        var completed = Directory.EnumerateFileSystemEntries(directory, "peer-completed-*.bin").ToArray();
        var aborted = await ReadAbortHistoryAsync(ct).ConfigureAwait(false);
        if (aborted.Length == 64)
        {
            await CompactAbortedHistoryLockedAsync(ct).ConfigureAwait(false);
            aborted = await ReadAbortHistoryAsync(ct).ConfigureAwait(false);
        }
        if (completed.Length > 64) throw new InvalidDataException("Completion history is invalid.");
        foreach (var path in completed)
        {
            var evidence = ParseFinalEvidence(await ReadReadyEvidenceFileAsync(path, ct).ConfigureAwait(false));
            if (!string.Equals(path, CompletedPath(evidence.Identity.TransactionId), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Completion history is invalid.");
        }
        var retired = await ReadRetiredAsync(ct).ConfigureAwait(false);
        return completed.Length < 64 && aborted.Length < 64 && retired.Count < MaxRetiredPairings;
    }

    internal async Task SendHistoricalReadyOrAbsentAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalBinding binding, Func<bool, CancellationToken, Task> send, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PairingHistoricalRecoveryRules.ValidateBinding(binding);
            PairingHistoricalRecoveryRules.RequireFreshConfirmedIdentity(lease, binding.Identity, binding.Host);
            var state = await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
            var reservation = await ReadReservationAsync(ct).ConfigureAwait(false);
            if (reservation is not null && reservation.Binding != binding)
                throw new InvalidOperationException("A different pairing reservation requires recovery.");
            if (state.Phase == PairingRecoveryPhase.AwaitingPeer && (reservation is null || File.Exists(ReadyEvidencePath)))
            {
                var before = await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
                if (before.Phase != PairingRecoveryPhase.AwaitingPeer || before.PromotionHash is not null || before.Binding != binding)
                    throw new InvalidOperationException("Historical READY differs from the request.");
                var after = await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
                if (before != after) throw new InvalidOperationException("Historical READY changed.");
                lease.EnsureCurrent(ct);
                await send(true, ct).ConfigureAwait(false);
                return;
            }
            var orphanReservation = reservation is not null && state.Phase == PairingRecoveryPhase.AwaitingPeer &&
                !File.Exists(Path.Combine(directory, PendingInvitationFile));
            var reservedPending = reservation is not null && state.Phase == PairingRecoveryPhase.AwaitingPeer &&
                File.Exists(Path.Combine(directory, PendingInvitationFile)) && !File.Exists(ReadyEvidencePath);
            if (!orphanReservation && !reservedPending && state.Phase is not (PairingRecoveryPhase.None or PairingRecoveryPhase.Unsent) || Volatile.Read(ref unsafePromotion) != 0)
                throw new InvalidOperationException("Historical absence cannot be verified.");
            RejectExistingFinalizationEvidence();
            byte[]? reviewedPending = null;
            if (state.Phase == PairingRecoveryPhase.Unsent || reservedPending)
            {
                var pending = await ReadPendingAsync(ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Historical pending invitation disappeared.");
                var candidate = pending.Invitation;
                if (pending.Identity != binding.Identity || !DigestMatches(pending.InvitationDigest, binding.InvitationDigest) ||
                    candidate.Host != binding.Host || candidate.Port != binding.ControlPort ||
                    candidate.KeyboardPort != binding.KeyboardPort || candidate.DataPort != binding.DataPort)
                    throw new InvalidOperationException("Historical pending invitation differs from the request.");
                reviewedPending = await ReadReadyEvidenceFileAsync(Path.Combine(directory, PendingInvitationFile), ct).ConfigureAwait(false);
            }
            var active = Volatile.Read(ref invitation);
            if (active is not null && DigestMatches(ComputePendingDigest(active), binding.InvitationDigest))
                throw new InvalidOperationException("The historical invitation is currently active.");
            var completions = Directory.EnumerateFileSystemEntries(directory, "peer-completed-*.bin").ToArray();
            if (completions.Length > 64) throw new InvalidDataException("Completion history is invalid.");
            foreach (var path in completions)
            {
                var record = ParseFinalEvidence(await ReadReadyEvidenceFileAsync(path, ct).ConfigureAwait(false));
                if (!string.Equals(path, CompletedPath(record.Identity.TransactionId), StringComparison.OrdinalIgnoreCase) ||
                    record.Identity.TransactionId == binding.Identity.TransactionId || record.Identity.SessionId == binding.Identity.SessionId ||
                    record.Identity.OfferNonce == binding.Identity.OfferNonce)
                    throw new InvalidOperationException("Historical completion conflicts with absence.");
            }
            var retired = await ReadRetiredAsync(ct).ConfigureAwait(false);
            var sameRetired = retired.Contains(RetiredOf(binding, RetiredAborted));
            if (!sameRetired && ConflictsWithRetired(retired, binding.Identity))
                throw new InvalidOperationException("Historical retired identity conflicts.");
            if (sameRetired && reservation is null && state.Phase == PairingRecoveryPhase.None)
            {
                lease.EnsureCurrent(ct); await send(false, ct).ConfigureAwait(false); lease.EnsureCurrent(ct);
                return;
            }
            var aborted = await ReadAbortHistoryAsync(ct).ConfigureAwait(false);
            var existing = aborted.Where(x => x.Binding.Identity.TransactionId == binding.Identity.TransactionId ||
                x.Binding.Identity.SessionId == binding.Identity.SessionId || x.Binding.Identity.OfferNonce == binding.Identity.OfferNonce).ToArray();
            if (existing.Length > 1 || existing.Length == 1 && existing[0].Binding != binding)
                throw new InvalidOperationException("Historical abort evidence conflicts.");
            if (existing.Length == 0)
            {
                if (aborted.Length >= 64)
                {
                    // The protected single slot itself remains a durable refusal when
                    // history is full; no old PREPARE may pass its staging guard.
                    if (reservation is null) await PublishHistoricalRefusalLockedAsync(lease, binding, ct).ConfigureAwait(false);
                    else await PreserveReservationRefusalLockedAsync(lease, reservation, ct).ConfigureAwait(false);
                    AbortCheckpointForTesting?.Invoke("reservation-refused");
                    if (reviewedPending is not null)
                    {
                        var pendingPath = Path.Combine(directory, PendingInvitationFile);
                        var currentPending = await ReadReadyEvidenceFileAsync(pendingPath, ct).ConfigureAwait(false);
                        if (File.Exists(ReadyEvidencePath) || File.Exists(RecoveryPromotionPath) ||
                            !CryptographicOperations.FixedTimeEquals(reviewedPending, currentPending))
                            throw new InvalidOperationException("Historical pending invitation changed before refusal.");
                        ct.ThrowIfCancellationRequested(); RejectLinks(pendingPath); File.Delete(pendingPath);
                    }
                    try { await CompactAbortedHistoryLockedAsync(ct).ConfigureAwait(false); }
                    catch (RetiredHistoryFullException)
                    {
                        // The protected refused single slot still blocks this exact
                        // old identity even if the bounded index has no room.
                    }
                    lease.EnsureCurrent(ct); await send(false, ct).ConfigureAwait(false); lease.EnsureCurrent(ct);
                    return;
                }
                var plain = JsonSerializer.SerializeToUtf8Bytes(new PeerAbortedEvidence(1, binding), PendingJson);
                byte[] encrypted;
                try { encrypted = protector.Protect(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected abort evidence is invalid.");
                var destination = AbortedPath(binding.Identity.TransactionId);
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await WriteNewFinalFileAsync(temporary, encrypted, ct).ConfigureAwait(false);
                    // Same gate prevents pending staging until this protected tombstone exists.
                    lease.PublishPeerFinalEvidence(temporary, destination, ct);
                    AbortCheckpointForTesting?.Invoke("tombstone");
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (reservation is not null)
            {
                if (await ReadReservationAsync(ct).ConfigureAwait(false) != reservation)
                    throw new InvalidOperationException("Reservation changed before cleanup.");
                ct.ThrowIfCancellationRequested(); RejectLinks(ReservationPath); File.Delete(ReservationPath);
            }
            if (state.Phase == PairingRecoveryPhase.Unsent || reservedPending)
            {
                var current = await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
                var currentBytes = await ReadReadyEvidenceFileAsync(Path.Combine(directory, PendingInvitationFile), ct).ConfigureAwait(false);
                if (current.Phase != PairingRecoveryPhase.Unsent || reviewedPending is null ||
                    !CryptographicOperations.FixedTimeEquals(reviewedPending, currentBytes))
                    throw new InvalidOperationException("Historical pending invitation changed before abort.");
                RejectExistingReadyEvidence();
                var pendingPath = Path.Combine(directory, PendingInvitationFile);
                ct.ThrowIfCancellationRequested(); RejectLinks(pendingPath); File.Delete(pendingPath);
            }
            lease.EnsureCurrent(ct);
            await send(false, ct).ConfigureAwait(false);
            lease.EnsureCurrent(ct);
        }
        finally { gate.Release(); }
    }
}
