using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

internal sealed record LivePreparePayload(Guid StageId, int Slot, int DataPortBase, string Digest, MultiPeerInvitation Invitation)
{
    public override string ToString() => "[isolated prepare]";
}

public sealed partial class MultiDeviceCredentialVault
{
    internal ControlledLivePublishBarrier? BeforeLivePrepareSend { get; set; }
    internal TaskCompletionSource? BeforePrepareReleaseGateWait { get; set; }
    private PairingTlsConsentSession? activePrepareSession;
    private TaskCompletionSource? activePrepareDrained;
    private object? activePreparePermit;
    private LivePreparePayload? activePreparePayload;
    private byte[]? activePreparePendingHash;
    private byte[]? activePrepareMarkerHash;
    private bool activePrepareStarted;
    private Task? isolatedDisposeTask;
    private string PrepareMarkerPath => Path.Combine(directory, "pending-prepare-unknown.bin");
    private sealed record PrepareUnknownMarker(int Version, Guid StageId, PairingSessionIdentity Identity, string InvitationDigest, string TokenDigest, PeerIdentityOffer? Offer = null, string? PendingCiphertextDigest = null)
    {
        public override string ToString() => "[isolated prepare unknown]";
    }

    private void RejectUnresolvedPrepareTest()
    {
        var path = PrepareMarkerPath; RejectLinks(directory); RejectLinks(path);
        if (activePrepareSession is not null || File.Exists(path) || Directory.Exists(path))
            throw new InvalidOperationException("An isolated PREPARE may have been sent; authenticated recovery is required.");
    }

    private async Task<object> RegisterPrepareTestAsync(PairingTlsConsentSession session, PendingGrantDocument pending, byte[] pendingCiphertext, LivePreparePayload payload, CancellationToken ct)
    {
        RejectUnresolvedPrepareTest();
        var frozen = pending.Frozen ?? throw new InvalidOperationException("Missing frozen invitation.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(new PrepareUnknownMarker(2, pending.StageId, pending.Identity, frozen.InvitationDigest, VaultTokenDigest(pending.Token), pending.Offer, Convert.ToHexString(SHA256.HashData(pendingCiphertext))), StrictVaultJson);
        byte[] encrypted;
        try { encrypted = protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length is <= 0 or > MaxFileBytes) throw new InvalidDataException("Invalid protected PREPARE marker.");
        var path = PrepareMarkerPath; var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            RejectLinks(directory); RejectLinks(path); RejectLinks(temp);
            await File.WriteAllBytesAsync(temp, encrypted, ct);
            ct.ThrowIfCancellationRequested(); RejectLinks(path);
            File.Move(temp, path, false);
            activePrepareSession = session; activePrepareDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            activePreparePermit = new object(); activePreparePayload = payload; activePrepareStarted = false;
            activePreparePendingHash = SHA256.HashData(pendingCiphertext); activePrepareMarkerHash = SHA256.HashData(encrypted);
            return activePreparePermit;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task ReleasePrepareTestAsync(PairingTlsConsentSession session)
    {
        BeforePrepareReleaseGateWait?.TrySetResult();
        await gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(activePrepareSession, session)) throw new InvalidOperationException("Isolated PREPARE lease does not match.");
            activePrepareSession = null; activePreparePermit = null; activePreparePayload = null; activePrepareStarted = false;
            activePreparePendingHash = null; activePrepareMarkerHash = null; activeCommittedSendPermit = null; activeCommittedSendKey = null;
            activePrepareDrained!.TrySetResult(); activePrepareDrained = null;
        }
        finally { gate.Release(); }
    }

    internal async Task ClaimPrepareTransmissionTestAsync(PairingTlsConsentSession session, object permit, LivePreparePayload payload, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed(); ct.ThrowIfCancellationRequested(); RejectLinks(PrepareMarkerPath);
            if (!ReferenceEquals(session, activePrepareSession) || !ReferenceEquals(permit, activePreparePermit) ||
                !ReferenceEquals(payload, activePreparePayload) || activePrepareStarted || !File.Exists(PrepareMarkerPath))
                throw new InvalidOperationException("Isolated PREPARE send is not registered.");
            var current = await ReadPendingGrantSnapshotAsync(ct);
            var marker = await ReadBoundedVaultFileAsync(PrepareMarkerPath, ct);
            if (current.Ciphertext is null || activePreparePendingHash is null || activePrepareMarkerHash is null ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(current.Ciphertext), activePreparePendingHash) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(marker), activePrepareMarkerHash))
                throw new InvalidOperationException("Isolated PREPARE files changed before sending.");
            ct.ThrowIfCancellationRequested();
            activePrepareStarted = true;
        }
        finally { gate.Release(); }
    }
    internal async Task<LivePeerStageMetadata> SendPrepareAndReceiveReadyTestAsync(PairingTlsConsentSession session, object capability,
        Guid stage, PairingSessionIdentity expected, PeerIdentityOffer offer, DateTimeOffset now, Func<DateTimeOffset> clock, CancellationToken ct = default)
        => (await SendPrepareCoreTestAsync(session, capability, stage, expected, offer, now, clock, false, false, false, ct)).Ready;

    internal async Task<string> SendPrepareReadyAndCommitTestAsync(PairingTlsConsentSession session, object capability,
        Guid stage, PairingSessionIdentity expected, PeerIdentityOffer offer, DateTimeOffset now, Func<DateTimeOffset> clock, CancellationToken ct = default)
        => (await SendPrepareCoreTestAsync(session, capability, stage, expected, offer, now, clock, true, false, false, ct)).Commit ?? throw new InvalidOperationException("Missing isolated commit result.");

    internal async Task<string> SendPrepareReadyCommitAndNotifyTestAsync(PairingTlsConsentSession session, object capability,
        Guid stage, PairingSessionIdentity expected, PeerIdentityOffer offer, DateTimeOffset now, Func<DateTimeOffset> clock, CancellationToken ct = default)
        => (await SendPrepareCoreTestAsync(session, capability, stage, expected, offer, now, clock, true, true, false, ct)).Commit ?? throw new InvalidOperationException("Missing isolated notification result.");

    internal async Task<string> SendPrepareReadyCommitNotifyAndObserveAppliedTestAsync(PairingTlsConsentSession session, object capability,
        Guid stage, PairingSessionIdentity expected, PeerIdentityOffer offer, DateTimeOffset now, Func<DateTimeOffset> clock, CancellationToken ct = default)
        => (await SendPrepareCoreTestAsync(session, capability, stage, expected, offer, now, clock, true, true, true, ct)).Commit ?? throw new InvalidOperationException("Missing isolated APPLIED observation.");

    private async Task<(LivePeerStageMetadata Ready, string? Commit)> SendPrepareCoreTestAsync(PairingTlsConsentSession session, object capability,
        Guid stage, PairingSessionIdentity expected, PeerIdentityOffer offer, DateTimeOffset now, Func<DateTimeOffset> clock, bool commitAfterReady, bool notifyCommitted, bool observeApplied, CancellationToken ct)
    {
        session.BeginLiveVaultTest(capability, expected, offer);
        var registered = false;
        try
        {
            using var deadline = session.CreateLiveVaultDeadline(ct); LivePreparePayload payload; object permit;
            await gate.WaitAsync(deadline.Token);
            try
            {
                ThrowIfDisposed(); session.CheckLiveVaultTest(capability, deadline.Token);
                var captured = await ReadPendingGrantSnapshotAsync(deadline.Token);
                var pending = captured.Document ?? throw new InvalidOperationException("Missing frozen pending grant.");
                var pendingCiphertext = captured.Ciphertext ?? throw new InvalidOperationException("Missing frozen pending ciphertext.");
                if (pending.StageId != stage || pending.Identity != expected || pending.Offer != offer || pending.Frozen is null || clock() < now)
                    throw new InvalidOperationException("Frozen pending grant does not match.");
                ValidatePendingBinding(pending.Identity, pending.Offer, clock()); ValidateFrozenInvitation(pending);
                var frozen = pending.Frozen;
                if (frozen.Host != "127.0.0.1") throw new InvalidOperationException("Only loopback test invitations are allowed.");
                payload = new(stage, pending.Slot, frozen.DataPortBase, frozen.InvitationDigest,
                    ToInvitation(pending, frozen.Host, frozen.ControlPort, frozen.KeyboardPort, frozen.DataPort));
                permit = await RegisterPrepareTestAsync(session, pending, pendingCiphertext, payload, deadline.Token); registered = true;
            }
            finally { gate.Release(); }
            if (BeforeLivePrepareSend is not null) await BeforeLivePrepareSend.PauseAsync();
            var ready = await session.ExchangePrepareLiveTestAsync(capability, this, permit, payload, deadline.Token, commitAfterReady);
            var result = commitAfterReady ? await CommitReadyTestAsync(session, capability, permit, payload, expected, offer, now, clock, deadline.Token) : null;
            if (notifyCommitted)
            {
                try { await VerifyCommittedAndSendTestAsync(session, capability, permit, payload, expected, offer, now, clock, deadline.Token); }
                catch { session.StopLivePrepareTest(); throw; }
                result = "committed-notified";
            }
            if (observeApplied)
            {
                try
                {
                    await session.ReceiveAppliedFrameTestAsync(capability, payload, deadline.Token);
                    await VerifyAppliedObservationTestAsync(session, capability, permit, payload, expected, offer, now, clock, deadline.Token);
                    result = "applied-observed";
                }
                catch { session.StopLivePrepareTest(); throw; }
            }
            return (ready, result);
        }
        finally
        {
            try { if (registered) await ReleasePrepareTestAsync(session); }
            finally { session.FinishLiveVaultTest(); }
        }
    }
}

internal sealed partial class PairingTlsConsentSession
{
    internal ControlledLivePublishBarrier? BeforeLiveReady { get; set; }
    internal NeverReleaseReadyBarrier? BeforeLiveReadyStop { get; set; }
    internal ControlledLivePublishBarrier? AfterLiveReadySent { get; set; }
    internal bool ThrowAfterLivePrepareStop { get; set; }
    internal void StopLivePrepareTest()
    {
        Abort();
        if (ThrowAfterLivePrepareStop) throw new IOException("Synthetic stop failure.");
    }
    private object? readyCommitTicket;
    private LivePreparePayload? readyCommitPayload;
    private const int MaximumPrepareFrameBytes = 8192;

    internal void ConsumeReadyCommitTest(object capability, LivePreparePayload payload, CancellationToken ct)
    {
        lock (gate)
        {
            RequireActive(); ct.ThrowIfCancellationRequested();
            if (role != "host" || !ReferenceEquals(capability, isolatedCapability) || !isolatedActionClaimed ||
                consent.Phase != PairingConsentPhase.ReadyToPrepare || readyCommitTicket is null || !ReferenceEquals(readyCommitPayload, payload))
                throw new InvalidOperationException("READY capability is not available for this session and payload.");
            readyCommitTicket = null; readyCommitPayload = null;
        }
    }
    private static readonly string[] PrepareFields = ["version", "type", "role", "sessionId", "transactionId", "offerNonce", "stageId", "senderChallenge", "receiverChallenge", "slot", "dataPortBase", "digest", "invitation"];
    private static readonly string[] ReadyFields = ["version", "type", "role", "sessionId", "transactionId", "offerNonce", "stageId", "senderChallenge", "receiverChallenge", "slot", "dataPortBase", "digest"];
    private static readonly string[] ProtocolInvitationFields = ["version", "host", "port", "keyboardPort", "dataPort", "hostName", "peerName", "certificateSha256", "token"];

    private static bool SamePrepareDigest(string? actual, string expected)
    {
        if (actual is not { Length: 64 } || expected.Length != 64 || actual.Any(c => !char.IsAsciiHexDigitUpper(c)) || expected.Any(c => !char.IsAsciiHexDigitUpper(c))) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected));
    }

    internal byte[] MakePrepareFrameForTest(Guid stage, MultiPeerInvitation candidate, int slot, int dataPortBase) =>
        JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type = "prepare", role, sessionId = identity.SessionId,
            transactionId = identity.TransactionId, offerNonce = identity.OfferNonce, stageId = stage,
            senderChallenge = challenge, receiverChallenge = remoteChallenge, slot, dataPortBase,
            digest = PeerInvitationStore.ComputePendingDigest(candidate), invitation = candidate }, JsonLineProtocol.Json);

    private void CheckProtocolHeader(JsonElement root, string type, string expectedRole, string[] fields)
    {
        if (!ExactFields(root, fields) || root.GetProperty("version").GetInt32() != 1 || root.GetProperty("type").GetString() != type || root.GetProperty("role").GetString() != expectedRole ||
            !root.GetProperty("sessionId").TryGetGuid(out var session) || session != identity.SessionId ||
            !root.GetProperty("transactionId").TryGetGuid(out var transaction) || transaction != identity.TransactionId ||
            !root.GetProperty("offerNonce").TryGetGuid(out var nonce) || nonce != identity.OfferNonce ||
            !TryChallenge(root.GetProperty("senderChallenge"), out var sender) || sender != remoteChallenge ||
            !TryChallenge(root.GetProperty("receiverChallenge"), out var receiver) || receiver != challenge)
            throw new InvalidDataException("Invalid isolated protocol header.");
    }

    internal async Task<LivePeerStageMetadata> ExchangePrepareLiveTestAsync(object capability, MultiDeviceCredentialVault vault, object permit, LivePreparePayload payload, CancellationToken ct, bool requireV3Ready = false)
    {
        try
        {
            await vault.ClaimPrepareTransmissionTestAsync(this, permit, payload, ct);
            CheckLiveVaultTest(capability, ct);
            await WritePrepareFrameAsync(new { version = 1, type = "prepare", role, sessionId = identity.SessionId,
                transactionId = identity.TransactionId, offerNonce = identity.OfferNonce, stageId = payload.StageId,
                senderChallenge = challenge, receiverChallenge = remoteChallenge, slot = payload.Slot, dataPortBase = payload.DataPortBase,
                digest = payload.Digest, invitation = payload.Invitation }, ct);
            using var document = await ReadPrepareFrameAsync(ct); var root = document.RootElement;
            CheckProtocolHeader(root, requireV3Ready ? "ready-v3" : "ready", "peer", ReadyFields);
            if (!root.GetProperty("stageId").TryGetGuid(out var stage) || stage != payload.StageId ||
                root.GetProperty("slot").GetInt32() != payload.Slot || root.GetProperty("dataPortBase").GetInt32() != payload.DataPortBase ||
                !SamePrepareDigest(root.GetProperty("digest").GetString(), payload.Digest)) throw new InvalidDataException("Ready does not match prepare.");
            CheckLiveVaultTest(capability, ct);
            if (requireV3Ready)
            {
                lock (gate)
                {
                    RequireActive(); ct.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(capability, isolatedCapability) || readyCommitTicket is not null)
                        throw new InvalidOperationException("READY capability cannot be issued.");
                    readyCommitTicket = new object(); readyCommitPayload = payload;
                }
            }
            return new(stage, payload.Slot, payload.DataPortBase, payload.Digest);
        }
        catch { Abort(); throw; }
    }

    internal async Task<LivePeerStageMetadata> ReceivePrepareAndStageTestAsync(PeerInvitationStore store, object capability, CancellationToken ct = default, bool writeV3 = false, bool readyV3 = false)
        => (await ReceivePrepareCoreTestAsync(store, capability, ct, writeV3, readyV3, false)).Ready;

    internal async Task<object> ReceivePrepareReadyAndCommittedTestAsync(PeerInvitationStore store, object capability, CancellationToken ct = default)
        => (await ReceivePrepareCoreTestAsync(store, capability, ct, true, true, true)).Ticket ?? throw new InvalidOperationException("Missing isolated promotion ticket.");

    private async Task<(LivePeerStageMetadata Ready, object? Ticket)> ReceivePrepareCoreTestAsync(PeerInvitationStore store, object capability, CancellationToken ct, bool writeV3, bool readyV3, bool receiveCommitted)
    {
        if (readyV3 && !writeV3) throw new InvalidOperationException("V3 READY requires V3 staging.");
        lock (gate)
        {
            RequireActive();
            if (role != "peer" || isolatedCapability is null || !ReferenceEquals(capability, isolatedCapability) || isolatedActionClaimed || consent.Phase != PairingConsentPhase.ReadyToPrepare)
                throw new InvalidOperationException("Live peer test capability is unavailable.");
            isolatedActionClaimed = true; peerPrepareReserved = true; activeOperations++;
        }
        try
        {
            using var deadline = CreateDeadline(ct);
            using var document = await ReadPrepareFrameAsync(deadline.Token); var root = document.RootElement;
            CheckProtocolHeader(root, "prepare", "host", PrepareFields);
            if (!root.GetProperty("stageId").TryGetGuid(out var stage) || stage == Guid.Empty || !ExactFields(root.GetProperty("invitation"), ProtocolInvitationFields))
                throw new InvalidDataException("Invalid prepare contents.");
            var slot = root.GetProperty("slot").GetInt32(); var dataPortBase = root.GetProperty("dataPortBase").GetInt32();
            var digest = root.GetProperty("digest").GetString();
            var candidate = root.GetProperty("invitation").Deserialize<MultiPeerInvitation>(JsonLineProtocol.Json) ?? throw new InvalidDataException("Missing invitation.");
            if (!SamePrepareDigest(digest, PeerInvitationStore.ComputePendingDigest(candidate))) throw new InvalidDataException("Prepare digest mismatch.");
            object ticket;
            lock (gate)
            {
                RequireActive(); deadline.Token.ThrowIfCancellationRequested();
                if (!peerPrepareReserved || peerPrepareTicket is not null) throw new InvalidOperationException("Isolated prepare reservation is unavailable.");
                peerPrepareTicket = ticket = new object(); peerPrepareCandidate = candidate; peerPrepareStage = stage;
                peerPrepareSlot = slot; peerPrepareDataPortBase = dataPortBase;
            }
            var publishedHash = await store.StageLivePeerTestAsync(this, capability, stage, identity, offer, candidate, slot, dataPortBase, deadline.Token, protocolTicket: ticket, writeV3: writeV3);
            var readySnapshot = receiveCommitted ? await CapturePeerStageForCommittedTestAsync(store, capability, publishedHash, stage, slot, dataPortBase, digest!, deadline.Token) : null;
            if (BeforeLiveReadyStop is not null) await BeforeLiveReadyStop.PauseUntilCancelledAsync(deadline.Token);
            if (BeforeLiveReady is not null) await BeforeLiveReady.PauseAsync();
            CheckLiveVaultTest(capability, deadline.Token);
            await WriteReadyFrameTestAsync(new { version = 1, type = readyV3 ? "ready-v3" : "ready", role, sessionId = identity.SessionId, transactionId = identity.TransactionId,
                offerNonce = identity.OfferNonce, stageId = stage, senderChallenge = challenge, receiverChallenge = remoteChallenge,
                slot, dataPortBase, digest }, deadline.Token);
            if (AfterLiveReadySent is not null) await AfterLiveReadySent.PauseAsync();
            var promotion = receiveCommitted ? await ReceiveCommittedAndIssueTicketTestAsync(store, capability, readySnapshot!, stage, slot, dataPortBase, digest!, deadline.Token) : null;
            return (new(stage, slot, dataPortBase, digest!), promotion);
        }
        catch { Abort(); throw; }
        finally { FinishOperation(); }
    }

    private async Task WritePrepareFrameAsync<T>(T frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonLineProtocol.Json);
        try
        {
            if (bytes.Length + 1 > MaximumPrepareFrameBytes) throw new InvalidDataException("Isolated frame exceeds limit.");
            await writeGate.WaitAsync(ct);
            try { await stream.WriteAsync(bytes, ct); await stream.WriteAsync(new byte[] { (byte)'\n' }, ct); await stream.FlushAsync(ct); }
            finally { writeGate.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<JsonDocument> ReadPrepareFrameAsync(CancellationToken ct)
    {
        var bytes = new byte[MaximumPrepareFrameBytes]; var count = 0; var one = new byte[1];
        try
        {
            await readGate.WaitAsync(ct);
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(one, ct);
                    if (read == 0) throw new EndOfStreamException("Isolated protocol connection closed.");
                    if (one[0] == (byte)'\n')
                    {
                        try { return JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes, 0, count), new JsonDocumentOptions { MaxDepth = 6 }); }
                        catch (Exception e) when (e is DecoderFallbackException or JsonException) { throw new InvalidDataException("Invalid isolated protocol frame.", e); }
                    }
                    if (count >= MaximumPrepareFrameBytes - 1) throw new InvalidDataException("Isolated protocol frame exceeds limit.");
                    bytes[count++] = one[0];
                }
            }
            finally { readGate.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(one); }
    }
}
