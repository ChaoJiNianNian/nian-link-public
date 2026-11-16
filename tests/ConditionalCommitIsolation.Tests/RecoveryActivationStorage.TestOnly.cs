using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

// Recovery activation is a test-only experiment; neither API is in the product assembly.
public sealed partial class MultiDeviceCredentialVault
{
    private RecoveryActivationSession? activeRecoverySession;
    private TaskCompletionSource? activeRecoveryDrained;
    private sealed class ActivationSendPermit { internal int Claimed; }
    internal TaskCompletionSource? BeforeActivationReleaseGateWait { get; set; }
    internal TaskCompletionSource? HostActivationGateWaiting { get; set; }
    private ActivationSendPermit? activationSendPermit;
    private RecoveryActivationSession? activationSendSession;
    private OfflineRecoverySnapshot? activationSendSnapshot;

    private bool SameActivationHostSnapshot(OfflineRecoverySnapshot a, OfflineRecoverySnapshot b) =>
        ReferenceEquals(a.Owner, recoveryOwner) && ReferenceEquals(a.Generation, b.Generation) && a.HasSameBinding(b) &&
        CryptographicOperations.FixedTimeEquals(a.FirstHash, b.FirstHash) && CryptographicOperations.FixedTimeEquals(a.SecondHash, b.SecondHash) &&
        EqualOptionalHash(a.ActiveHash, b.ActiveHash) && !a.IsExpired && !b.IsExpired && a.MarkerVersion == 2 && b.MarkerVersion == 2;

    internal async Task BeginHostRecoveryTestAsync(RecoveryActivationSession session, OfflineRecoverySnapshot snapshot, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed(); session.CheckOwner(true, snapshot, ct);
            var fresh = await CapturePromotedRecoveryCoreTestAsync(session.CheckInitialTime(ct), ct);
            if (activeRecoverySession is not null || fresh is null || !SameActivationHostSnapshot(snapshot, fresh)) throw new InvalidOperationException("Host recovery session is unavailable.");
            activeRecoverySession = session; activeRecoveryDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        finally { gate.Release(); }
    }
    internal async Task EndHostRecoveryTestAsync(RecoveryActivationSession session)
    {
        await gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(activeRecoverySession, session)) throw new InvalidOperationException("Host recovery lease does not match.");
            activeRecoverySession = null; activeRecoveryDrained!.TrySetResult(); activeRecoveryDrained = null;
        }
        finally { gate.Release(); }
    }

    internal async Task<object> RegisterActivationSendTestAsync(RecoveryActivationSession session, OfflineRecoverySnapshot snapshot, CancellationToken ct)
    {
        var waiting = gate.WaitAsync(ct);
        if (!waiting.IsCompleted) HostActivationGateWaiting?.TrySetResult();
        await waiting;
        try
        {
            ThrowIfDisposed(); session.CheckHostConfirmed(snapshot, ct);
            if (!ReferenceEquals(activeRecoverySession, session)) throw new InvalidOperationException("Host recovery lease is unavailable.");
            var fresh = await CapturePromotedRecoveryCoreTestAsync(session.CheckTime(ct), ct);
            if (fresh is null || !SameActivationHostSnapshot(snapshot, fresh) || activationSendPermit is not null)
                throw new InvalidOperationException("Recovery host authorization is not current.");
            session.CheckHostConfirmed(snapshot, ct);
            activationSendSession = session; activationSendSnapshot = snapshot; return activationSendPermit = new ActivationSendPermit();
        }
        finally { gate.Release(); }
    }

    internal async Task ClaimActivationSendTestAsync(RecoveryActivationSession session, OfflineRecoverySnapshot snapshot, object permit, CancellationToken ct)
    {
        var registered = Volatile.Read(ref activationSendPermit);
        if (registered is null || !ReferenceEquals(registered, permit) || !ReferenceEquals(Volatile.Read(ref activationSendSession), session) ||
            !ReferenceEquals(Volatile.Read(ref activationSendSnapshot), snapshot) || Interlocked.CompareExchange(ref registered.Claimed, 1, 0) != 0)
            throw new InvalidOperationException("Recovery authorization permit does not match or was already claimed.");
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(activeRecoverySession, session) || !ReferenceEquals(activationSendPermit, registered) || !ReferenceEquals(activationSendSession, session) || !ReferenceEquals(activationSendSnapshot, snapshot))
                throw new InvalidOperationException("Recovery authorization permit no longer matches.");
            activationSendPermit = null; activationSendSession = null; activationSendSnapshot = null;
            session.CheckHostConfirmed(snapshot, ct);
            var fresh = await CapturePromotedRecoveryCoreTestAsync(session.CheckTime(ct), ct);
            if (fresh is null || !SameActivationHostSnapshot(snapshot, fresh)) throw new InvalidOperationException("Recovery host grant changed.");
            session.CheckHostConfirmed(snapshot, ct);
        }
        finally { gate.Release(); }
    }

    internal async Task ReleaseActivationSendTestAsync(RecoveryActivationSession session)
    {
        BeforeActivationReleaseGateWait?.TrySetResult();
        await gate.WaitAsync();
        try { if (ReferenceEquals(activationSendSession, session)) { activationSendSession = null; activationSendSnapshot = null; activationSendPermit = null; } }
        finally { gate.Release(); }
    }
}

public sealed partial class PeerInvitationStore
{
    private RecoveryActivationSession? activeRecoverySession;
    private TaskCompletionSource? activeRecoveryDrained;
    private Task? isolatedRecoveryDisposeTask;
    internal TaskCompletionSource? BeforePeerRecoveryEndGateWait { get; set; }
    internal async Task BeginPeerRecoveryTestAsync(RecoveryActivationSession session, OfflineRecoverySnapshot snapshot, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); session.CheckOwner(false, snapshot, ct);
            var fresh = await CapturePromotedRecoveryCoreTestAsync(session.CheckInitialTime(ct), ct);
            if (activeRecoverySession is not null || fresh is null || !SameActivationPeerSnapshot(snapshot, fresh)) throw new InvalidOperationException("Peer recovery session is unavailable.");
            activeRecoverySession = session; activeRecoveryDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        finally { gate.Release(); }
    }
    internal async Task EndPeerRecoveryTestAsync(RecoveryActivationSession session)
    {
        BeforePeerRecoveryEndGateWait?.TrySetResult();
        await gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(activeRecoverySession, session)) throw new InvalidOperationException("Peer recovery lease does not match.");
            activeRecoverySession = null; activeRecoveryDrained!.TrySetResult(); activeRecoveryDrained = null;
        }
        finally { gate.Release(); }
    }

    private sealed record IsolatedRecoveryRecord(int Version, Guid RecoveryId, Guid RequestId, PairingSessionIdentity Identity, PeerIdentityOffer Offer,
        Guid StageId, int Slot, int DataPortBase, string InvitationDigest, string PromotionCipherDigest, string V3CipherDigest, string? BaseCipherDigest, DateTimeOffset ExpiresAtUtc)
    {
        public override string ToString() => "[isolated protected recovery]";
    }
    internal TaskCompletionSource? BeforeActivationGateWait { get; set; }
    internal ControlledLivePublishBarrier? BeforeRecoveryMove { get; set; }
    internal ControlledLivePublishBarrier? AfterRecoveryFinalCheck { get; set; }
    internal Action? AfterRecoveryMove { get; set; }
    internal string? RecoveryTemporaryOverride { get; set; }
    internal void ValidateRecoveryTestPaths(string temporary, string target) => IsolatedTestPathGuard.Validate(directory, temporary, target, "peer-recovery.bin");

    private bool SameActivationPeerSnapshot(OfflineRecoverySnapshot a, OfflineRecoverySnapshot b) =>
        ReferenceEquals(a.Owner, recoveryOwner) && SamePeerRecoverySnapshot(a, b) && CryptographicOperations.FixedTimeEquals(a.SecondHash, b.SecondHash) && !a.IsExpired && !b.IsExpired;

    internal async Task<string> ActivateReservedTestAsync(RecoveryActivationSession session, OfflineRecoverySnapshot snapshot, object permit, CancellationToken ct)
    {
        session.ClaimPeerActivation(this, snapshot, permit, ct);
        var waiting = gate.WaitAsync(ct);
        if (!waiting.IsCompleted) BeforeActivationGateWait?.TrySetResult();
        await waiting;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); RejectLinks(RecoveryPath);
            if (!ReferenceEquals(activeRecoverySession, session)) throw new InvalidOperationException("Peer recovery lease is unavailable.");
            if (Volatile.Read(ref promotionUnknown) == 0 || File.Exists(RecoveryPath) || Directory.Exists(RecoveryPath)) throw new InvalidOperationException("Recovery record is unavailable.");
            async Task Check()
            {
                session.CheckPeerActivation(this, snapshot, permit, ct);
                var fresh = await CapturePromotedRecoveryCoreTestAsync(session.CheckTime(ct), ct);
                if (fresh is null || !SameActivationPeerSnapshot(snapshot, fresh)) throw new InvalidOperationException("Recovery peer state changed.");
                session.CheckPeerActivation(this, snapshot, permit, ct);
            }
            await Check();
            var promotionBytes = await ReadOfflinePeerFileAsync(PromotionPath, ct);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(promotionBytes), snapshot.FirstHash)) throw new InvalidOperationException("Recovery promotion changed.");
            var promotionPlain = protector.Unprotect(promotionBytes); IsolatedPromotionRecord promotion;
            try
            {
                using var json = JsonDocument.Parse(StrictPendingUtf8.GetString(promotionPlain), new JsonDocumentOptions { MaxDepth = 8 }); var root = json.RootElement;
                RejectDuplicateFields(root); RequireExactFields(root, RecoveryPromotionFields); RequireExactFields(root.GetProperty("identity"), IdentityFields);
                RequireExactFields(root.GetProperty("offer"), V3OfferFields); RequireExactFields(root.GetProperty("invitation"), InvitationFields);
                promotion = root.Deserialize<IsolatedPromotionRecord>(PendingJson) ?? throw new InvalidDataException("Invalid isolated promotion.");
            }
            finally { CryptographicOperations.ZeroMemory(promotionPlain); }
            var b = snapshot.Binding;
            if (promotion.Version != 1 || promotion.StageId != b.Stage || promotion.Identity != b.Identity || promotion.Offer != b.Offer ||
                promotion.Slot != b.Slot || promotion.DataPortBase != b.DataPortBase || promotion.Invitation is null ||
                !DigestMatches(promotion.InvitationDigest, b.InvitationDigest) || !DigestMatches(ComputePendingDigest(promotion.Invitation), b.InvitationDigest))
                throw new InvalidOperationException("Recovery invitation does not match.");
            var record = new IsolatedRecoveryRecord(1, session.RecoveryId, session.RequestId, b.Identity, b.Offer, b.Stage, b.Slot, b.DataPortBase, b.InvitationDigest,
                Convert.ToHexString(snapshot.FirstHash), Convert.ToHexString(snapshot.SecondHash), snapshot.ActiveHash is null ? null : Convert.ToHexString(snapshot.ActiveHash), session.ExpiresAtUtc);
            var plain = JsonSerializer.SerializeToUtf8Bytes(record, PendingJson); byte[] encrypted;
            try { encrypted = protector.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid protected recovery record.");
            var temporary = RecoveryTemporaryOverride ?? RecoveryPath + "." + Guid.NewGuid().ToString("N") + ".tmp"; var owned = false;
            try
            {
                ValidateRecoveryTestPaths(temporary, RecoveryPath);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                { owned = true; await output.WriteAsync(encrypted, ct); }
                if (BeforeRecoveryMove is not null) await BeforeRecoveryMove.PauseAsync();
                await Check();
                session.PublishPeerActivation(this, snapshot, permit, temporary, RecoveryPath, ct);
                try { AfterRecoveryMove?.Invoke(); Volatile.Write(ref promotedInvitation, promotion.Invitation); Volatile.Write(ref promotionUnknown, 0); }
                catch { Volatile.Write(ref promotionUnknown, 1); throw; }
                return "test-process-recovered";
            }
            finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }
}
