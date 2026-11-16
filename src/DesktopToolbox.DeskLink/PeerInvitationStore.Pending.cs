using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopToolbox.DeskLink;

/// <summary>Non-secret description of a protected, not-yet-active pairing candidate.</summary>
internal sealed record PendingPeerInvitationMetadata(Guid TransactionId, Guid SessionId, Guid OfferNonce,
    string HostName, string PeerName, string HostFingerprint, string PeerFingerprint,
    DateTimeOffset ExpiresAtUtc, string InvitationDigest);

public sealed partial class PeerInvitationStore
{
    private const string PendingInvitationFile = "peer-pending-invitation.bin";
    private static readonly byte[] PendingDigestDomain = Encoding.UTF8.GetBytes("nian-link-pending-invitation-v1\0");
    private static readonly UTF8Encoding StrictPendingUtf8 = new(false, true);
    private static readonly string[] PendingFields = ["version", "identity", "invitation", "invitationDigest", "baseInvitationDigest"];
    private static readonly string[] IdentityFields = ["sessionId", "offerNonce", "transactionId", "hostName", "peerName", "hostFingerprint", "peerFingerprint", "expiresAtUtc"];
    private static readonly string[] InvitationFields = ["version", "host", "port", "keyboardPort", "dataPort", "hostName", "peerName", "certificateSha256", "token"];
    private static readonly JsonSerializerOptions PendingJson = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private sealed record PendingDocument(int Version, PairingSessionIdentity Identity,
        MultiPeerInvitation Invitation, string InvitationDigest, string? BaseInvitationDigest);

    // Hash the deterministic local invitation JSON under a versioned domain.
    // This identifies the data; it does not authenticate a sender or grant access.
    internal static string ComputePendingDigest(MultiPeerInvitation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value.ToJson());
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(PendingDigestDomain);
            hash.AppendData(bytes);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>
    /// Stages a single invitation without changing the active invitation. The caller
    /// must separately bind the values and user approvals to an authenticated live session.
    /// </summary>
    internal async Task<PendingPeerInvitationMetadata> StagePendingAsync(PairingSessionIdentity identity,
        MultiPeerInvitation value, string localName, string confirmedHostFingerprint, string expectedDigest,
        DateTimeOffset now, CancellationToken ct = default, TimeProvider? clock = null,
        PairingTlsConsentSession.DeliveryLease? reservationLease = null, PairingPrepareReceipt? receipt = null)
    {
        ValidatePending(identity, value, expectedDigest);
        DeskflowProfile.ValidateName(localName);
        if (!string.Equals(localName, identity.PeerName, StringComparison.Ordinal) ||
            !DeskLinkFingerprint.Matches(identity.HostFingerprint, confirmedHostFingerprint) ||
            identity.ExpiresAtUtc <= now || identity.ExpiresAtUtc > now.AddMinutes(10))
            throw new InvalidOperationException("The pending invitation does not match the confirmed session.");

        var sourceClock = clock ?? TimeProvider.System;
        var lastCheck = now;
        void CheckDeadline()
        {
            var current = sourceClock.GetUtcNow();
            if (current < lastCheck || current >= identity.ExpiresAtUtc || identity.ExpiresAtUtc > current.AddMinutes(10))
                throw new InvalidOperationException("The pending pairing session expired or its clock changed.");
            lastCheck = current;
        }
        CheckDeadline();

        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CheckDeadline();
            await RejectAbortedHistoricalIdentityAsync(identity, ct).ConfigureAwait(false);
            await EnsurePairingHistoryCapacityAsync(ct).ConfigureAwait(false);
            var reservation = await ReadReservationAsync(ct).ConfigureAwait(false);
            if (reservationLease is not null && reservation is null)
                throw new InvalidOperationException("The confirmed pairing reservation is missing.");
            if (reservation is not null)
            {
                if (reservationLease is null || receipt is null) throw new InvalidOperationException("Pairing reservation requires the confirmed session.");
                ValidateReservedInvitation(reservationLease, reservation, identity, value, receipt, ct);
            }
            RejectExistingReadyEvidence(allowReservation: reservation is not null);
            RejectExistingPromotionEvidence();
            var current = await ReadPendingAsync(ct);
            var previousDigest = invitation is null ? null : ComputePendingDigest(invitation);
            var next = new PendingDocument(1, identity, value, expectedDigest, previousDigest);
            if (current is not null)
            {
                if (current != next) throw new InvalidOperationException("Another pending pairing already exists.");
                CheckDeadline();
                return PendingMetadata(current);
            }

            var plain = JsonSerializer.SerializeToUtf8Bytes(next, PendingJson);
            byte[] encrypted;
            try { encrypted = protector.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected pending invitation is invalid or oversized.");
            var path = Path.Combine(directory, PendingInvitationFile);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                RejectLinks(directory); RejectLinks(path); RejectLinks(temporary);
                if (Directory.Exists(path)) throw new IOException("Pending invitation path is not a file.");
                await File.WriteAllBytesAsync(temporary, encrypted, ct);
                ct.ThrowIfCancellationRequested();
                RejectLinks(path);
                CheckDeadline();
                var currentReservation = await ReadReservationAsync(ct).ConfigureAwait(false);
                if (currentReservation != reservation) throw new InvalidOperationException("Pairing reservation changed before staging.");
                if (reservation is not null) ValidateReservedInvitation(reservationLease!, reservation, identity, value, receipt!, ct);
                // Existing pending content must never be silently overwritten.
                File.Move(temporary, path, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return PendingMetadata(next);
        }
        finally { gate.Release(); }
    }

    internal async Task<PendingPeerInvitationMetadata?> GetPendingMetadataAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var pending = await ReadPendingAsync(ct);
            return pending is null ? null : PendingMetadata(pending);
        }
        finally { gate.Release(); }
    }

    /// <summary>Discard only a precisely matching pending transaction, never the active invitation.</summary>
    internal async Task<bool> AbortPendingAsync(PairingSessionIdentity identity, string digest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateDigest(digest);
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var pending = await ReadPendingAsync(ct);
            if (pending is null || pending.Identity != identity || !DigestMatches(pending.InvitationDigest, digest)) return false;
            RejectExistingReadyEvidence();
            RejectExistingPromotionEvidence();
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, PendingInvitationFile);
            RejectLinks(path);
            File.Delete(path);
            return true;
        }
        finally { gate.Release(); }
    }

    private async Task<PendingDocument?> ReadPendingAsync(CancellationToken ct)
    {
        var path = Path.Combine(directory, PendingInvitationFile);
        RejectLinks(directory); RejectLinks(path);
        if (Directory.Exists(path)) throw new InvalidDataException("Pending invitation path is not a file.");
        if (!File.Exists(path)) return null;
        var length = new FileInfo(path).Length;
        if (length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected pending invitation is invalid or oversized.");
        var encrypted = await File.ReadAllBytesAsync(path, ct);
        if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected pending invitation is invalid or oversized.");
        var plain = protector.Unprotect(encrypted);
        try
        {
            if (plain.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Pending invitation is invalid or oversized.");
            PendingDocument value;
            try
            {
                // Decode strictly before parsing; corrupt UTF-8 must not be silently replaced.
                using var parsed = JsonDocument.Parse(StrictPendingUtf8.GetString(plain), new JsonDocumentOptions { MaxDepth = 8 });
                RejectDuplicateFields(parsed.RootElement);
                RequireExactFields(parsed.RootElement, PendingFields);
                RequireExactFields(parsed.RootElement.GetProperty("identity"), IdentityFields);
                RequireExactFields(parsed.RootElement.GetProperty("invitation"), InvitationFields);
                value = parsed.RootElement.Deserialize<PendingDocument>(PendingJson)
                    ?? throw new InvalidDataException("Pending invitation is empty.");
                if (value.Version != 1) throw new InvalidDataException("Pending invitation version is invalid.");
                ValidatePending(value.Identity, value.Invitation, value.InvitationDigest);
                if (value.BaseInvitationDigest is not null) ValidateDigest(value.BaseInvitationDigest);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or DecoderFallbackException)
            { throw new InvalidDataException("Pending invitation is invalid.", error); }
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static void ValidatePending(PairingSessionIdentity identity, MultiPeerInvitation value, string digest)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(value);
        DeskflowProfile.ValidateName(identity.HostName); DeskflowProfile.ValidateName(identity.PeerName);
        if (identity.TransactionId == Guid.Empty || identity.SessionId == Guid.Empty || identity.OfferNonce == Guid.Empty ||
            identity.ExpiresAtUtc == default || string.Equals(identity.HostName, identity.PeerName, StringComparison.OrdinalIgnoreCase) ||
            DeskLinkFingerprint.Normalize(identity.HostFingerprint) != identity.HostFingerprint ||
            DeskLinkFingerprint.Normalize(identity.PeerFingerprint) != identity.PeerFingerprint)
            throw new ArgumentException("Pending pairing identity is invalid.");
        value.Validate();
        if (!string.Equals(value.HostName, identity.HostName, StringComparison.Ordinal) ||
            !string.Equals(value.PeerName, identity.PeerName, StringComparison.Ordinal) ||
            !DeskLinkFingerprint.Matches(value.CertificateSha256, identity.HostFingerprint) ||
            !DigestMatches(ComputePendingDigest(value), digest))
            throw new InvalidOperationException("Pending invitation differs from the reviewed session.");
    }

    private static void RejectDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new InvalidDataException("Pending invitation contains duplicate fields.");
                RejectDuplicateFields(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item);
    }

    private static void RequireExactFields(JsonElement value, IReadOnlyCollection<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Pending invitation object is invalid.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!expected.Contains(field.Name) || !found.Add(field.Name))
                throw new InvalidDataException("Pending invitation fields are invalid.");
        if (found.Count != expected.Count) throw new InvalidDataException("Pending invitation fields are incomplete.");
    }

    private static void ValidateDigest(string digest)
    {
        if (digest is not { Length: 64 } || digest.Any(c => !char.IsAsciiHexDigitUpper(c)))
            throw new ArgumentException("Invalid invitation digest.");
    }

    private static bool DigestMatches(string first, string second)
    {
        ValidateDigest(first); ValidateDigest(second);
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second));
    }

    private static PendingPeerInvitationMetadata PendingMetadata(PendingDocument document) => new(
        document.Identity.TransactionId, document.Identity.SessionId, document.Identity.OfferNonce,
        document.Identity.HostName, document.Identity.PeerName, document.Identity.HostFingerprint,
        document.Identity.PeerFingerprint, document.Identity.ExpiresAtUtc, document.InvitationDigest);
}
