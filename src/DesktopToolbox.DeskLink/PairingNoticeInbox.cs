using System.Diagnostics;
using System.Net;

namespace DesktopToolbox.DeskLink;

/// <summary>A single unverified request; possession does not grant trust or expose the packet source.</summary>
public sealed class PairingIncomingRequest
{
    private readonly PairingNoticeInbox owner;
    internal PairingIncomingRequest(PairingNoticeInbox owner, Guid id, PairingDiscoveryNotice notice, DateTimeOffset expiresAtUtc)
    { this.owner = owner; Id = id; HostName = notice.HostName; HostFingerprint = notice.HostFingerprint; ExpiresAtUtc = expiresAtUtc; }
    public Guid Id { get; }
    public string HostName { get; }
    public string HostFingerprint { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    internal PairingIncomingClaim? TryClaim() => owner.TryClaim(this);
}

/// <summary>Internal one-use handoff; source is a route, not an authenticated identity.</summary>
internal sealed class PairingIncomingClaim
{
    private readonly byte[] source;
    private readonly PairingNoticeInbox owner;
    private readonly Func<CancellationToken, Task<PairingNetworkHandoff>>? acquire;
    private int attempted;
    internal PairingIncomingClaim(PairingNoticeInbox owner, IPAddress source, PairingDiscoveryNotice notice, PeerIdentityOffer offer,
        DiscoveryNetworkBinding binding, CancellationToken revocationToken, DateTimeOffset expiresAtUtc,
        Func<CancellationToken, Task<PairingNetworkHandoff>>? acquire)
    { this.owner = owner; this.source = source.GetAddressBytes(); Notice = notice; Offer = offer; Binding = binding; RevocationToken = revocationToken; ExpiresAtUtc = expiresAtUtc; this.acquire = acquire; }
    internal IPAddress SourceAddress => new(source);
    internal PairingDiscoveryNotice Notice { get; }
    internal PeerIdentityOffer Offer { get; }
    internal DiscoveryNetworkBinding Binding { get; }
    internal CancellationToken RevocationToken { get; }
    internal DateTimeOffset ExpiresAtUtc { get; }

    internal async Task<PairingNetworkHandoff> AcquireNetworkAsync(CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref attempted, 1) != 0 || acquire is null || !owner.IsHandoffCurrent(ExpiresAtUtc))
            throw new InvalidOperationException("Pairing network handoff is no longer available.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, RevocationToken);
        Task<PairingNetworkHandoff>? operation = null;
        PairingNetworkHandoff? result = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            operation = acquire(linked.Token);
            result = await operation.WaitAsync(linked.Token).ConfigureAwait(false);
            if (result is null || result.Binding.HasLiveNetworkOrigin != Binding.HasLiveNetworkOrigin ||
                result.Binding.InterfaceId != Binding.InterfaceId || result.Binding.InterfaceIndex != Binding.InterfaceIndex ||
                !result.Binding.Address.Equals(Binding.Address) || !result.Binding.SubnetMask.Equals(Binding.SubnetMask) ||
                !await result.RevalidateAsync(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false) ||
                !owner.IsHandoffCurrent(ExpiresAtUtc)) throw new InvalidOperationException("Pairing network changed during handoff.");
            linked.Token.ThrowIfCancellationRequested();
            var accepted = result; result = null;
            return accepted;
        }
        catch (Exception startup)
        {
            Exception? cleanup = null;
            if (result is not null)
            {
                try { await result.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanup = error; }
            }
            else if (operation is not null)
            {
                // Choose a path from one completion check. Once a Task is complete its final status
                // cannot change; a task that finishes immediately after this check is still drained
                // by DisposeLateAsync rather than falling between separate status checks.
                if (!operation.IsCompleted)
                {
                    _ = DisposeLateAsync(operation);
                    cleanup = new InvalidOperationException("Pairing network handoff has not finished.");
                }
                else if (operation.IsCompletedSuccessfully)
                {
                    var completed = await operation.ConfigureAwait(false);
                    try { if (completed is not null) await completed.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { cleanup = error; }
                }
                else if (operation.IsFaulted) _ = operation.Exception;
            }
            if (cleanup is not null) throw new PairingStartupCleanupException(startup, [cleanup]);
            throw;
        }
    }

    private static async Task DisposeLateAsync(Task<PairingNetworkHandoff> operation)
    {
        try { var late = await operation.ConfigureAwait(false); if (late is not null) await late.DisposeAsync().ConfigureAwait(false); }
        catch { /* The expired operation cannot publish a result. */ }
    }
}

/// <summary>Independent, bounded-session observation; never supplied by the UI.</summary>
internal abstract class PairingNetworkHandoff : IAsyncDisposable
{
    internal abstract DiscoveryNetworkBinding Binding { get; }
    internal abstract CancellationToken RevocationToken { get; }
    internal abstract Task<bool> RevalidateAsync(CancellationToken ct);
    public abstract ValueTask DisposeAsync();
}

/// <summary>Untrusted notices from one responder run; socket provenance is established by the caller.</summary>
internal sealed class PairingNoticeInbox : IDisposable
{
    private const int PerSecond = 8, MaximumSeen = 128;
    private readonly object gate = new();
    private readonly DiscoveryNetworkBinding binding;
    private readonly PeerIdentityOffer offer;
    private readonly DiscoverySubnet subnet;
    private readonly CancellationToken stopped;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<CancellationToken, Task<PairingNetworkHandoff>>? acquireNetwork;
    private readonly DateTimeOffset deadline;
    private readonly TimeSpan duration;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly HashSet<Guid> sessions = [], transactions = [];
    private readonly Queue<DateTimeOffset> arrivals = [];
    private DateTimeOffset last;
    private PairingIncomingRequest? pending;
    private PairingDiscoveryNotice? notice;
    private byte[]? source;
    private bool closed, claimed;

    internal PairingNoticeInbox(DiscoveryNetworkBinding binding, PeerIdentityOffer offer, TimeSpan duration,
        CancellationToken stopped, Func<DateTimeOffset>? clock = null,
        Func<CancellationToken, Task<PairingNetworkHandoff>>? acquireNetwork = null)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(offer);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(duration));
        this.binding = binding; this.offer = offer; this.duration = duration; this.stopped = stopped;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.acquireNetwork = acquireNetwork;
        last = this.clock(); offer.Validate(last);
        deadline = Min(last + duration, offer.ExpiresAtUtc);
        subnet = new DiscoverySubnet(binding.Address, binding.SubnetMask);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private bool Current(DateTimeOffset now)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (closed || stopped.IsCancellationRequested || now < last || now >= deadline || elapsed < TimeSpan.Zero || elapsed >= duration)
        { closed = true; pending = null; notice = null; source = null; return false; }
        last = now;
        if (pending is not null && now >= pending.ExpiresAtUtc) { pending = null; notice = null; source = null; }
        return true;
    }

    internal bool Observe(ReadOnlyMemory<byte> bytes, IPEndPoint from, IPAddress destination, int interfaceIndex)
    {
        ArgumentNullException.ThrowIfNull(from); ArgumentNullException.ThrowIfNull(destination);
        var address = new IPAddress(from.Address.GetAddressBytes());
        var target = new IPAddress(destination.GetAddressBytes());
        if (!binding.IsStillAvailable()) { Dispose(); return false; }
        lock (gate)
        {
            var now = clock();
            if (!Current(now)) return false;
            while (arrivals.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromSeconds(1)) arrivals.Dequeue();
            if (arrivals.Count >= PerSecond) return false;
            arrivals.Enqueue(now);
            if (claimed || pending is not null || sessions.Count >= MaximumSeen || transactions.Count >= MaximumSeen ||
                from.Port is <= 0 or > 65535 || interfaceIndex != binding.InterfaceIndex || !binding.Address.Equals(target) ||
                !subnet.Allows(address) || !PairingDiscoveryNotice.TryParse(bytes, now, out var parsed) || parsed is null ||
                !Guid.TryParseExact(offer.Nonce, "N", out var nonce) || parsed.OfferNonce != nonce ||
                !string.Equals(parsed.PeerName, offer.Name, StringComparison.Ordinal) || parsed.SessionExpiresAtUtc > offer.ExpiresAtUtc ||
                sessions.Contains(parsed.SessionId) || transactions.Contains(parsed.TransactionId)) return false;
            sessions.Add(parsed.SessionId); transactions.Add(parsed.TransactionId);
            notice = parsed; source = address.GetAddressBytes();
            pending = new PairingIncomingRequest(this, Guid.NewGuid(), parsed, Min(deadline, parsed.ExpiresAtUtc));
            return true;
        }
    }

    internal PairingIncomingRequest? GetPending()
    { lock (gate) return Current(clock()) ? pending : null; }

    internal bool Reject(Guid id)
    {
        lock (gate)
        {
            if (!Current(clock()) || pending is null || pending.Id != id) return false;
            pending = null; notice = null; source = null; return true;
        }
    }

    internal PairingIncomingClaim? TryClaim(PairingIncomingRequest request)
    {
        if (!binding.IsStillAvailable()) { Dispose(); return null; }
        lock (gate)
        {
            if (!Current(clock()) || !ReferenceEquals(pending, request) || notice is null || source is null) return null;
            var result = new PairingIncomingClaim(this, new IPAddress(source), notice, offer, binding, stopped, request.ExpiresAtUtc, acquireNetwork);
            claimed = true;
            pending = null; notice = null; source = null;
            return result;
        }
    }

    internal bool IsHandoffCurrent(DateTimeOffset expiresAtUtc)
    {
        if (!binding.IsStillAvailable()) { Dispose(); return false; }
        lock (gate) { var now = clock(); return Current(now) && now < expiresAtUtc; }
    }

    public void Dispose()
    { lock (gate) { closed = true; pending = null; notice = null; source = null; arrivals.Clear(); sessions.Clear(); transactions.Clear(); } }
}
