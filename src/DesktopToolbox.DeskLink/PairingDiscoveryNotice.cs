using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

/// <summary>
/// An unauthenticated short-lived pairing request. It carries no credential and grants no trust.
/// The receiving transport must separately validate the actual source, destination, interface and active offer.
/// </summary>
public sealed record PairingDiscoveryNotice(string HostName, string PeerName, string HostFingerprint,
    Guid OfferNonce, Guid SessionId, Guid TransactionId, int Port, DateTimeOffset ExpiresAtUtc,
    DateTimeOffset SessionExpiresAtUtc)
{
    public const int MaximumBytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Fields = ["version", "type", "hostName", "peerName", "hostFingerprint",
        "offerNonce", "sessionId", "transactionId", "port", "expiresAtUtc", "sessionExpiresAtUtc"];

    public byte[] Encode(DateTimeOffset now)
    {
        var fingerprint = Validate(now);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, type = "pairing-request", hostName = HostName, peerName = PeerName,
            hostFingerprint = fingerprint, offerNonce = OfferNonce, sessionId = SessionId,
            transactionId = TransactionId, port = Port, expiresAtUtc = ExpiresAtUtc.ToUniversalTime(),
            sessionExpiresAtUtc = SessionExpiresAtUtc.ToUniversalTime()
        }, JsonLineProtocol.Json);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Pairing notice is oversized.");
        return bytes;
    }

    public static bool TryParse(ReadOnlyMemory<byte> bytes, DateTimeOffset now, out PairingDiscoveryNotice? notice)
    {
        notice = null;
        if (bytes.Length is 0 or > MaximumBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes.Span), new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!Fields.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name)) return false;
            if (found.Count != Fields.Length || root.GetProperty("version").GetInt32() != 1 ||
                root.GetProperty("type").GetString() != "pairing-request" ||
                !root.GetProperty("offerNonce").TryGetGuid(out var offerNonce) ||
                !root.GetProperty("sessionId").TryGetGuid(out var sessionId) ||
                !root.GetProperty("transactionId").TryGetGuid(out var transactionId)) return false;
            var candidate = new PairingDiscoveryNotice(root.GetProperty("hostName").GetString()!,
                root.GetProperty("peerName").GetString()!, root.GetProperty("hostFingerprint").GetString()!,
                offerNonce, sessionId, transactionId, root.GetProperty("port").GetInt32(),
                root.GetProperty("expiresAtUtc").GetDateTimeOffset().ToUniversalTime(),
                root.GetProperty("sessionExpiresAtUtc").GetDateTimeOffset().ToUniversalTime());
            var fingerprint = candidate.Validate(now);
            notice = candidate with { HostFingerprint = fingerprint };
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or DecoderFallbackException or OverflowException)
        { return false; }
    }

    private string Validate(DateTimeOffset now)
    {
        DeskflowProfile.ValidateName(HostName);
        DeskflowProfile.ValidateName(PeerName);
        if (string.Equals(HostName, PeerName, StringComparison.OrdinalIgnoreCase) ||
            OfferNonce == Guid.Empty || SessionId == Guid.Empty || TransactionId == Guid.Empty ||
            Port is < 1024 or > 65535 || ExpiresAtUtc <= now || ExpiresAtUtc > now.AddSeconds(30) ||
            SessionExpiresAtUtc < ExpiresAtUtc || SessionExpiresAtUtc > now.AddMinutes(2))
            throw new ArgumentException("Invalid or expired pairing notice.");
        return DeskLinkFingerprint.Normalize(HostFingerprint);
    }
}
