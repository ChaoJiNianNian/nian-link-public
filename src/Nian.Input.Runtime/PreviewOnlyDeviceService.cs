using Nian.Input.Control;
using Nian.Input.Security;

namespace Nian.Input.Runtime;

/// <summary>Independent, explicitly approved preview gestures. It never owns a physical keyboard or display switch.</summary>
public sealed partial class PreviewOnlyDeviceService : INativePreviewControl, IAsyncDisposable
{
    private readonly string profile, deviceName;
    private readonly IIdentityVault vault;
    private readonly PreviewOnlyDeviceOptions options;
    private readonly PreviewOnlyDeviceStore store;
    private readonly DesktopControlArbiter arbiter;
    private readonly PreviewFrameAuthority frames;
    private readonly PreviewOnlyTargetPolicy target;
    private readonly SemaphoreSlim lifecycle = new(1, 1), peerMutation = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object channelsGate = new(), pairingGate = new();
    private readonly HashSet<NativeInputConnection> channels = [];
    private DeviceIdentity? identity;
    private DeviceTrustStore? trust;
    private DesktopInputServer? server;
    private SavedPreviewPeer? peer;
    private PreviewOnlyDeviceStatus status = new();
    private string message = "预览操作尚未准备";
    private string? error;
    private int started, disposed, receiverPort;
    private long pairingGeneration, controlGeneration;

    public PreviewOnlyDeviceService(string dataDirectory, string deviceName, IIdentityVault vault,
        IPreviewInputTarget target, PreviewOnlyDeviceOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory); ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BindAddress); ArgumentNullException.ThrowIfNull(options.ReadCurrentGeometry);
        ArgumentNullException.ThrowIfNull(options.Prepare); ArgumentNullException.ThrowIfNull(options.Renew);
        ArgumentNullException.ThrowIfNull(options.RequireLocalApproval); ArgumentNullException.ThrowIfNull(options.ConfirmIncomingPairingAsync);
        ArgumentNullException.ThrowIfNull(options.CheckAccess);
        if (!Path.IsPathFullyQualified(dataDirectory) || Path.GetFullPath(dataDirectory) == Path.GetPathRoot(dataDirectory)) throw new ArgumentException("Choose an absolute preview data directory.");
        if (options.ReceiverPort is < 0 or > 65535 || options.BindAddress.Equals(System.Net.IPAddress.Any) || options.BindAddress.Equals(System.Net.IPAddress.IPv6Any))
            throw new ArgumentException("Choose a concrete preview receiver address and port.");
        profile = Path.Combine(Path.GetFullPath(dataDirectory), "preview-control");
        this.deviceName = deviceName; this.vault = vault; this.options = options; store = new(profile);
        arbiter = new(options.Clock); frames = new(options.ReadCurrentGeometry, options.Clock);
        this.target = new(target, options, arbiter, fingerprint => Allowed(PreviewControlDirection.Receive, fingerprint), QueueStatus);
    }
    public PreviewOnlyDeviceStatus Status => Volatile.Read(ref status);
    public event Action? Changed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); if (Volatile.Read(ref started) != 0) return;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            request.Token.ThrowIfCancellationRequested(); store.Open();
            // Once the vault stores a new key its matching metadata must commit, even if a dialog closes.
            // Cancellation still prevents listening/arming, and a retry loads that same identity instead of resetting it.
            identity = await DeviceIdentity.OpenAsync(profile, deviceName, vault, CancellationToken.None).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            trust = await DeviceTrustStore.OpenAsync(profile, request.Token).ConfigureAwait(false);
            if (trust.Devices.Any(device => device.Permissions != DevicePermission.PreviewControl)) throw new InvalidDataException("This store contains permissions outside preview control.");
            Volatile.Write(ref peer, store.Read());
            server = new(identity, trust, options.BindAddress, options.ReceiverPort, target, ConfirmIncomingAsync, options.Clock,
                new DesktopInputServerOptions
                {
                    Arbiter = arbiter, PreviewFrames = frames,
                    PrepareInput = (fingerprint, mode) => mode == DesktopControlMode.Preview && target.Prepare(fingerprint),
                    RenewInput = (fingerprint, mode, consent) => mode == DesktopControlMode.Preview && target.Renew(fingerprint, consent),
                    Paired = CommitIncoming
                });
            server.StatusChanged += OnServerStatus;
            await server.StartAsync(request.Token).ConfigureAwait(false);
            Volatile.Write(ref receiverPort, server.Port); Volatile.Write(ref started, 1);
            SetStatus(ApprovedPeer() is null ? "请单独配对预览操作，当前仅观看" : "预览操作已配对，当前仅观看", clearError: true);
        }
        catch
        {
            Volatile.Write(ref started, 0); await ReleaseRuntimeAsync().ConfigureAwait(false);
            SetStatus("预览操作准备失败", "无法准备独立预览身份或接收服务。"); throw;
        }
        finally { lifecycle.Release(); }
    }
    private SavedPreviewPeer? ApprovedPeer()
    {
        if (Volatile.Read(ref started) == 0 || Volatile.Read(ref disposed) != 0) return null;
        var value = Volatile.Read(ref peer); var currentTrust = trust;
        if (value is null || currentTrust is null || !PreviewOnlyDeviceStore.Valid(value)) return null;
        return currentTrust.Devices.Any(device => device.Permissions == DevicePermission.PreviewControl &&
            string.Equals(device.Fingerprint, value.Endpoint.Fingerprint, StringComparison.OrdinalIgnoreCase) && device.DeviceName == value.Endpoint.DeviceName) ? value : null;
    }
    private bool Allowed(PreviewControlDirection direction, string fingerprint)
    {
        var approved = ApprovedPeer();
        if (approved is null || !string.Equals(approved.Endpoint.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var admission = options.CheckAccess(direction, fingerprint);
            if (admission is { Allowed: true }) return true;
            SetStatus("预览操作已停止", admission?.Reason ?? "当前键鼠焦点不允许预览操作。");
        }
        catch { SetStatus("预览操作已停止", "无法确认当前键鼠焦点，已拒绝预览操作。"); }
        return false;
    }
    private TrustedEndpoint? ReadSendEndpoint(SavedPreviewPeer expectedPeer, long generation)
    {
        if (generation != Interlocked.Read(ref controlGeneration) || !ReferenceEquals(ApprovedPeer(), expectedPeer)) return null;
        if (!Allowed(PreviewControlDirection.Send, expectedPeer.Endpoint.Fingerprint)) return null;
        return generation == Interlocked.Read(ref controlGeneration) && ReferenceEquals(ApprovedPeer(), expectedPeer) ? expectedPeer.Endpoint : null;
    }
    public PreviewFrameProof? BindCapturedFrame(byte[] jpeg, PreviewSourceGeometry? geometry)
    {
        if (Volatile.Read(ref started) == 0 || Volatile.Read(ref disposed) != 0 || ApprovedPeer() is null || geometry is null) return null;
        try { return frames.Register(jpeg, geometry); }
        catch { InvalidateCapturedFrames(); return null; }
    }
    public void InvalidateCapturedFrames()
    {
        frames.Invalidate();
        try { target.Invalidate(); }
        catch { SetStatus("预览操作已停止", "无法确认预览输入已完全释放。"); }
        QueueStatus();
    }
    public void StopControl(string reason)
    {
        var reasonText = string.IsNullOrWhiteSpace(reason) ? "预览操作已停止" : reason;
        try
        {
            lock (channelsGate)
            {
                Interlocked.Increment(ref controlGeneration);
                foreach (var connection in channels) connection.Abort(reasonText);
                arbiter.StopAll(reasonText); target.Invalidate();
            }
        }
        catch { SetStatus("预览操作已停止", "无法确认预览输入已完全释放。"); }
        frames.Invalidate(); SetStatus("预览操作已停止");
    }
    private NativeInputConnection[] Connections() { lock (channelsGate) return channels.ToArray(); }
    private void OnServerStatus(string _) => QueueStatus();
    private void QueueStatus() => ThreadPool.QueueUserWorkItem(_ => PublishStatus());
    private void SetStatus(string value, string? failure = null, bool clearError = false)
    {
        Volatile.Write(ref message, value);
        if (clearError || failure is not null) Volatile.Write(ref error, failure);
        PublishStatus();
    }
    private void PublishStatus()
    {
        var approved = ApprovedPeer();
        Volatile.Write(ref status, new()
        {
            Started = Volatile.Read(ref started) != 0 && Volatile.Read(ref disposed) == 0,
            Paired = approved is not null, ControlActive = arbiter.Current is not null,
            LocalFingerprint = identity?.Fingerprint, PeerFingerprint = approved?.Endpoint.Fingerprint,
            PeerName = approved?.Endpoint.DeviceName, ReceiverPort = Volatile.Read(ref receiverPort),
            Message = Volatile.Read(ref message), Error = Volatile.Read(ref error)
        });
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (Changed is not { } handlers) return;
            foreach (Action handler in handlers.GetInvocationList()) try { handler(); } catch { }
        });
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    private async Task ReleaseRuntimeAsync()
    {
        if (server is { } receiver) { server = null; receiver.StatusChanged -= OnServerStatus; await receiver.DisposeAsync().ConfigureAwait(false); }
        await peerMutation.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var connection in Connections()) await RemoveConnectionAsync(connection).ConfigureAwait(false);
            trust?.Dispose(); trust = null; identity?.Dispose(); identity = null; store.Dispose();
            Volatile.Write(ref receiverPort, 0);
        }
        finally { peerMutation.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        InvalidatePairing(); Volatile.Write(ref started, 0); lifetime.Cancel(); StopControl("preview-service-disposed");
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await ReleaseRuntimeAsync().ConfigureAwait(false);
            arbiter.Dispose(); SetStatus("预览操作已停止");
        }
        finally { lifecycle.Release(); }
    }
}
