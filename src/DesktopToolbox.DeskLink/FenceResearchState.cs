#if NIAN_FENCE_RESEARCH
using System.Security.Cryptography;

namespace DesktopToolbox.DeskLink;

// Loopback-only, no-actions research state. Its sealed response proves only that
// this object recorded a tombstone; it is not a native or durable return proof.
internal sealed class FenceResearchState(int capacity) : IDisposable
{
    private sealed record Binding(byte[] Marker, string Target);
    private readonly object gate = new();
    private readonly Dictionary<Guid, Binding> sealedOperations = [];
    private bool disposed;
    internal string Incarnation { get; } = Guid.NewGuid().ToString("N");

    internal string Apply(string action, TrustedDeviceIdentity identity, byte[] token, string target, string? operation, string? incarnation)
    {
        if (action == "probe")
        {
            lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return "unavailable"; }
        }
        if (!Guid.TryParseExact(operation, "N", out var id) || id == Guid.Empty ||
            !string.Equals(incarnation, Incarnation, StringComparison.Ordinal)) return "rejected";
        var marker = ResearchGrantMarker.Derive(token, identity);
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (sealedOperations.TryGetValue(id, out var existing))
                {
                    if (!CryptographicOperations.FixedTimeEquals(existing.Marker, marker) ||
                        !string.Equals(existing.Target, target, StringComparison.Ordinal)) return "rejected";
                    return action == "seal" ? "sealed" : "rejected";
                }
                // The research endpoint cannot begin any keyboard action.
                if (action == "begin") return "unavailable";
                if (action != "seal" || sealedOperations.Count >= capacity) return "unconfirmed";
                sealedOperations.Add(id, new Binding(marker.ToArray(), target));
                return "sealed";
            }
        }
        finally { CryptographicOperations.ZeroMemory(marker); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var value in sealedOperations.Values) CryptographicOperations.ZeroMemory(value.Marker);
            sealedOperations.Clear();
        }
    }
}
#endif
