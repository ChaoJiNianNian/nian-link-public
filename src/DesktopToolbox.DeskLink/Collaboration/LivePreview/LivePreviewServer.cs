using System.Security.Cryptography;
namespace DesktopToolbox.DeskLink.Collaboration.LivePreview;

/// <summary>Owns the paired endpoint's independent, bounded live-media slots.</summary>
public sealed class LivePreviewServer
{
    private readonly CollaborationServer owner;
    private readonly object gate = new();
    private readonly SemaphoreSlim authenticationGate = new(1, 1);
    private ILivePreviewCaptureProvider? provider;
    private DateTimeOffset? peerAdvertisedAt;
    private LivePreviewDemand? demand;
    private CancellationTokenSource? subscription;
    private CancellationTokenSource? upload;
    private byte[]? authenticatedToken;
    private long authenticationGeneration;
    internal LivePreviewServer(CollaborationServer owner) => this.owner = owner;
    public bool RemotePeerSupportsLivePreview { get { lock (gate) return peerAdvertisedAt is { } at && owner.LiveNow - at < TimeSpan.FromSeconds(4); } }
    public bool IsLocalCaptureActive { get { lock (gate) return subscription is not null; } }
    internal LivePreviewDemand? Demand { get { lock (gate) return demand; } }
    public event Action<Guid, LivePreviewCapturePacket>? PacketReceived;
    public event Action<Guid, string>? StreamClosed;
    internal void AdvertisePeer() { lock (gate) peerAdvertisedAt = owner.LiveNow; }
    // Serializes a fresh authoritative re-read with registration: a request
    // delayed after its first check cannot install a token already rotated away.
    internal async Task<long> ObserveAuthenticatedTokenAsync(byte[] token, CancellationToken ct)
    {
        await authenticationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] current;
            try { current = await owner.ReadLiveTokenAsync(ct).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { throw new UnauthorizedAccessException("Pairing is no longer available.", error); }
            if (!CryptographicOperations.FixedTimeEquals(current, token)) throw new UnauthorizedAccessException("Pairing was revoked.");
            CancellationTokenSource? local = null, remote = null;
            long generation;
            lock (gate)
            {
                if (authenticatedToken is null || !CryptographicOperations.FixedTimeEquals(authenticatedToken, token))
                {
                    demand = null; peerAdvertisedAt = null; local = subscription; remote = upload;
                    authenticatedToken = token.ToArray(); authenticationGeneration++;
                }
                generation = authenticationGeneration;
            }
            Cancel(local); Cancel(remote);
            return generation;
        }
        finally { authenticationGate.Release(); }
    }
    internal bool TryCommitAuthenticated(long generation, Action commit)
    {
        lock (gate)
        {
            if (authenticatedToken is null || authenticationGeneration != generation) return false;
            commit(); return true;
        }
    }
    public void SetLocalCaptureProvider(ILivePreviewCaptureProvider? next)
    {
        CancellationTokenSource? old;
        lock (gate) { if (ReferenceEquals(provider, next)) return; provider = next; old = subscription; }
        Cancel(old);
    }
    public LivePreviewLease RequestRemoteCapture(LivePreviewCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        lock (gate)
        {
            if (!RemotePeerSupportsLivePreview) throw new NotSupportedException("Update the other computer to use live preview.");
            if (demand is not null) throw new InvalidOperationException("Live preview is already in use.");
            demand = new(Guid.NewGuid(), options);
            return new(demand.StreamId, options, ReleaseAsync);
        }
    }
    private ValueTask ReleaseAsync(Guid id)
    {
        CancellationTokenSource? old = null;
        lock (gate) { if (demand?.StreamId == id) { demand = null; old = upload; } }
        Cancel(old);
        return ValueTask.CompletedTask;
    }
    internal void Reset()
    {
        CancellationTokenSource? local, remote;
        lock (gate) { demand = null; peerAdvertisedAt = null; authenticatedToken = null; authenticationGeneration++; local = subscription; remote = upload; }
        Cancel(local); Cancel(remote);
    }
    // A late watcher must not revoke a new pairing or another run of the same
    // pairing after this watcher's stream has already been stopped.
    internal void RevokeForToken(byte[] token, CancellationTokenSource? watcher = null)
    {
        CancellationTokenSource? local, remote;
        lock (gate)
        {
            if (authenticatedToken is null || !CryptographicOperations.FixedTimeEquals(authenticatedToken, token) ||
                watcher is not null && (watcher.IsCancellationRequested ||
                    !ReferenceEquals(watcher, subscription) && !ReferenceEquals(watcher, upload))) return;
            demand = null; peerAdvertisedAt = null; authenticatedToken = null; authenticationGeneration++;
            local = subscription; remote = upload;
        }
        Cancel(local); Cancel(remote);
    }
    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }
    internal async Task HandleAsync(CollaborationRequest request, Stream stream, byte[] token, long generation, CancellationToken ct)
    {
        LivePreviewDemand? requested = null;
        CancellationTokenSource? slot = null;
        ILivePreviewCaptureProvider? selected = null;
        var isUpload = request.Action == "preview-live-upload";
        try
        {
            requested = CollaborationProtocol.Parse<LivePreviewDemand>(request.Data);
            if (requested.StreamId == Guid.Empty || requested.Options is null) throw new ArgumentException("Invalid live stream request.");
            requested.Options.Validate();
            lock (gate)
            {
                if (authenticatedToken is null || authenticationGeneration != generation)
                    throw new UnauthorizedAccessException("Pairing was revoked.");
                if (isUpload)
                {
                    if (demand != requested) throw new UnauthorizedAccessException("This live preview request is no longer active.");
                    if (upload is not null) throw new InvalidOperationException("Live preview upload is already active.");
                    upload = slot = CancellationTokenSource.CreateLinkedTokenSource(ct);
                }
                else
                {
                    if (provider is not { IsSupported: true }) throw new NotSupportedException("Live capture is unavailable.");
                    if (subscription is not null) throw new InvalidOperationException("Live capture is already active.");
                    selected = provider;
                    subscription = slot = CancellationTokenSource.CreateLinkedTokenSource(ct);
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            var code = e is UnauthorizedAccessException ? "denied" : e is NotSupportedException ? "unsupported" : e is InvalidOperationException ? "busy" : "invalid";
            await CollaborationProtocol.WriteAsync(stream, new CollaborationResponse(1, request.Id, false, CollaborationProtocol.Element(false), code, e.Message), ct);
            return;
        }
        var id = requested.StreamId;
        string reason = "stopped";
        try
        {
            await CollaborationProtocol.WriteAsync(stream, new CollaborationResponse(1, request.Id, true, CollaborationProtocol.Element(true)), slot.Token);
            async Task WatchToken()
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), slot.Token);
                    byte[] current;
                    try { current = await owner.ReadLiveTokenAsync(slot.Token); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        RevokeForToken(token, slot);
                        throw new UnauthorizedAccessException("Pairing is no longer available.", error);
                    }
                    if (!CryptographicOperations.FixedTimeEquals(token, current)) { RevokeForToken(token, slot); throw new UnauthorizedAccessException("Pairing was revoked."); }
                }
            }
            var watch = WatchToken();
            var media = isUpload
                ? LivePreviewDuplex.ReceiveAsync(stream, id, requested.Options, packet => PacketReceived?.Invoke(id, packet), slot.Token)
                : LivePreviewDuplex.SendAsync(stream, id, requested.Options, selected!, slot.Token);
            try { await await Task.WhenAny(watch, media); }
            finally { slot.Cancel(); try { await Task.WhenAll(watch, media); } catch { } }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
        {
            reason = e is InvalidDataException or ArgumentException ? "protocol" : e is UnauthorizedAccessException ? "unauthorized" : e is OperationCanceledException ? "stopped" : "disconnected";
        }
        finally
        {
            lock (gate)
            {
                if (isUpload && ReferenceEquals(upload, slot)) upload = null;
                if (!isUpload && ReferenceEquals(subscription, slot)) subscription = null;
            }
            slot.Dispose();
            try { StreamClosed?.Invoke(id, reason); } catch { }
        }
    }
}
