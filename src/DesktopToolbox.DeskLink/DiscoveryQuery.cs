using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

/// <summary>Untrusted, short-lived discovery query; it cannot grant trust.</summary>
internal sealed record DiscoveryQuery(Guid ScanNonce, DateTimeOffset ExpiresAtUtc)
{
    internal const int MaximumBytes = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Fields = ["version", "type", "scanNonce", "expiresAtUtc"];

    internal static byte[] Encode(Guid nonce, DateTimeOffset now, DateTimeOffset expires)
    {
        Validate(nonce, now, expires);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type = "query", scanNonce = nonce, expiresAtUtc = expires }, JsonLineProtocol.Json);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Discovery query is oversized.");
        return bytes;
    }

    internal static bool TryParse(ReadOnlyMemory<byte> bytes, DateTimeOffset now, out DiscoveryQuery? query)
    {
        query = null;
        if (bytes.Length is 0 or > MaximumBytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(StrictUtf8.GetString(bytes.Span), new JsonDocumentOptions { MaxDepth = 2 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
                if (!Fields.Contains(p.Name, StringComparer.Ordinal) || !found.Add(p.Name)) return false;
            if (found.Count != Fields.Length || root.GetProperty("version").GetInt32() != 1 || root.GetProperty("type").GetString() != "query" ||
                !root.GetProperty("scanNonce").TryGetGuid(out var nonce)) return false;
            var expires = root.GetProperty("expiresAtUtc").GetDateTimeOffset();
            Validate(nonce, now, expires);
            query = new(nonce, expires); return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException or DecoderFallbackException or OverflowException) { return false; }
    }
    private static void Validate(Guid nonce, DateTimeOffset now, DateTimeOffset expires)
    {
        if (nonce == Guid.Empty || expires <= now || expires > now.AddSeconds(30)) throw new ArgumentException("Invalid discovery query.");
    }
}

internal static class DiscoveryPacketPolicy
{
    internal static bool IsAllowed(IPAddress source, int port, IPAddress destination, int incomingInterface, IPAddress expectedDestination, int expectedInterface, DiscoverySubnet subnet)
    {
        return port is >= 1 and <= 65535 && expectedInterface > 0 && incomingInterface == expectedInterface &&
            destination.Equals(expectedDestination) && subnet.Allows(source);
    }
}
