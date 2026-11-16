using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class PeerInvitationStore
{
    private static readonly string[] ReservationFields = ["version", "binding", "decision"];
    private sealed record PeerCapacityReservation(int Version, PairingHistoricalBinding Binding, string Decision);
    private string ReservationPath => Path.Combine(directory, "peer-reservation.bin");
    internal Action? ReservationCheckpointForTesting { get; set; }

    private void RejectExistingReservationEvidence()
    {
        RejectLinks(ReservationPath);
        if (File.Exists(ReservationPath) || Directory.Exists(ReservationPath))
            throw new InvalidOperationException("A protected pairing reservation requires recovery.");
    }

    private async Task<PeerCapacityReservation?> ReadReservationAsync(CancellationToken ct)
    {
        RejectLinks(ReservationPath);
        if (Directory.Exists(ReservationPath)) throw new InvalidDataException("Reservation path is invalid.");
        if (!File.Exists(ReservationPath)) return null;
        var encrypted = await ReadReadyEvidenceFileAsync(ReservationPath, ct).ConfigureAwait(false);
        var plain = protector.Unprotect(encrypted);
        try
        {
            if (plain.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Reservation is invalid.");
            using var json = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(plain), new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateFields(json.RootElement); RequireExactFields(json.RootElement, ReservationFields);
            var binding = json.RootElement.GetProperty("binding");
            RequireExactFields(binding, HistoricalBindingFields); RequireExactFields(binding.GetProperty("identity"), IdentityFields);
            var result = json.RootElement.Deserialize<PeerCapacityReservation>(PendingJson)
                ?? throw new InvalidDataException("Reservation is empty.");
            if (result.Version != 1 || result.Decision is not ("reserved" or "refused"))
                throw new InvalidDataException("Reservation decision is invalid.");
            PairingHistoricalRecoveryRules.ValidateBinding(result.Binding);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static void ValidateReservedInvitation(PairingTlsConsentSession.DeliveryLease lease,
        PeerCapacityReservation reservation, PairingSessionIdentity identity, MultiPeerInvitation candidate,
        PairingPrepareReceipt receipt, CancellationToken ct)
    {
        lease.EnsureCurrent(ct);
        var binding = reservation.Binding;
        if (lease.IsHost || reservation.Decision != "reserved" || lease.Identity != identity || binding.Identity != identity ||
            binding.StageId != receipt.StageId || binding.Slot != receipt.Slot || binding.DataPortBase != receipt.DataPortBase ||
            !DigestMatches(binding.InvitationDigest, receipt.InvitationDigest) || candidate.Host != binding.Host ||
            candidate.Port != binding.ControlPort || candidate.KeyboardPort != binding.KeyboardPort || candidate.DataPort != binding.DataPort ||
            candidate.Host != lease.RemoteAddress.ToString() || !DigestMatches(ComputePendingDigest(candidate), binding.InvitationDigest))
            throw new InvalidOperationException("The pending invitation differs from its protected reservation.");
        lease.EnsureCurrent(ct);
    }

    // This writes a durable single-slot decision before an acknowledgement can be sent.
    // It never creates an invitation or changes the existing active connection.
    internal async Task<bool> ReserveCapacityAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease); ArgumentNullException.ThrowIfNull(binding);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); lease.EnsureCurrent(ct);
            PairingHistoricalRecoveryRules.ValidateBinding(binding);
            if (lease.IsHost || lease.Identity != binding.Identity || binding.Host != lease.RemoteAddress.ToString())
                throw new InvalidOperationException("The reservation does not match this confirmed connection.");
            var existing = await ReadReservationAsync(ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Binding != binding) throw new InvalidOperationException("Another pairing reservation requires recovery.");
                return existing.Decision == "reserved";
            }
            var status = await InspectPairingRecoveryLockedAsync(ct).ConfigureAwait(false);
            if (status.Phase != PairingRecoveryPhase.None || Volatile.Read(ref unsafePromotion) != 0 || Volatile.Read(ref unfinishedPromotion) != 0)
                throw new InvalidOperationException("Another pairing requires recovery.");
            await RejectAbortedHistoricalIdentityAsync(binding.Identity, ct).ConfigureAwait(false);
            var hasCapacity = await HasPairingHistoryCapacityAsync(ct).ConfigureAwait(false);
            var evidence = new PeerCapacityReservation(1, binding, hasCapacity ? "reserved" : "refused");
            var plain = JsonSerializer.SerializeToUtf8Bytes(evidence, PendingJson);
            byte[] encrypted;
            try { encrypted = protector.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected reservation is invalid.");
            var temporary = ReservationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await WriteNewFinalFileAsync(temporary, encrypted, ct).ConfigureAwait(false);
                ReservationCheckpointForTesting?.Invoke();
                if (hasCapacity && !await HasPairingHistoryCapacityAsync(ct).ConfigureAwait(false))
                {
                    // External changes can fill history while the protected temporary
                    // file is being written. Publish a refusal, never an invalid slot.
                    hasCapacity = false;
                    var refusedPlain = JsonSerializer.SerializeToUtf8Bytes(evidence with { Decision = "refused" }, PendingJson);
                    try { encrypted = protector.Protect(refusedPlain); }
                    finally { CryptographicOperations.ZeroMemory(refusedPlain); }
                    if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected reservation is invalid.");
                    RejectLinks(temporary); File.Delete(temporary);
                    await WriteNewFinalFileAsync(temporary, encrypted, ct).ConfigureAwait(false);
                }
                RejectExistingReservationEvidence(); lease.EnsureCurrent(ct);
                lease.PublishPeerFinalEvidence(temporary, ReservationPath, ct);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return hasCapacity;
        }
        finally { gate.Release(); }
    }

    private async Task PreserveReservationRefusalLockedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PeerCapacityReservation reservation, CancellationToken ct)
    {
        if (reservation.Decision == "refused") return;
        var original = await ReadReadyEvidenceFileAsync(ReservationPath, ct).ConfigureAwait(false);
        if (await ReadReservationAsync(ct).ConfigureAwait(false) != reservation)
            throw new InvalidOperationException("Reservation changed before refusal.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(reservation with { Decision = "refused" }, PendingJson);
        byte[] encrypted;
        try { encrypted = protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected reservation is invalid.");
        var temporary = ReservationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteNewFinalFileAsync(temporary, encrypted, ct).ConfigureAwait(false);
            var current = await ReadReadyEvidenceFileAsync(ReservationPath, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(original, current))
                throw new InvalidOperationException("Reservation changed before refusal.");
            RejectLinks(ReservationPath); lease.ReplacePeerReservation(temporary, ReservationPath, ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task PublishHistoricalRefusalLockedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalBinding binding, CancellationToken ct)
    {
        RejectExistingReservationEvidence();
        var plain = JsonSerializer.SerializeToUtf8Bytes(new PeerCapacityReservation(1, binding, "refused"), PendingJson);
        byte[] encrypted;
        try { encrypted = protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected reservation is invalid.");
        var temporary = ReservationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteNewFinalFileAsync(temporary, encrypted, ct).ConfigureAwait(false);
            RejectExistingReservationEvidence(); lease.EnsureCurrent(ct);
            lease.PublishPeerFinalEvidence(temporary, ReservationPath, ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed partial class PairingTlsConsentSession
{
    internal sealed partial class DeliveryLease
    {
        internal void ReplacePeerReservation(string temporary, string destination, CancellationToken ct)
        {
            lock (owner.gate) { owner.CheckDelivery(this, ct); File.Move(temporary, destination, true); }
        }
    }
}
