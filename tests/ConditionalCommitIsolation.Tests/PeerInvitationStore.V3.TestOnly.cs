using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

internal sealed record PeerStageV3Inspection(string State, Guid? StageId, PeerIdentityOffer? Offer, bool IsExpired)
{
    public override string ToString() => "[isolated peer history]";
}

public sealed partial class PeerInvitationStore
{
    private static readonly string[] V3Fields = ["version", "stageId", "identity", "offer", "slot", "dataPortBase", "invitation", "invitationDigest", "baseInvitationDigest"];
    private static readonly string[] V3OfferFields = ["version", "name", "platform", "keyboardFingerprint", "nonce", "expiresAtUtc"];
    internal ControlledLivePublishBarrier? BeforePeerV3Move { get; set; }
    private sealed record V3Document(int Version, Guid StageId, PairingSessionIdentity Identity, PeerIdentityOffer Offer, int Slot, int DataPortBase,
        MultiPeerInvitation Invitation, string InvitationDigest, string? BaseInvitationDigest)
    {
        public override string ToString() => "[isolated protected peer history]";
    }

    private static PeerIdentityOffer NormalizeV3Offer(PeerIdentityOffer value) => value with
    {
        KeyboardFingerprint = DeskLinkFingerprint.Normalize(value.KeyboardFingerprint),
        Nonce = Guid.ParseExact(value.Nonce, "N").ToString("N"),
        ExpiresAtUtc = value.ExpiresAtUtc.ToUniversalTime()
    };

    private static void ValidateV3History(V3Document doc)
    {
        if (doc.Version != 3 || doc.Identity is null || doc.Offer is null || doc.Invitation is null)
            throw new InvalidDataException("Invalid isolated peer history.");
        ValidateLiveStage(new LiveStageDocument(2, doc.StageId, doc.Identity, doc.Slot, doc.DataPortBase, doc.Invitation, doc.InvitationDigest, doc.BaseInvitationDigest));
        var offer = doc.Offer; DeskflowProfile.ValidateName(offer.Name);
        if (offer.Version != 1 || offer.Platform is not ("windows" or "mac") || offer.ExpiresAtUtc == default ||
            offer.ExpiresAtUtc.Offset != TimeSpan.Zero || !Guid.TryParseExact(offer.Nonce, "N", out var nonce) || nonce == Guid.Empty ||
            nonce.ToString("N") != offer.Nonce || nonce != doc.Identity.OfferNonce || offer.Name != doc.Identity.PeerName ||
            DeskLinkFingerprint.Normalize(offer.KeyboardFingerprint) != offer.KeyboardFingerprint ||
            offer.KeyboardFingerprint != doc.Identity.PeerFingerprint || doc.Identity.ExpiresAtUtc > offer.ExpiresAtUtc)
            throw new InvalidDataException("Invalid isolated offer history.");
    }

    // Offline synthetic writer only; it creates no confirmation capability and activates nothing.
    internal async Task StagePeerV3IsolatedTestAsync(Guid stage, PairingSessionIdentity identity, PeerIdentityOffer offer,
        MultiPeerInvitation candidate, int slot, int dataPortBase, DateTimeOffset now, Func<DateTimeOffset> clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(offer); ArgumentNullException.ThrowIfNull(candidate); ArgumentNullException.ThrowIfNull(clock);
        var last = now;
        void CheckTime()
        {
            var current = clock();
            if (current < last) throw new InvalidOperationException("Isolated peer clock moved backwards.");
            offer.Validate(current); _ = new PairingConsentState(identity, () => current); last = current;
        }
        CheckTime();
        var normalized = NormalizeV3Offer(offer);
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); RejectPromotionMutationTest();
            if (Volatile.Read(ref liveStageUncertain) != 0) throw new InvalidOperationException("Reopen isolated peer store to inspect uncertain result.");
            CheckTime(); ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, PendingInvitationFile); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            IsolatedTestPathGuard.Validate(directory, temporary, path, PendingInvitationFile);
            if (File.Exists(path) || Directory.Exists(path)) throw new InvalidOperationException("A pending invitation already exists.");
            var doc = new V3Document(3, stage, identity, normalized, slot, dataPortBase, candidate, ComputePendingDigest(candidate), invitation is null ? null : ComputePendingDigest(invitation));
            ValidateV3History(doc);
            var plain = JsonSerializer.SerializeToUtf8Bytes(doc, PendingJson); byte[] encrypted;
            try { encrypted = protector.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid protected peer history.");
            try
            {
                await File.WriteAllBytesAsync(temporary, encrypted, ct);
                if (BeforePeerV3Move is not null) await BeforePeerV3Move.PauseAsync();
                ct.ThrowIfCancellationRequested(); CheckTime(); IsolatedTestPathGuard.Validate(directory, temporary, path, PendingInvitationFile);
                try { File.Move(temporary, path, false); }
                catch { MarkLivePeerStageUncertain(); throw; }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    // Metadata is only a point-in-time offline inspection, never an authorization or recovery result.
    internal async Task<PeerStageV3Inspection> InspectPeerV3IsolatedTestAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Volatile.Read(ref liveStageUncertain) != 0) return new("unknown", null, null, true);
            var path = Path.Combine(directory, PendingInvitationFile); RejectLinks(directory); RejectLinks(path);
            if (!File.Exists(path)) return new("unknown", null, null, true);
            var buffer = new byte[MaximumBytes + 1]; var count = 0;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
            {
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count), ct); if (read == 0) break; count += read;
                }
            }
            if (count is <= 0 or > MaximumBytes) return new("unknown", null, null, true);
            var plain = protector.Unprotect(buffer[..count]);
            try
            {
                if (plain.Length is <= 0 or > MaximumBytes) return new("unknown", null, null, true);
                using var parsed = JsonDocument.Parse(StrictPendingUtf8.GetString(plain), new JsonDocumentOptions { MaxDepth = 8 });
                var root = parsed.RootElement; RejectDuplicateFields(root);
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var v) || !v.TryGetInt32(out var version)) return new("unknown", null, null, true);
                if (version == 1)
                {
                    RequireExactFields(root, PendingFields); RequireExactFields(root.GetProperty("identity"), IdentityFields); RequireExactFields(root.GetProperty("invitation"), InvitationFields);
                    var legacy = root.Deserialize<PendingDocument>(PendingJson) ?? throw new InvalidDataException("Invalid legacy peer record.");
                    ValidatePending(legacy.Identity, legacy.Invitation, legacy.InvitationDigest);
                    if (legacy.BaseInvitationDigest is not null) ValidateDigest(legacy.BaseInvitationDigest);
                    return new("legacy", null, null, true);
                }
                if (version == 2)
                {
                    RequireExactFields(root, LiveStageFields); RequireExactFields(root.GetProperty("identity"), IdentityFields); RequireExactFields(root.GetProperty("invitation"), InvitationFields);
                    var legacy = root.Deserialize<LiveStageDocument>(PendingJson) ?? throw new InvalidDataException("Invalid legacy peer record.");
                    ValidateLiveStage(legacy); return new("legacy", null, null, true);
                }
                RequireExactFields(root, V3Fields); RequireExactFields(root.GetProperty("identity"), IdentityFields);
                RequireExactFields(root.GetProperty("offer"), V3OfferFields); RequireExactFields(root.GetProperty("invitation"), InvitationFields);
                var doc = root.Deserialize<V3Document>(PendingJson) ?? throw new InvalidDataException("Invalid peer history.");
                ValidateV3History(doc);
                return new("v3-history", doc.StageId, doc.Offer, now >= doc.Identity.ExpiresAtUtc || now >= doc.Offer.ExpiresAtUtc);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or FormatException or InvalidOperationException or System.Text.DecoderFallbackException)
        { return new("unknown", null, null, true); }
        finally { gate.Release(); }
    }
}
