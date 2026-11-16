using System.Security.Cryptography;

namespace DesktopToolbox.DeskLink;

internal sealed record AppliedBinding(OfflineRecoveryBinding Value)
{
    public override string ToString() => "[isolated applied binding]";
}

public sealed partial class PeerInvitationStore
{
    private OfflineRecoverySnapshot? promotedSnapshot;
    private PairingTlsConsentSession? promotionOwnerSession;
    private byte[]? promotionRecordHash;
    private sealed class AppliedPermit { internal int Claimed; }
    private AppliedPermit? appliedPermit;
    private AppliedBinding? appliedBinding;
    private bool appliedRegistered;
    internal ControlledLivePublishBarrier? BeforeAppliedSend { get; set; }

    private async Task ValidateAppliedStateTestAsync(PairingTlsConsentSession session, CancellationToken ct)
    {
        try
        {
        ObjectDisposedException.ThrowIf(disposed, this); session.CheckAppliedSessionTest(ct);
        var snapshot = promotedSnapshot; var current = Volatile.Read(ref promotedInvitation);
        if (Volatile.Read(ref promotionUnknown) != 0 || snapshot is null || current is null || promotionRecordHash is null ||
            !ReferenceEquals(promotionOwnerSession, session) || snapshot.IsExpired ||
            !DigestMatches(ComputePendingDigest(current), snapshot.Binding.InvitationDigest))
            throw new InvalidOperationException("Applied promotion state is unavailable.");
        RejectLinks(directory); RejectLinks(PromotionPath);
        var record = await ReadOfflinePeerFileAsync(PromotionPath, ct);
        var stagePath = Path.Combine(directory, PendingInvitationFile); RejectLinks(stagePath);
        var stage = await ReadOfflinePeerFileAsync(stagePath, ct);
        RejectLinks(file);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(record), promotionRecordHash) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stage), snapshot.FirstHash))
            throw new InvalidOperationException("Applied promotion evidence changed.");
        if (snapshot.ActiveHash is null)
        {
            if (File.Exists(file) || Directory.Exists(file)) throw new InvalidOperationException("Prior invitation changed.");
        }
        else
        {
            var active = await ReadOfflinePeerFileAsync(file, ct);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(active), snapshot.ActiveHash)) throw new InvalidOperationException("Prior invitation changed.");
        }
        session.CheckAppliedSessionTest(ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or ArgumentException)
        {
            Volatile.Write(ref promotionUnknown, 1);
            throw;
        }
    }

    internal async Task<(object Permit, AppliedBinding Binding)> RegisterAppliedTestAsync(PairingTlsConsentSession session, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await ValidateAppliedStateTestAsync(session, ct);
            if (appliedRegistered) throw new InvalidOperationException("Applied was already registered.");
            appliedRegistered = true; appliedPermit = new AppliedPermit(); appliedBinding = new AppliedBinding(promotedSnapshot!.Binding);
            return (appliedPermit, appliedBinding);
        }
        finally { gate.Release(); }
    }

    internal async Task ClaimAppliedTestAsync(PairingTlsConsentSession session, object permit, AppliedBinding binding, CancellationToken ct)
    {
        var registered = Volatile.Read(ref appliedPermit);
        if (registered is null || !ReferenceEquals(permit, registered) || !ReferenceEquals(binding, Volatile.Read(ref appliedBinding)) ||
            Interlocked.CompareExchange(ref registered.Claimed, 1, 0) != 0)
            throw new InvalidOperationException("Applied send was not registered or was already claimed.");
        await gate.WaitAsync(ct);
        try
        {
            if (!ReferenceEquals(appliedPermit, registered) || !ReferenceEquals(binding, appliedBinding))
                throw new InvalidOperationException("Applied send is no longer registered.");
            appliedPermit = null; appliedBinding = null;
            await ValidateAppliedStateTestAsync(session, ct);
        }
        finally { gate.Release(); }
    }
}

public sealed partial class MultiDeviceCredentialVault
{
    internal ControlledLivePublishBarrier? BeforeAppliedFinalCheck { get; set; }
    internal TaskCompletionSource? BeforeAppliedFinalGateWait { get; set; }
    private async Task VerifyAppliedObservationTestAsync(PairingTlsConsentSession session, object capability, object permit,
        LivePreparePayload payload, PairingSessionIdentity identity, PeerIdentityOffer offer,
        DateTimeOffset now, Func<DateTimeOffset> clock, CancellationToken ct)
    {
        if (BeforeAppliedFinalCheck is not null) await BeforeAppliedFinalCheck.PauseAsync();
        BeforeAppliedFinalGateWait?.TrySetResult();
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed(); session.CheckLiveVaultTest(capability, ct);
            if (!ReferenceEquals(activePrepareSession, session) || !ReferenceEquals(activePreparePermit, permit) ||
                !ReferenceEquals(activePreparePayload, payload) || !activePrepareStarted || activePrepareMarkerHash is null || activePreparePendingHash is null)
                throw new InvalidOperationException("Applied observation has no active lease.");
            var captured = await ReadPendingGrantSnapshotAsync(ct); var pending = captured.Document;
            RejectLinks(PrepareMarkerPath); var marker = await ReadBoundedVaultFileAsync(PrepareMarkerPath, ct);
            if (pending is null || pending.Frozen is null || captured.Ciphertext is null || pending.Identity != identity || pending.Offer != offer ||
                pending.StageId != payload.StageId || pending.Slot != payload.Slot || pending.Frozen.DataPortBase != payload.DataPortBase ||
                !FixedDigestEquals(pending.Frozen.InvitationDigest, payload.Digest) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(captured.Ciphertext), activePreparePendingHash) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(marker), activePrepareMarkerHash))
                throw new InvalidOperationException("Applied observation files changed.");
            var currentTime = clock(); if (currentTime < now || currentTime < pending.CreatedAtUtc) throw new InvalidOperationException("Clock moved backwards.");
            ValidatePendingBinding(identity, offer, currentTime); ValidateFrozenInvitation(pending);
            var key = new IsolatedGrantKey(identity, payload.StageId, offer.Platform, pending.Slot, pending.Frozen.InvitationDigest, VaultTokenDigest(pending.Token));
            if (InspectIsolatedCore(key) != "committed" || await VerifyOfflineActiveVaultAsync(vaultSnapshot, ct) is null)
                throw new InvalidOperationException("Applied grant is no longer current.");
            session.CheckLiveVaultTest(capability, ct);
        }
        finally { gate.Release(); }
    }
}

internal sealed partial class PairingTlsConsentSession
{
    private bool appliedReceived;
    internal void CheckAppliedSessionTest(CancellationToken ct)
    {
        lock (gate)
        {
            RequireActive(); ct.ThrowIfCancellationRequested();
            if (role != "peer" || isolatedCapability is null || !isolatedActionClaimed || consent.Phase != PairingConsentPhase.ReadyToPrepare)
                throw new InvalidOperationException("Applied session is no longer active.");
        }
    }

    internal async Task PromoteAndSendAppliedTestAsync(PeerInvitationStore store, object ticket, CancellationToken ct = default)
    {
        lock (gate) { RequireActive(); ct.ThrowIfCancellationRequested(); activeOperations++; }
        try
        {
            await PromoteWithTicketTestAsync(store, ticket, ct);
            using var deadline = CreateDeadline(ct);
            var (permit, binding) = await store.RegisterAppliedTestAsync(this, deadline.Token);
            if (store.BeforeAppliedSend is not null) await store.BeforeAppliedSend.PauseAsync();
            await SendAppliedFrameTestAsync(store, permit, binding, deadline.Token);
        }
        catch { StopLivePrepareTest(); throw; }
        finally { FinishOperation(); }
    }

    internal async Task SendAppliedFrameTestAsync(PeerInvitationStore store, object permit, AppliedBinding applied, CancellationToken ct)
    {
        await store.ClaimAppliedTestAsync(this, permit, applied, ct);
        CheckAppliedSessionTest(ct); var value = applied.Value;
        await WriteReadyFrameTestAsync(new { version = 1, type = "applied", role, sessionId = identity.SessionId,
            transactionId = identity.TransactionId, offerNonce = identity.OfferNonce, stageId = value.Stage,
            senderChallenge = challenge, receiverChallenge = remoteChallenge, slot = value.Slot,
            dataPortBase = value.DataPortBase, digest = value.InvitationDigest }, ct);
    }

    internal async Task ReceiveAppliedFrameTestAsync(object capability, LivePreparePayload payload, CancellationToken ct)
    {
        CheckLiveVaultTest(capability, ct);
        using var frame = await ReadPrepareFrameAsync(ct); var root = frame.RootElement;
        CheckProtocolHeader(root, "applied", "peer", ReadyFields);
        if (!root.GetProperty("stageId").TryGetGuid(out var stage) || stage != payload.StageId ||
            root.GetProperty("slot").GetInt32() != payload.Slot || root.GetProperty("dataPortBase").GetInt32() != payload.DataPortBase ||
            !SamePrepareDigest(root.GetProperty("digest").GetString(), payload.Digest)) throw new InvalidDataException("Applied does not match prepare.");
        lock (gate)
        {
            RequireActive(); ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(capability, isolatedCapability) || !isolatedActionClaimed || appliedReceived) throw new InvalidOperationException("Applied was already received.");
            appliedReceived = true;
        }
    }
}
