using System.Net;
using System.Net.Sockets;

namespace DesktopToolbox.DeskLink;

/// <summary>A candidate and source address are unverified display data, not a trusted device or connection target.</summary>
public sealed record DiscoveryCandidate(PeerIdentityOffer Offer, IPAddress Source, DateTimeOffset ExpiresAtUtc);

/// <summary>Bounded in-memory results for one explicit scan; callers must enforce the selected network interface.</summary>
public sealed class DiscoveryCandidateCache
{
    public const int MaximumCandidates = 32;
    public const int MaximumPacketsPerSecond = 32;
    private const int MaximumSeenNonces = 128;
    private readonly object gate = new();
    private readonly Guid scanNonce;
    private readonly Func<DateTimeOffset> clock;
    private readonly bool loopbackTest;
    // Guid text is case-insensitive; changing hex case must not evade replay protection.
    private readonly Dictionary<string, DiscoveryCandidate> candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> recentPackets = new();
    private DateTimeOffset lastTime;
    private bool invalidated;

    public DiscoveryCandidateCache(Guid scanNonce, Func<DateTimeOffset>? clock = null) : this(scanNonce, clock, false) { }
    internal static DiscoveryCandidateCache ForLoopbackTest(Guid scanNonce, Func<DateTimeOffset>? clock = null) => new(scanNonce, clock, true);
    private DiscoveryCandidateCache(Guid scanNonce, Func<DateTimeOffset>? clock, bool loopbackTest)
    {
        this.loopbackTest = loopbackTest;
        if (scanNonce == Guid.Empty) throw new ArgumentException("A scan nonce is required.", nameof(scanNonce));
        this.scanNonce = scanNonce;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        lastTime = this.clock();
    }

    public bool Observe(ReadOnlyMemory<byte> message, IPAddress source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source = new IPAddress(source.GetAddressBytes());
        lock (gate)
        {
            var now = clock();
            if (!Refresh(now)) return false;
            while (recentPackets.TryPeek(out var seenAt) && now - seenAt >= TimeSpan.FromSeconds(1)) recentPackets.Dequeue();
            if (recentPackets.Count >= MaximumPacketsPerSecond) return false;
            recentPackets.Enqueue(now);
            if (loopbackTest ? !IPAddress.IsLoopback(source) : !IsPrivateAddress(source)) return false;
            if (!DiscoveryAnnouncement.TryParse(message, now, out var value) || value!.ScanNonce != scanNonce) return false;
            var nonce = value.Offer.Nonce;
            if (seen.ContainsKey(nonce) || seen.Count >= MaximumSeenNonces || candidates.Count >= MaximumCandidates) return false;
            seen.Add(nonce, value.Offer.ExpiresAtUtc);
            candidates.Add(nonce, new(value.Offer, new IPAddress(source.GetAddressBytes()), value.ExpiresAtUtc));
            return true;
        }
    }

    public IReadOnlyList<DiscoveryCandidate> GetCandidates()
    {
        lock (gate)
        {
            if (!Refresh(clock())) return [];
            return candidates.Values.Select(value => value with { Source = new IPAddress(value.Source.GetAddressBytes()) }).ToArray();
        }
    }

    // Clear visible results but retain nonce tombstones for this scan, so clearing is not a replay bypass.
    public void Clear() { lock (gate) candidates.Clear(); }

    // Permanently end this scan. Late packets cannot repopulate a stopped cache.
    public void Stop()
    {
        lock (gate) { invalidated = true; candidates.Clear(); seen.Clear(); recentPackets.Clear(); }
    }

    private bool Refresh(DateTimeOffset now)
    {
        if (invalidated) return false;
        if (now < lastTime) { candidates.Clear(); seen.Clear(); recentPackets.Clear(); invalidated = true; return false; }
        lastTime = now;
        foreach (var key in candidates.Where(x => x.Value.ExpiresAtUtc <= now).Select(x => x.Key).ToArray()) candidates.Remove(key);
        foreach (var key in seen.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) seen.Remove(key);
        return true;
    }

    private static bool IsPrivateAddress(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
    }
}
