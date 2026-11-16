using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

/// <summary>Non-secret receipt for one pending invitation. This is not an active pairing.</summary>
internal sealed record PairingPrepareReceipt(Guid StageId, int Slot, int DataPortBase, string InvitationDigest);

internal sealed partial class PairingTlsConsentSession
{
    private const int MaximumDeliveryFrameBytes = 8192;
    private static readonly string[] PrepareFields = ["version", "type", "role", "sessionId", "transactionId", "offerNonce", "stageId", "senderChallenge", "receiverChallenge", "slot", "dataPortBase", "digest", "invitation"];
    private static readonly string[] ReadyFields = ["version", "type", "role", "sessionId", "transactionId", "offerNonce", "stageId", "senderChallenge", "receiverChallenge", "slot", "dataPortBase", "digest"];
    private static readonly string[] ProtocolInvitationFields = ["version", "host", "port", "keyboardPort", "dataPort", "hostName", "peerName", "certificateSha256", "token"];

    internal sealed partial class DeliveryLease
    {
        private int prepareStarted;
        private PairingPrepareReceipt? verifiedReady;
        private bool readyClaimed;
        private PeerInvitationStore? preparedPeerStore;
        private PairingPrepareReceipt? preparedPeerReceipt;

        internal async Task<PairingPrepareReceipt> SendPrepareAndAwaitReadyAsync(CancellationToken ct = default)
        {
            EnsureCurrent(ct);
            if (!IsHost) throw new InvalidOperationException("Only the host can send a pending invitation.");
            var (vault, stage, frozen) = GetFrozenHost(ct);
            if (Interlocked.CompareExchange(ref prepareStarted, 1, 0) != 0)
                throw new InvalidOperationException("This session already attempted invitation delivery.");
            owner.BeginDeliveryOperation(this, ct);
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                var ready = await vault.SendPrepareAndAwaitReadyAsync(owner, this, stage, frozen, deadline.Token).ConfigureAwait(false);
                lock (owner.gate)
                {
                    owner.CheckDelivery(this, deadline.Token);
                    if (verifiedReady is not null) throw new InvalidOperationException("A pairing receipt already exists.");
                    verifiedReady = ready;
                }
                return ready;
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }

        internal PairingPrepareReceipt ClaimVerifiedReady(CancellationToken ct = default)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || readyClaimed || verifiedReady is null)
                    throw new InvalidOperationException("A verified pairing receipt is unavailable.");
                readyClaimed = true;
                return verifiedReady;
            }
        }

        internal async Task<PairingPrepareReceipt> ReceivePrepareAndStageAsync(PeerInvitationStore store, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(store);
            EnsureCurrent(ct);
            if (IsHost) throw new InvalidOperationException("Only the peer can receive a pending invitation.");
            if (Interlocked.CompareExchange(ref prepareStarted, 1, 0) != 0)
                throw new InvalidOperationException("This session already attempted invitation delivery.");
            owner.BeginDeliveryOperation(this, ct);
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                var receipt = await owner.ReceivePrepareCoreAsync(this, store, deadline.Token).ConfigureAwait(false);
                lock (owner.gate)
                {
                    owner.CheckDelivery(this, deadline.Token);
                    preparedPeerStore = store;
                    preparedPeerReceipt = receipt;
                }
                return receipt;
            }
            catch { owner.Abort(); throw; }
            finally { owner.FinishOperation(); }
        }
    }

    internal async Task<PairingPrepareReceipt> SendPrepareCoreAsync(DeliveryLease lease,
        MultiPeerInvitation invitation, PairingPrepareReceipt receipt, CancellationToken ct)
    {
        CheckDelivery(lease, ct);
        if (!lease.IsHost || invitation.Host != lease.LocalAddress.ToString())
            throw new InvalidOperationException("Pending invitation does not belong to the live connection.");
        var binding = new PairingHistoricalBinding(identity, receipt.StageId, receipt.Slot, receipt.DataPortBase,
            invitation.Host, invitation.Port, invitation.KeyboardPort, invitation.DataPort, receipt.InvitationDigest);
        await SendReserveAsync(lease, binding, ct).ConfigureAwait(false);
        CheckDelivery(lease, ct);
        await WriteDeliveryFrameAsync(new { version = 1, type = "prepare", role, sessionId = identity.SessionId,
            transactionId = identity.TransactionId, offerNonce = identity.OfferNonce, stageId = receipt.StageId,
            senderChallenge = challenge, receiverChallenge = remoteChallenge, slot = receipt.Slot,
            dataPortBase = receipt.DataPortBase, digest = receipt.InvitationDigest, invitation }, ct).ConfigureAwait(false);
        using var document = await ReadDeliveryFrameAsync(ct).ConfigureAwait(false);
        var root = document.RootElement;
        ValidateDeliveryHeader(root, "ready", "peer", ReadyFields);
        var ready = ReadDeliveryReceipt(root);
        if (ready != receipt) throw new InvalidDataException("Pairing receipt does not match the pending invitation.");
        CheckDelivery(lease, ct);
        return ready;
    }

    private async Task<PairingPrepareReceipt> ReceivePrepareCoreAsync(DeliveryLease lease, PeerInvitationStore store, CancellationToken ct)
    {
        var reservedBinding = await ReceiveReserveAsync(lease, store, ct).ConfigureAwait(false);
        using var document = await ReadDeliveryFrameAsync(ct).ConfigureAwait(false);
        var root = document.RootElement;
        ValidateDeliveryHeader(root, "prepare", "host", PrepareFields);
        var receipt = ReadDeliveryReceipt(root);
        if (!ExactFields(root.GetProperty("invitation"), ProtocolInvitationFields))
            throw new InvalidDataException("Pairing invitation fields are invalid.");
        var candidate = root.GetProperty("invitation").Deserialize<MultiPeerInvitation>(JsonLineProtocol.Json)
            ?? throw new InvalidDataException("Pairing invitation is missing.");
        if (candidate.Host != lease.RemoteAddress.ToString() || candidate.DataPort != receipt.DataPortBase + receipt.Slot - 1 ||
            candidate.Port == candidate.KeyboardPort || candidate.Port >= receipt.DataPortBase && candidate.Port < receipt.DataPortBase + 4 ||
            candidate.KeyboardPort >= receipt.DataPortBase && candidate.KeyboardPort < receipt.DataPortBase + 4 ||
            !SameDeliveryDigest(PeerInvitationStore.ComputePendingDigest(candidate), receipt.InvitationDigest))
            throw new InvalidDataException("Pairing invitation does not match this connection.");
        var receivedBinding = new PairingHistoricalBinding(identity, receipt.StageId, receipt.Slot, receipt.DataPortBase,
            candidate.Host, candidate.Port, candidate.KeyboardPort, candidate.DataPort, receipt.InvitationDigest);
        if (receivedBinding != reservedBinding) throw new InvalidDataException("Pairing invitation differs from its reserved capacity.");
        CheckDelivery(lease, ct);
        var pending = await store.StagePendingAsync(identity, candidate, identity.PeerName, identity.HostFingerprint,
            receipt.InvitationDigest, clock(), ct, reservationLease: lease, receipt: receipt).ConfigureAwait(false);
        CheckDelivery(lease, ct);
        if (pending.TransactionId != identity.TransactionId || pending.SessionId != identity.SessionId ||
            pending.OfferNonce != identity.OfferNonce || !SameDeliveryDigest(pending.InvitationDigest, receipt.InvitationDigest))
            throw new InvalidOperationException("Pending invitation changed before acknowledgement.");
        // Publish evidence before READY can leave this connection. If delivery fails,
        // the peer retains enough protected evidence for authenticated recovery.
        await store.RecordReadyEvidenceAsync(lease, receipt, ct).ConfigureAwait(false);
        CheckDelivery(lease, ct);
        await WriteDeliveryFrameAsync(new { version = 1, type = "ready", role, sessionId = identity.SessionId,
            transactionId = identity.TransactionId, offerNonce = identity.OfferNonce, stageId = receipt.StageId,
            senderChallenge = challenge, receiverChallenge = remoteChallenge, slot = receipt.Slot,
            dataPortBase = receipt.DataPortBase, digest = receipt.InvitationDigest }, ct).ConfigureAwait(false);
        CheckDelivery(lease, ct);
        return receipt;
    }

    private void ValidateDeliveryHeader(JsonElement root, string type, string expectedRole, string[] fields)
    {
        if (!ExactFields(root, fields) || root.GetProperty("version").GetInt32() != 1 ||
            root.GetProperty("type").GetString() != type || root.GetProperty("role").GetString() != expectedRole ||
            !root.GetProperty("sessionId").TryGetGuid(out var session) || session != identity.SessionId ||
            !root.GetProperty("transactionId").TryGetGuid(out var transaction) || transaction != identity.TransactionId ||
            !root.GetProperty("offerNonce").TryGetGuid(out var nonce) || nonce != identity.OfferNonce ||
            !TryChallenge(root.GetProperty("senderChallenge"), out var sender) || sender != remoteChallenge ||
            !TryChallenge(root.GetProperty("receiverChallenge"), out var receiver) || receiver != challenge)
            throw new InvalidDataException("Pairing message does not match this session.");
    }

    private static PairingPrepareReceipt ReadDeliveryReceipt(JsonElement root)
    {
        if (!root.GetProperty("stageId").TryGetGuid(out var stage) || stage == Guid.Empty)
            throw new InvalidDataException("Pairing stage is invalid.");
        var slot = root.GetProperty("slot").GetInt32();
        var port = root.GetProperty("dataPortBase").GetInt32();
        var digest = root.GetProperty("digest").GetString();
        if (slot is < 1 or > 4 || port is < 1 or > 65532 || digest is not { Length: 64 } ||
            digest.Any(c => !char.IsAsciiHexDigitUpper(c))) throw new InvalidDataException("Pairing receipt is invalid.");
        return new(stage, slot, port, digest);
    }

    private static bool SameDeliveryDigest(string first, string second)
    {
        if (first is not { Length: 64 } || second is not { Length: 64 } ||
            first.Any(c => !char.IsAsciiHexDigitUpper(c)) || second.Any(c => !char.IsAsciiHexDigitUpper(c))) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second));
    }

    private async Task WriteDeliveryFrameAsync<T>(T frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonLineProtocol.Json);
        try
        {
            if (bytes.Length + 1 > MaximumDeliveryFrameBytes) throw new InvalidDataException("Pairing frame is too large.");
            await writeGate.WaitAsync(ct).ConfigureAwait(false);
            try { await stream.WriteAsync(bytes, ct).ConfigureAwait(false); await stream.WriteAsync(new byte[] { (byte)'\n' }, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false); }
            finally { writeGate.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<JsonDocument> ReadDeliveryFrameAsync(CancellationToken ct)
    {
        var buffer = new byte[MaximumDeliveryFrameBytes]; var single = new byte[1]; var count = 0;
        try
        {
            await readGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    var length = await stream.ReadAsync(single, ct).ConfigureAwait(false);
                    if (length == 0) throw new EndOfStreamException("Pairing connection closed.");
                    if (single[0] == (byte)'\n')
                    {
                        try
                        {
                            _ = StrictUtf8.GetCharCount(buffer, 0, count);
                            using var input = new MemoryStream(buffer, 0, count, writable: false, publiclyVisible: false);
                            return JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 6 });
                        }
                        catch (Exception error) when (error is System.Text.DecoderFallbackException or JsonException)
                        { throw new InvalidDataException("Invalid pairing frame.", error); }
                    }
                    if (count >= MaximumDeliveryFrameBytes - 1) throw new InvalidDataException("Pairing frame is too large.");
                    buffer[count++] = single[0];
                }
            }
            finally { readGate.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); CryptographicOperations.ZeroMemory(single); }
    }
}
