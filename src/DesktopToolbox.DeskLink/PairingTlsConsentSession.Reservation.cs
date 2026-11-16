using System.Text.Json;

namespace DesktopToolbox.DeskLink;

internal sealed partial class PairingTlsConsentSession
{
    internal sealed partial class DeliveryLease
    {
        private bool peerReserveAccepted;

        internal bool RequiresPeerReservation(CancellationToken ct)
        {
            lock (owner.gate) { owner.CheckDelivery(this, ct); return peerReserveAccepted; }
        }

        internal void MarkPeerReservationAccepted(CancellationToken ct)
        {
            lock (owner.gate) { owner.CheckDelivery(this, ct); peerReserveAccepted = true; }
        }
    }
    private static readonly string[] ReserveFields = ["version", "type", "role", "sessionId", "transactionId", "offerNonce", "senderChallenge", "receiverChallenge", "commitment", "binding"];
    private static readonly string[] ReserveReplyFields = ["version", "type", "role", "sessionId", "transactionId", "offerNonce", "senderChallenge", "receiverChallenge", "commitment", "decision"];

    private async Task SendReserveAsync(DeliveryLease lease, PairingHistoricalBinding binding, CancellationToken ct)
    {
        PairingHistoricalRecoveryRules.ValidateBinding(binding);
        if (!lease.IsHost || lease.Identity != binding.Identity || binding.Host != lease.LocalAddress.ToString())
            throw new InvalidOperationException("The capacity request does not match this connection.");
        var commitment = binding.Commitment();
        await WriteDeliveryFrameAsync(new { version = 1, type = "reserve", role, sessionId = identity.SessionId,
            transactionId = identity.TransactionId, offerNonce = identity.OfferNonce, senderChallenge = challenge,
            receiverChallenge = remoteChallenge, commitment, binding }, ct).ConfigureAwait(false);
        using var reply = await ReadDeliveryFrameAsync(ct).ConfigureAwait(false);
        ValidateDeliveryHeader(reply.RootElement, "reserve-reply", "peer", ReserveReplyFields);
        if (!SameDeliveryDigest(reply.RootElement.GetProperty("commitment").GetString() ?? "", commitment) ||
            reply.RootElement.GetProperty("decision").GetString() is not ("reserved" or "refused"))
            throw new InvalidDataException("The capacity reply differs from the request.");
        if (reply.RootElement.GetProperty("decision").GetString() != "reserved")
            throw new InvalidOperationException("对方配对记录已满，本次配对需要安全终止。");
        CheckDelivery(lease, ct);
    }

    private async Task<PairingHistoricalBinding> ReceiveReserveAsync(DeliveryLease lease, PeerInvitationStore store, CancellationToken ct)
    {
        using var request = await ReadDeliveryFrameAsync(ct).ConfigureAwait(false);
        ValidateDeliveryHeader(request.RootElement, "reserve", "host", ReserveFields);
        var json = request.RootElement.GetProperty("binding");
        if (!ExactFields(json, PeerInvitationStore.HistoricalBindingFields) || !ExactFields(json.GetProperty("identity"), HistoricalIdentityFields))
            throw new InvalidDataException("Reservation binding fields are invalid.");
        var binding = json.Deserialize<PairingHistoricalBinding>(JsonLineProtocol.Json)
            ?? throw new InvalidDataException("Reservation binding is empty.");
        PairingHistoricalRecoveryRules.ValidateBinding(binding);
        var commitment = request.RootElement.GetProperty("commitment").GetString() ?? "";
        if (!SameDeliveryDigest(binding.Commitment(), commitment)) throw new InvalidDataException("Reservation commitment is invalid.");
        var reserved = await store.ReserveCapacityAsync(lease, binding, ct).ConfigureAwait(false);
        CheckDelivery(lease, ct);
        await WriteDeliveryFrameAsync(new { version = 1, type = "reserve-reply", role, sessionId = identity.SessionId,
            transactionId = identity.TransactionId, offerNonce = identity.OfferNonce, senderChallenge = challenge,
            receiverChallenge = remoteChallenge, commitment, decision = reserved ? "reserved" : "refused" }, ct).ConfigureAwait(false);
        CheckDelivery(lease, ct);
        if (!reserved) throw new InvalidOperationException("本机配对记录已满，本次配对需要安全终止。");
        lease.MarkPeerReservationAccepted(ct);
        return binding;
    }
}
