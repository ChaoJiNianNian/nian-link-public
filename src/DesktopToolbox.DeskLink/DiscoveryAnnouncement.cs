using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

/// <summary>An untrusted, short-lived discovery response; never evidence of pairing or identity.</summary>
public sealed record DiscoveryAnnouncement(Guid ScanNonce, PeerIdentityOffer Offer, DateTimeOffset ExpiresAtUtc)
{
    public const int MaximumBytes = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Fields = ["version", "type", "scanNonce", "offer", "expiresAtUtc"];
    private static readonly string[] OfferFields = ["version", "name", "platform", "keyboardFingerprint", "nonce", "expiresAtUtc"];

    public static byte[] Encode(PeerIdentityOffer offer, Guid scanNonce, DateTimeOffset now, DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(offer);
        offer.Validate(now);
        Validate(scanNonce, offer, expiresAtUtc, now);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type = "offer", scanNonce, offer, expiresAtUtc }, JsonLineProtocol.Json);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Discovery announcement is oversized.");
        return bytes;
    }

    public static bool TryParse(ReadOnlyMemory<byte> bytes, DateTimeOffset now, out DiscoveryAnnouncement? announcement)
    {
        announcement = null;
        if (bytes.Length is 0 or > MaximumBytes) return false;
        try
        {
            var text = StrictUtf8.GetString(bytes.Span);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!HasExactFields(root, Fields) || !HasExactFields(root.GetProperty("offer"), OfferFields)) return false;
            if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("type").GetString() != "offer" ||
                !root.GetProperty("scanNonce").TryGetGuid(out var scanNonce)) return false;
            var offer = PeerIdentityOffer.Parse(root.GetProperty("offer").GetRawText(), now);
            var expires = root.GetProperty("expiresAtUtc").GetDateTimeOffset();
            Validate(scanNonce, offer, expires, now);
            announcement = new(scanNonce, offer, expires);
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or DecoderFallbackException or OverflowException)
        { return false; }
    }

    private static void Validate(Guid scanNonce, PeerIdentityOffer offer, DateTimeOffset expires, DateTimeOffset now)
    {
        if (scanNonce == Guid.Empty || expires <= now || expires > now.AddSeconds(30) || expires > offer.ExpiresAtUtc)
            throw new ArgumentException("Invalid or expired discovery response.");
    }

    private static bool HasExactFields(JsonElement element, IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Contains(property.Name) || !found.Add(property.Name)) return false;
        return found.Count == expected.Count;
    }
}
