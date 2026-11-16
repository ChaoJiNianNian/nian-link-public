using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    internal ControlledLivePublishBarrier? BeforeCommittedSend { get; set; }
    internal ControlledLivePublishBarrier? BeforeCommittedVerify { get; set; }
    private object? activeCommittedSendPermit;
    private IsolatedGrantKey? activeCommittedSendKey;
    private async Task VerifyCommittedAndSendTestAsync(PairingTlsConsentSession session, object capability, object permit,
        LivePreparePayload payload, PairingSessionIdentity identity, PeerIdentityOffer offer,
        DateTimeOffset now, Func<DateTimeOffset> clock, CancellationToken ct)
    {
        object sendPermit;
        if (BeforeCommittedVerify is not null) await BeforeCommittedVerify.PauseAsync();
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed(); session.CheckLiveVaultTest(capability, ct);
            if (!ReferenceEquals(activePrepareSession, session) || !ReferenceEquals(activePreparePermit, permit) ||
                !ReferenceEquals(activePreparePayload, payload) || !activePrepareStarted || activePrepareMarkerHash is null || activePreparePendingHash is null)
                throw new InvalidOperationException("Committed notification has no active lease.");
            var captured = await ReadPendingGrantSnapshotAsync(ct); var pending = captured.Document;
            RejectLinks(PrepareMarkerPath); var marker = await ReadBoundedVaultFileAsync(PrepareMarkerPath, ct);
            if (pending is null || pending.Frozen is null || captured.Ciphertext is null || pending.Identity != identity || pending.Offer != offer ||
                pending.StageId != payload.StageId || pending.Slot != payload.Slot || pending.Frozen.DataPortBase != payload.DataPortBase ||
                !FixedDigestEquals(pending.Frozen.InvitationDigest, payload.Digest) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(captured.Ciphertext), activePreparePendingHash) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(marker), activePrepareMarkerHash))
                throw new InvalidOperationException("Committed notification files do not match.");
            var currentTime = clock(); if (currentTime < now || currentTime < pending.CreatedAtUtc) throw new InvalidOperationException("Clock moved backwards.");
            ValidatePendingBinding(identity, offer, currentTime); ValidateFrozenInvitation(pending);
            var key = new IsolatedGrantKey(identity, payload.StageId, offer.Platform, pending.Slot, pending.Frozen.InvitationDigest, VaultTokenDigest(pending.Token));
            if (InspectIsolatedCore(key) != "committed" || await VerifyOfflineActiveVaultAsync(vaultSnapshot, ct) is null)
                throw new InvalidOperationException("Committed grant is not current.");
            session.CheckLiveVaultTest(capability, ct);
            if (activeCommittedSendPermit is not null) throw new InvalidOperationException("Committed send is already registered.");
            activeCommittedSendKey = key;
            activeCommittedSendPermit = sendPermit = new object();
        }
        finally { gate.Release(); }
        if (BeforeCommittedSend is not null) await BeforeCommittedSend.PauseAsync();
        await session.SendCommittedFrameTestAsync(capability, this, sendPermit, payload, ct);
    }

    internal async Task ClaimCommittedSendTestAsync(PairingTlsConsentSession session, object permit, LivePreparePayload payload, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed(); ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(activePrepareSession, session) || !ReferenceEquals(activePreparePayload, payload) ||
                activeCommittedSendPermit is null || !ReferenceEquals(permit, activeCommittedSendPermit) || activeCommittedSendKey is null ||
                activePreparePendingHash is null || activePrepareMarkerHash is null)
                throw new InvalidOperationException("Committed send is not registered by the vault.");
            var pending = await ReadPendingGrantSnapshotAsync(ct); RejectLinks(PrepareMarkerPath);
            var marker = await ReadBoundedVaultFileAsync(PrepareMarkerPath, ct);
            if (pending.Ciphertext is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(pending.Ciphertext), activePreparePendingHash) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(marker), activePrepareMarkerHash) ||
                InspectIsolatedCore(activeCommittedSendKey) != "committed" || await VerifyOfflineActiveVaultAsync(vaultSnapshot, ct) is null)
                throw new InvalidOperationException("Committed grant changed before transmission.");
            ct.ThrowIfCancellationRequested();
            activeCommittedSendPermit = null; activeCommittedSendKey = null;
        }
        finally { gate.Release(); }
    }
}

internal sealed partial class PairingTlsConsentSession
{
    [JsonConverter(typeof(RejectPromotionTicketJson))]
    private sealed class PromotionTicket
    {
        public override string ToString() => "[isolated promotion ticket]";
    }
    private sealed class RejectPromotionTicketJson : JsonConverter<PromotionTicket>
    {
        public override PromotionTicket? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, PromotionTicket value, JsonSerializerOptions options) => throw new NotSupportedException();
    }
    private PromotionTicket? promotionTicket;
    private PeerInvitationStore? promotionStore;
    private OfflineRecoverySnapshot? promotionSnapshot;

    internal async Task SendCommittedFrameTestAsync(object capability, MultiDeviceCredentialVault vault, object permit, LivePreparePayload payload, CancellationToken ct)
    {
        await vault.ClaimCommittedSendTestAsync(this, permit, payload, ct);
        CheckLiveVaultTest(capability, ct);
        await WriteReadyFrameTestAsync(new { version = 1, type = "committed", role, sessionId = identity.SessionId,
            transactionId = identity.TransactionId, offerNonce = identity.OfferNonce, stageId = payload.StageId,
            senderChallenge = challenge, receiverChallenge = remoteChallenge, slot = payload.Slot,
            dataPortBase = payload.DataPortBase, digest = payload.Digest }, ct);
    }

    private async Task<OfflineRecoverySnapshot> CapturePeerStageForCommittedTestAsync(PeerInvitationStore store, object capability, byte[] publishedHash,
        Guid stage, int slot, int dataPortBase, string digest, CancellationToken ct)
    {
        CheckLiveVaultTest(capability, ct);
        var snapshot = await store.CaptureOfflineRecoveryTestAsync(clock(), ct);
        if (snapshot is null || snapshot.IsExpired || !CryptographicOperations.FixedTimeEquals(snapshot.FirstHash, publishedHash) ||
            snapshot.Binding.Identity != identity || snapshot.Binding.Offer != offer || snapshot.Binding.Stage != stage ||
            snapshot.Binding.Slot != slot || snapshot.Binding.DataPortBase != dataPortBase || !SamePrepareDigest(snapshot.Binding.InvitationDigest, digest) ||
            !await store.IsOfflineRecoverySnapshotCurrentAsync(snapshot, clock(), ct))
            throw new InvalidOperationException("Published peer stage does not match its READY snapshot.");
        CheckLiveVaultTest(capability, ct); return snapshot;
    }

    private async Task<object> ReceiveCommittedAndIssueTicketTestAsync(PeerInvitationStore store, object capability, OfflineRecoverySnapshot snapshot,
        Guid stage, int slot, int dataPortBase, string digest, CancellationToken ct)
    {
        using var frame = await ReadPrepareFrameAsync(ct); var root = frame.RootElement;
        CheckProtocolHeader(root, "committed", "host", ReadyFields);
        if (!root.GetProperty("stageId").TryGetGuid(out var receivedStage) || receivedStage != stage ||
            root.GetProperty("slot").GetInt32() != slot || root.GetProperty("dataPortBase").GetInt32() != dataPortBase ||
            !SamePrepareDigest(root.GetProperty("digest").GetString(), digest))
            throw new InvalidDataException("Committed does not match the peer stage.");
        CheckLiveVaultTest(capability, ct);
        if (!await store.IsOfflineRecoverySnapshotCurrentAsync(snapshot, clock(), ct))
            throw new InvalidOperationException("Protected peer state changed.");
        lock (gate)
        {
            RequireActive(); ct.ThrowIfCancellationRequested();
            if (role != "peer" || !ReferenceEquals(capability, isolatedCapability) || !isolatedActionClaimed || promotionTicket is not null)
                throw new InvalidOperationException("Promotion ticket cannot be issued.");
            promotionStore = store; promotionSnapshot = snapshot;
            return promotionTicket = new PromotionTicket();
        }
    }

    internal async Task<bool> ConsumePromotionTicketTestAsync(PeerInvitationStore store, object ticket, CancellationToken ct = default)
    {
        OfflineRecoverySnapshot snapshot;
        lock (gate)
        {
            RequireActive(); ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(ticket, promotionTicket) || promotionTicket is null || !ReferenceEquals(store, promotionStore) || promotionSnapshot is null)
                throw new InvalidOperationException("Promotion ticket does not match this session and store.");
            snapshot = promotionSnapshot; promotionTicket = null; promotionStore = null; promotionSnapshot = null; activeOperations++;
        }
        try
        {
            using var deadline = CreateDeadline(ct);
            if (!await store.IsOfflineRecoverySnapshotCurrentAsync(snapshot, clock(), deadline.Token) || snapshot.IsExpired)
                throw new InvalidOperationException("Promotion snapshot is no longer current.");
            lock (gate) { RequireActive(); deadline.Token.ThrowIfCancellationRequested(); }
            return true;
        }
        finally { FinishOperation(); }
    }
}
