using System.Text;
using Nian.Input.Control;
namespace DesktopToolbox.DeskLink.Collaboration;

/// <summary>Memory-only current-value mailbox. Opt-out invalidates both old values.</summary>
public sealed class CollaborationClipboardExchange
{
    public const int MaxTextUtf8Bytes = 256 * 1024;
    private readonly object gate = new();
    private bool localEnabled, remoteEnabled;
    private ClipboardPayload? local, remote;
    public bool LocalEnabled { get { lock (gate) return localEnabled; } }
    public bool RemoteEnabled { get { lock (gate) return remoteEnabled; } }
    public void SetLocalEnabled(bool enabled) { lock (gate) { if (localEnabled != enabled) { local = remote = null; localEnabled = enabled; } } }
    public void SetRemoteEnabled(bool enabled) { lock (gate) { if (remoteEnabled != enabled) { local = remote = null; remoteEnabled = enabled; } } }
    public void ClearCurrent() { lock (gate) { local = remote = null; } }
    public void ClearSharedCurrent() { lock (gate) { RequireEnabled(); local = remote = null; } }
    public void SetLocal(ClipboardPayload value) { Validate(value); lock (gate) { RequireEnabled(); local = Copy(value); remote = null; } }
    public void SetRemote(ClipboardPayload value)
    {
        Validate(value);
        lock (gate)
        {
            RequireEnabled();
            // Old peers may still send images: acknowledge but never retain them.
            // Keep any newer locally-published text; only the stale remote value expires.
            if (value.PngBytes is not null) { remote = null; return; }
            remote = Copy(value); local = null;
        }
    }
    public ClipboardPayload? GetLocal() { lock (gate) return localEnabled && remoteEnabled && local is not null ? Copy(local) : null; }
    public ClipboardPayload? GetRemote() { lock (gate) return localEnabled && remoteEnabled && remote is not null ? Copy(remote) : null; }
    private void RequireEnabled() { if (!localEnabled || !remoteEnabled) throw new InvalidOperationException("Clipboard sharing is paused on one of the computers."); }
    private static ClipboardPayload Copy(ClipboardPayload value) => value with { PngBytes = value.PngBytes?.ToArray() };
    public static void Validate(ClipboardPayload value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Text is not null && Encoding.UTF8.GetByteCount(value.Text) > MaxTextUtf8Bytes)
            throw new ArgumentException("文字超过 256 KB，无法同步；请分段复制。");
        if (value.Id is null || value.Origin is null || value.Id.Length != 32 || !value.Id.All(Uri.IsHexDigit) || value.Origin.Length is < 1 or > 128 ||
            (value.Text is null) == (value.PngBytes is null) ||
            value.PngBytes is not null && (value.PngBytes.Length > 4 * 1024 * 1024 || !CollaborationImageBounds.IsBoundedPng(value.PngBytes)))
            throw new ArgumentException("Invalid or oversized current clipboard value.");
    }
}

internal sealed class CollaborationPreviewExchange(TimeProvider? clock = null)
{
    private readonly object gate = new();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private DateTimeOffset requestedUntil, remoteRequestedUntil, localReceived, remoteReceived;
    private PreviewFrame local = PreviewFrame.Off(), remote = PreviewFrame.Off();
    private Guid? requestedId, remoteRequestedId;
    private long localCaptureSequence, remoteCaptureSequence;
    private PreviewSourceStatus? localSource, remoteSource;
    private readonly HashSet<Guid> retiredLocalSources = [], retiredRemoteSources = [];
    private readonly HashSet<Guid> usedRequestedIds = [], usedRemoteRequestedIds = [];
    private const int MaximumRequestGenerations = 65536;
    public int LocalCaptureMaxWidth { get; private set; } = 960;
    public int LocalCaptureMaxHeight { get; private set; } = 540;
    public int RemoteCaptureMaxWidth { get; private set; } = 960;
    public int RemoteCaptureMaxHeight { get; private set; } = 540;
    public int LocalCaptureRefreshSeconds { get; private set; } = PreviewCaptureCadence.DefaultRefreshSeconds;
    public int RemoteCaptureRefreshSeconds { get; private set; } = PreviewCaptureCadence.DefaultRefreshSeconds;
    public bool LocalCaptureRequested { get { lock (gate) { if (clock.GetUtcNow() < remoteRequestedUntil) return true; local = PreviewFrame.Off(); return false; } } }
    public bool RemoteCaptureRequested { get { lock (gate) { if (clock.GetUtcNow() < requestedUntil) return true; remote = PreviewFrame.Off(); return false; } } }
    public Guid? LocalCaptureRequestId { get { lock (gate) return LocalCaptureRequested ? remoteRequestedId : null; } }
    public Guid? RemoteCaptureRequestId { get { lock (gate) return RemoteCaptureRequested ? requestedId : null; } }
    public PreviewSourceStatus? LocalPreviewSourceStatus { get { lock (gate) return localSource; } }
    public PreviewSourceStatus? RemotePreviewSourceStatus { get { lock (gate) return remoteSource; } }
    public void SetLocalSource(PreviewSourceStatus status)
    {
        ValidateSource(status);
        lock (gate)
        {
            var previous = localSource;
            if (AcceptSource(ref localSource, retiredLocalSources, status))
            {
                if (previous?.InstanceId != status.InstanceId) localCaptureSequence = 0;
                local = PreviewFrame.Off(); localReceived = DateTimeOffset.MinValue;
            }
        }
    }
    public void SetRemoteSource(PreviewSourceStatus status)
    {
        ValidateSource(status);
        lock (gate)
        {
            var previous = remoteSource;
            if (AcceptSource(ref remoteSource, retiredRemoteSources, status))
            {
                if (previous?.InstanceId != status.InstanceId) remoteCaptureSequence = 0;
                remote = PreviewFrame.Off(); remoteReceived = DateTimeOffset.MinValue;
            }
        }
    }
    public void SetRequested(bool enabled, int maxWidth = 960, int maxHeight = 540, int previewRefreshSeconds = PreviewCaptureCadence.DefaultRefreshSeconds, Guid? requestId = null)
    {
        ValidateSize(maxWidth, maxHeight); PreviewCaptureCadence.ValidateRefreshSeconds(previewRefreshSeconds);
        if (requestId == Guid.Empty) throw new ArgumentException("Invalid passive preview generation.");
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (!enabled && requestId is not null && requestId != requestedId) return;
            if (enabled) AcceptRequestId(requestId, requestedId, requestedUntil, now, usedRequestedIds);
            if (!enabled || now >= requestedUntil || requestedId != requestId) { remote = PreviewFrame.Off(); remoteReceived = DateTimeOffset.MinValue; remoteCaptureSequence = 0; }
            requestedId = enabled ? requestId : null;
            RemoteCaptureMaxWidth = maxWidth; RemoteCaptureMaxHeight = maxHeight; RemoteCaptureRefreshSeconds = previewRefreshSeconds;
            requestedUntil = enabled ? now.AddSeconds(5) : DateTimeOffset.MinValue;
        }
    }
    public void SetRemoteRequested(bool enabled, int maxWidth = 960, int maxHeight = 540, int previewRefreshSeconds = PreviewCaptureCadence.DefaultRefreshSeconds, Guid? requestId = null)
    {
        ValidateSize(maxWidth, maxHeight); PreviewCaptureCadence.ValidateRefreshSeconds(previewRefreshSeconds);
        if (requestId == Guid.Empty) throw new ArgumentException("Invalid passive preview generation.");
        lock (gate)
        {
            var now = clock.GetUtcNow();
            // An old or legacy viewer cannot close a newer identified request.
            if (!enabled && requestId != remoteRequestedId) return;
            if (enabled) AcceptRequestId(requestId, remoteRequestedId, remoteRequestedUntil, now, usedRemoteRequestedIds);
            if (!enabled || now >= remoteRequestedUntil || remoteRequestedId != requestId) { local = PreviewFrame.Off(); localReceived = DateTimeOffset.MinValue; localCaptureSequence = 0; }
            remoteRequestedId = enabled ? requestId : null;
            LocalCaptureMaxWidth = maxWidth; LocalCaptureMaxHeight = maxHeight; LocalCaptureRefreshSeconds = previewRefreshSeconds;
            remoteRequestedUntil = enabled ? now.AddSeconds(5) : DateTimeOffset.MinValue;
        }
    }
    public void SetLocal(PreviewFrame value)
    {
        Validate(value);
        lock (gate)
        {
            if (!LocalCaptureRequested) { local = PreviewFrame.Off(); return; }
            if (!Matches(value, remoteRequestedId, localSource)) return;
            if (remoteRequestedId is not null && value.CaptureSequence <= localCaptureSequence) return;
            if (remoteRequestedId is not null) localCaptureSequence = value.CaptureSequence!.Value;
            if (!SameSnapshot(local, value)) localReceived = clock.GetUtcNow();
            local = Copy(value);
        }
    }
    public void SetRemote(PreviewFrame value)
    {
        Validate(value);
        lock (gate)
        {
            if (!RemoteCaptureRequested) { remote = PreviewFrame.Off(); return; }
            if (!Matches(value, requestedId, remoteSource)) return;
            if (requestedId is not null && value.CaptureSequence <= remoteCaptureSequence) return;
            if (requestedId is not null) remoteCaptureSequence = value.CaptureSequence!.Value;
            if (!SameSnapshot(remote, value)) remoteReceived = clock.GetUtcNow();
            remote = Copy(value);
        }
    }
    public PreviewFrame GetLocal()
    {
        lock (gate) return !LocalCaptureRequested ? PreviewFrame.Off() : IsExpired(localReceived, LocalCaptureRefreshSeconds) ? PreviewFrame.Off("waiting") : Copy(local);
    }
    public PreviewFrame GetRemote()
    {
        lock (gate) return !RemoteCaptureRequested ? PreviewFrame.Off() : IsExpired(remoteReceived, RemoteCaptureRefreshSeconds) ? PreviewFrame.Off("waiting") : Copy(remote);
    }
    public void ClearFrames() { lock (gate) { local = remote = PreviewFrame.Off(); localReceived = remoteReceived = DateTimeOffset.MinValue; } }
    // Pairing rotation changes the peer, not the local source. Do not carry
    // the previous peer's leases, state or snapshots into the new pairing.
    public void InvalidatePeer()
    {
        lock (gate)
        {
            local = remote = PreviewFrame.Off();
            localReceived = remoteReceived = requestedUntil = remoteRequestedUntil = DateTimeOffset.MinValue;
            requestedId = remoteRequestedId = null;
            localCaptureSequence = remoteCaptureSequence = 0;
            usedRequestedIds.Clear(); usedRemoteRequestedIds.Clear();
            remoteSource = null; retiredRemoteSources.Clear();
        }
    }
    private bool IsExpired(DateTimeOffset received, int seconds) => clock.GetUtcNow() - received > TimeSpan.FromSeconds(seconds + 5);
    private static bool SameSnapshot(PreviewFrame first, PreviewFrame second) => first.State == second.State && first.CaptureSequence == second.CaptureSequence && first.CapturedAtUtc == second.CapturedAtUtc && first.Width == second.Width && first.Height == second.Height && first.JpegBytes.AsSpan().SequenceEqual(second.JpegBytes);
    private static void AcceptRequestId(Guid? next, Guid? current, DateTimeOffset until, DateTimeOffset now, HashSet<Guid> used)
    {
        if (next is not { } id)
        {
            // A delayed old peer must not replace an active identified request.
            // Legacy viewing can still start after that request is closed/expired.
            if (current is not null && now < until)
                throw new InvalidOperationException("An identified preview request is already active.");
            return;
        }
        if (id == current && now < until) return; // Ordinary lease renewal.
        if (used.Contains(id)) throw new InvalidOperationException("A closed or expired preview request needs a new generation.");
        if (used.Count >= MaximumRequestGenerations) throw new InvalidOperationException("Preview request limit reached. Reconnect the paired service.");
        used.Add(id);
    }
    private static bool Matches(PreviewFrame frame, Guid? requestId, PreviewSourceStatus? source)
    {
        if (frame.RequestId != requestId) return false;
        // New-generation frames require a source snapshot taken before capture;
        // legacy unbound frames remain viewable only for a legacy request.
        if (requestId is not null && (source is null || frame.SourceStatus != source || frame.CaptureSequence is null)) return false;
        if (source is not null && (source.Availability != "enabled" || frame.SourceStatus is not null && frame.SourceStatus != source)) return false;
        return true;
    }
    private static void ValidateSource(PreviewSourceStatus status)
    {
        if (status is null || status.InstanceId == Guid.Empty || status.Revision < 1 ||
            status.Availability is not ("unknown" or "enabled" or "disabled" or "locked" or "unavailable"))
            throw new ArgumentException("Invalid preview source status.");
    }
    private static bool AcceptSource(ref PreviewSourceStatus? current, HashSet<Guid> retired, PreviewSourceStatus next)
    {
        if (current is null) { current = next; return true; }
        if (current.InstanceId == next.InstanceId)
        {
            if (next.Revision <= current.Revision) return false;
            current = next; return true;
        }
        // Retired instances cannot be resurrected by a delayed authenticated
        // request. Bound memory; at capacity fail closed until endpoint restart.
        if (retired.Contains(next.InstanceId) || retired.Count >= 128) return false;
        retired.Add(current.InstanceId); current = next; return true;
    }
    private static void ValidateSize(int width, int height) { if (width is < 160 or > 1920 || height is < 90 or > 1080) throw new ArgumentException("Invalid preview size hint."); }
    private static PreviewFrame Copy(PreviewFrame value) => value.State == "live" ? value with { JpegBytes = value.JpegBytes.ToArray() } : value with { JpegBytes = [], Width = 0, Height = 0, Geometry = null, ControlProof = null };
    private static void Validate(PreviewFrame value)
    {
        if (value is null || value.State is null || value.JpegBytes is null || value.State.Length is < 1 or > 32 || value.Error?.Length > 512 || value.JpegBytes.Length > 1024 * 1024 ||
            value.State == "live" && (value.Width is < 1 or > 1920 || value.Height is < 1 or > 1080 || value.ContentType != "image/jpeg" ||
            !CollaborationImageBounds.IsBoundedJpeg(value.JpegBytes, value.Width, value.Height))) throw new ArgumentException("Invalid bounded preview frame.");
        if (value.RequestId == Guid.Empty) throw new ArgumentException("Invalid passive preview generation.");
        if (value.CaptureSequence is <= 0) throw new ArgumentException("Invalid passive capture sequence.");
        if (value.SourceStatus is not null) ValidateSource(value.SourceStatus);
        if (value.State != "live") return;
        if (value.Geometry is { } geometry && (!geometry.IsValid || geometry.ImageWidth != value.Width || geometry.ImageHeight != value.Height))
            throw new ArgumentException("The native preview geometry does not match the encoded frame.");
        if (value.ControlProof is { } proof && (value.Geometry is null || proof.Geometry != value.Geometry ||
            !PreviewFrameAuthority.ContentMatches(proof, value.JpegBytes, value.Width, value.Height)))
            throw new ArgumentException("The preview control proof does not match this frame.");
    }
}
