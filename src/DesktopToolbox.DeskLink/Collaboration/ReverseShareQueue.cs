namespace DesktopToolbox.DeskLink.Collaboration;

/// <summary>In-memory prototype for one paired grant. It has no network or filesystem access.</summary>
public enum ReverseShareOperation { List, Preview, Download }

public sealed record ReverseShareRequest(Guid Id, Guid Authorization, ReverseShareOperation Operation,
    string? RootId, string RelativePath, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// A single bounded request per grant. Authorization is explicit and recreated after revocation.
/// Grant keys and authorization epochs are scope identifiers, not authentication credentials.
/// The caller must bind them to an independently authenticated channel and enforce the peer's local share permission.
/// Dispose at disconnect or stop to release pending requests and results. A future transport must check the complete encoded frame limit.
/// </summary>
public sealed class ReverseShareQueue : IDisposable
{
    public const int MaximumReplyBytes = 4 * 1024 * 1024;
    private readonly object gate = new();
    private readonly string grant;
    private readonly Func<DateTimeOffset> now;
    private Guid authorization;
    private ReverseShareRequest? pending;
    private bool delivered;
    private byte[]? reply;
    private bool disposed;

    public ReverseShareQueue(string grant, Func<DateTimeOffset>? now = null)
    {
        if (grant is not { Length: 64 } || grant.Any(c => !char.IsAsciiHexDigitUpper(c)))
            throw new ArgumentException("Invalid paired grant identity.", nameof(grant));
        this.grant = grant;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public Guid Authorize(string grantKey)
    {
        lock (gate)
        {
            ThrowIfDisposed(); RequireGrant(grantKey);
            // Reauthorization changes the epoch, including when already authorized.
            authorization = Guid.NewGuid(); Clear(); return authorization;
        }
    }

    public void Revoke(string grantKey)
    {
        lock (gate) { ThrowIfDisposed(); RequireGrant(grantKey); authorization = Guid.Empty; Clear(); }
    }

    public ReverseShareRequest Enqueue(string grantKey, Guid authorizationId, ReverseShareOperation operation,
        string? rootId, string relativePath, TimeSpan lifetime)
    {
        lock (gate)
        {
            ThrowIfDisposed(); RequireAuthorization(grantKey, authorizationId); Expire();
            if (pending is not null) throw new InvalidOperationException("A shared-folder request is already pending.");
            if (!Enum.IsDefined(operation) || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(5))
                throw new ArgumentException("Invalid request or lifetime.");
            if (rootId is not null && (rootId.Length is < 1 or > 64 || rootId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
                throw new ArgumentException("Invalid shared directory identity.");
            CollaborationPaths.ValidateRelative(relativePath, allowEmpty: true);
            if (rootId is null && (operation != ReverseShareOperation.List || relativePath.Length != 0) ||
                operation != ReverseShareOperation.List && relativePath.Length == 0)
                throw new ArgumentException("Choose an item in a shared directory.");
            var created = now();
            pending = new(Guid.NewGuid(), authorization, operation, rootId, relativePath, created, created.Add(lifetime));
            return pending;
        }
    }

    public ReverseShareRequest? Deliver(string grantKey, Guid authorizationId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!IsAuthorized(grantKey, authorizationId)) return null;
            Expire();
            if (pending is null || delivered) return null;
            delivered = true; return pending;
        }
    }

    public bool Complete(string grantKey, Guid authorizationId, Guid requestId, byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (gate)
        {
            ThrowIfDisposed();
            if (!IsAuthorized(grantKey, authorizationId)) return false;
            Expire();
            if (pending is null || pending.Id != requestId || !delivered || reply is not null || response.Length > MaximumReplyBytes) return false;
            reply = response.ToArray(); return true;
        }
    }

    public byte[]? TakeResult(string grantKey, Guid authorizationId, Guid requestId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!IsAuthorized(grantKey, authorizationId)) return null;
            Expire();
            if (pending is null || pending.Id != requestId || reply is null) return null;
            var result = reply; Clear(); return result;
        }
    }

    public bool Cancel(string grantKey, Guid authorizationId, Guid requestId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!IsAuthorized(grantKey, authorizationId)) return false;
            Expire();
            if (pending is null || pending.Id != requestId) return false;
            Clear(); return true;
        }
    }

    public void Dispose()
    { lock (gate) { if (disposed) return; disposed = true; authorization = Guid.Empty; Clear(); } }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private bool IsAuthorized(string grantKey, Guid authorizationId) =>
        string.Equals(grant, grantKey, StringComparison.Ordinal) && authorization != Guid.Empty && authorizationId == authorization;
    private void RequireGrant(string grantKey)
    { if (!string.Equals(grant, grantKey, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Device grant does not match."); }
    private void RequireAuthorization(string grantKey, Guid authorizationId)
    { if (!IsAuthorized(grantKey, authorizationId)) throw new UnauthorizedAccessException("Shared-folder access is not authorized."); }
    private void Expire()
    {
        if (pending is not { } request) return;
        var current = now();
        if (current < request.CreatedAtUtc || current >= request.ExpiresAtUtc) Clear();
    }
    private void Clear() { pending = null; delivered = false; reply = null; }
}


