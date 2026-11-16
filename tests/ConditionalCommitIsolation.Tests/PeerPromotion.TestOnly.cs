using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

// Only the isolated test assembly uses this store variant and promotion overlay.
public sealed partial class PeerInvitationStore
{
    internal ControlledLivePublishBarrier? BeforePromotionMove { get; set; }
    internal string? PromotionTemporaryOverride { get; set; }
    internal Action? AfterPromotionMove { get; set; }
    private sealed record IsolatedPromotionRecord(int Version, Guid StageId, PairingSessionIdentity Identity, PeerIdentityOffer Offer,
        int Slot, int DataPortBase, MultiPeerInvitation Invitation, string InvitationDigest, string V3CipherDigest, string? BaseCipherDigest)
    {
        public override string ToString() => "[isolated protected promotion]";
    }

    internal void ValidatePromotionTestPaths(string temporary, string target) => IsolatedTestPathGuard.Validate(directory, temporary, target, "peer-promotion.bin");
    internal void MarkPromotionUnknownTest() => Volatile.Write(ref promotionUnknown, 1);

    internal async Task<string> PromoteReservedTestAsync(PairingTlsConsentSession session, object permit, OfflineRecoverySnapshot expected, CancellationToken ct)
    {
        session.ClaimPromotionTest(this, permit, expected, ct);
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RejectLinks(PromotionPath);
            if (Volatile.Read(ref promotionUnknown) != 0 || promotedInvitation is not null || File.Exists(PromotionPath) || Directory.Exists(PromotionPath))
                throw new InvalidOperationException("Promotion requires recovery.");
            async Task CheckSnapshot()
            {
                session.CheckPromotionTest(this, permit, expected, ct);
                var actual = await CaptureOfflinePeerCoreAsync(session.PromotionClockTest(), ct);
                if (actual is null || actual.IsExpired || !ReferenceEquals(expected.Owner, recoveryOwner) || !SamePeerRecoverySnapshot(expected, actual))
                    throw new InvalidOperationException("Peer promotion snapshot changed.");
                session.CheckPromotionTest(this, permit, expected, ct);
            }
            await CheckSnapshot();
            var stagePath = Path.Combine(directory, PendingInvitationFile); RejectLinks(stagePath);
            var encryptedStage = await ReadOfflinePeerFileAsync(stagePath, ct);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(encryptedStage), expected.FirstHash))
                throw new InvalidOperationException("Peer stage ciphertext changed.");
            var stagePlain = protector.Unprotect(encryptedStage); V3Document document;
            try
            {
                using var parsed = JsonDocument.Parse(StrictPendingUtf8.GetString(stagePlain), new JsonDocumentOptions { MaxDepth = 8 });
                var root = parsed.RootElement; RejectDuplicateFields(root); RequireExactFields(root, V3Fields);
                RequireExactFields(root.GetProperty("identity"), IdentityFields); RequireExactFields(root.GetProperty("offer"), V3OfferFields);
                RequireExactFields(root.GetProperty("invitation"), InvitationFields);
                document = root.Deserialize<V3Document>(PendingJson) ?? throw new InvalidDataException("Invalid protected peer stage.");
                ValidateV3History(document);
            }
            finally { CryptographicOperations.ZeroMemory(stagePlain); }
            var binding = expected.Binding;
            if (document.StageId != binding.Stage || document.Identity != binding.Identity || document.Offer != binding.Offer ||
                document.Slot != binding.Slot || document.DataPortBase != binding.DataPortBase || !DigestMatches(document.InvitationDigest, binding.InvitationDigest))
                throw new InvalidOperationException("Peer promotion stage does not match.");
            var record = new IsolatedPromotionRecord(1, document.StageId, document.Identity, document.Offer, document.Slot, document.DataPortBase,
                document.Invitation, document.InvitationDigest, Convert.ToHexString(expected.FirstHash), expected.ActiveHash is null ? null : Convert.ToHexString(expected.ActiveHash));
            var plain = JsonSerializer.SerializeToUtf8Bytes(record, PendingJson); byte[] encrypted;
            try { encrypted = protector.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid protected promotion record.");
            var temporary = PromotionTemporaryOverride ?? PromotionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var ownsTemporary = false;
            try
            {
                ValidatePromotionTestPaths(temporary, PromotionPath);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                {
                    ownsTemporary = true;
                    await output.WriteAsync(encrypted, ct);
                }
                if (BeforePromotionMove is not null) await BeforePromotionMove.PauseAsync();
                await CheckSnapshot();
                session.PublishPromotionTest(this, permit, expected, temporary, PromotionPath, ct);
                try
                {
                    AfterPromotionMove?.Invoke();
                    promotedSnapshot = expected; promotionOwnerSession = session; promotionRecordHash = SHA256.HashData(encrypted);
                    Volatile.Write(ref promotedInvitation, document.Invitation);
                    Volatile.Write(ref promotionUnknown, 0);
                }
                catch { Volatile.Write(ref promotionUnknown, 1); throw; }
                return "promoted";
            }
            finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }
}

internal sealed partial class PairingTlsConsentSession
{
    internal ControlledLivePublishBarrier? AfterPromotionFinalCheck { get; set; }
    private object? activePromotionPermit;
    private PeerInvitationStore? activePromotionStore;
    private OfflineRecoverySnapshot? activePromotionSnapshot;
    private bool activePromotionClaimed;

    internal async Task<string> PromoteWithTicketTestAsync(PeerInvitationStore store, object ticket, CancellationToken ct = default)
    {
        object permit; OfflineRecoverySnapshot snapshot;
        lock (gate)
        {
            RequireActive(); ct.ThrowIfCancellationRequested();
            if (promotionTicket is null || !ReferenceEquals(ticket, promotionTicket) || !ReferenceEquals(store, promotionStore) || promotionSnapshot is null || activePromotionPermit is not null)
                throw new InvalidOperationException("Promotion ticket does not match.");
            snapshot = promotionSnapshot; promotionTicket = null; promotionStore = null; promotionSnapshot = null;
            activePromotionStore = store; activePromotionSnapshot = snapshot; activePromotionPermit = permit = new object(); activePromotionClaimed = false; activeOperations++;
        }
        try
        {
            using var deadline = CreateDeadline(ct);
            return await store.PromoteReservedTestAsync(this, permit, snapshot, deadline.Token);
        }
        finally
        {
            lock (gate) { activePromotionPermit = null; activePromotionStore = null; activePromotionSnapshot = null; activePromotionClaimed = false; }
            FinishOperation();
        }
    }

    internal void ClaimPromotionTest(PeerInvitationStore store, object permit, OfflineRecoverySnapshot snapshot, CancellationToken ct)
    {
        lock (gate)
        {
            RequireActive(); ct.ThrowIfCancellationRequested();
            if (activePromotionClaimed || !ReferenceEquals(activePromotionPermit, permit) || !ReferenceEquals(activePromotionStore, store) || !ReferenceEquals(activePromotionSnapshot, snapshot))
                throw new InvalidOperationException("Promotion was not reserved for this session.");
            activePromotionClaimed = true;
        }
    }
    internal DateTimeOffset PromotionClockTest() => clock();
    internal void CheckPromotionTest(PeerInvitationStore store, object permit, OfflineRecoverySnapshot snapshot, CancellationToken ct)
    {
        lock (gate)
        {
            RequireActive(); ct.ThrowIfCancellationRequested();
            if (!activePromotionClaimed || !ReferenceEquals(activePromotionPermit, permit) || !ReferenceEquals(activePromotionStore, store) || !ReferenceEquals(activePromotionSnapshot, snapshot))
                throw new InvalidOperationException("Promotion reservation is no longer active.");
        }
    }
    internal void PublishPromotionTest(PeerInvitationStore store, object permit, OfflineRecoverySnapshot snapshot, string temporary, string target, CancellationToken ct)
    {
        store.ValidatePromotionTestPaths(temporary, target);
        lock (gate)
        {
            RequireActive(); ct.ThrowIfCancellationRequested();
            if (!activePromotionClaimed || !ReferenceEquals(activePromotionPermit, permit) || !ReferenceEquals(activePromotionStore, store) || !ReferenceEquals(activePromotionSnapshot, snapshot))
                throw new InvalidOperationException("Promotion reservation is no longer active.");
            store.MarkPromotionUnknownTest();
            AfterPromotionFinalCheck?.PauseInsideGate();
            File.Move(temporary, target, false);
        }
    }
}
