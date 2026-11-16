using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.DeskLink.Coordination;
namespace DesktopToolbox.DeskLink.Collaboration;

/// <summary>Separate authenticated data endpoint. Only explicitly selected shares and queued files are readable.</summary>
public sealed partial class CollaborationServer : IAsyncDisposable
{
    private readonly CollaborationServerOptions options;
    private readonly SharedCatalog shares;
    private readonly CollaborationPreviewExchange previews;
    private readonly TimeProvider clock;
    private readonly object peerGate = new();
    private readonly CollaborationClipboardExchange clipboard = new();
    private readonly ConcurrentDictionary<string, PreparedTransfer> outgoing = new();
    private readonly ConcurrentDictionary<string, TransferProgress> transfers = new();
    private readonly ConcurrentDictionary<string, TransferReceiver> incoming = new();
    private readonly ConcurrentDictionary<int, Task> clients = new();
    private readonly SemaphoreSlim clientLimit = new(8, 8);
    private readonly CollaborationBandwidth bandwidth;
    private TransferReceiver? receiver;
    private CancellationTokenSource? running;
    private TcpListener? listener;
    private X509Certificate2? certificate;
    private Task accepting = Task.CompletedTask;
    private int clientId;
    private long lastSeenTicks;
    private DateTimeOffset? passivePeerSeen;
    private byte[]? passivePeerToken;
    private readonly MultiPeerDisplayQueue? experimentalDisplayQueue;
    private bool experimentalDisplayInvalidated;
    private Guid? experimentalPeerEpoch;
    private DateTimeOffset? experimentalPeerSeen;
    internal MultiPeerDisplayQueue? ExperimentalDisplayQueue { get { lock (peerGate) return experimentalDisplayInvalidated ? null : experimentalDisplayQueue; } }
    internal Guid? ExperimentalPeerDisplayEpoch
    {
        get
        {
            lock (peerGate)
            {
                var now = clock.GetUtcNow();
                return !experimentalDisplayInvalidated && experimentalDisplayQueue is not null && experimentalPeerSeen is { } seen && now >= seen && now - seen < TimeSpan.FromSeconds(4)
                    ? experimentalPeerEpoch : null;
            }
        }
    }
    public event EventHandler<Exception>? PeerError;
    public int Port => (listener?.LocalEndpoint as IPEndPoint)?.Port ?? options.Port;
    public DateTimeOffset? LastPeerSeenUtc => Interlocked.Read(ref lastSeenTicks) is var value && value != 0 ? new DateTimeOffset(value, TimeSpan.Zero) : null;
    public bool IsPeerConnected => LastPeerSeenUtc is { } seen && clock.GetUtcNow() - seen < TimeSpan.FromSeconds(8);
    public bool LocalCaptureRequested => previews.LocalCaptureRequested;
    public int LocalCaptureMaxWidth => previews.LocalCaptureMaxWidth;
    public int LocalCaptureMaxHeight => previews.LocalCaptureMaxHeight;
    public int LocalCaptureRefreshSeconds => previews.LocalCaptureRefreshSeconds;
    public bool RemoteClipboardEnabled => clipboard.RemoteEnabled && IsPeerConnected;
    public CollaborationServer(CollaborationServerOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(options.ProfileDirectory) || options.Port is < 0 or > 65535 || options.BytesPerSecond is < 65536 or > 64 * 1024 * 1024) throw new ArgumentException("Invalid collaboration endpoint options.");
        if ((options.ReverseShareTestVault is null) != (options.ReverseShareTestPeer is null) ||
            options.ReverseShareTestVault is not null && (!IPAddress.IsLoopback(options.BindAddress) || options.TokenProvider is null))
            throw new ArgumentException("Reverse-share experiment requires an isolated paired loopback endpoint.");
        this.clock = clock ?? TimeProvider.System; previews = new(this.clock); livePreview = new(this);
        this.options = options; shares = new(options.SharedDirectories); bandwidth = new(options.BytesPerSecond); UpdateReceiveDirectory(options.ReceiveDirectory);
        experimentalDisplayQueue = options.EnableExperimentalDisplay ? new MultiPeerDisplayQueue(() => this.clock.GetUtcNow()) : null;
    }
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (running is not null) return;
        var certificatePath = options.CertificateFile ?? Path.Combine(options.ProfileDirectory, "control.pem");
        if (!File.Exists(certificatePath) || options.TokenProvider is null && !File.Exists(Path.Combine(options.ProfileDirectory, "pair-token"))) throw new InvalidOperationException("Pair the computers before enabling file collaboration.");
        certificate = PrivateFiles.LoadTlsCertificate(certificatePath);
        await ReadTokenAsync(ct);
        var queueDirectory = Path.Combine(options.ProfileDirectory, "collaboration-outgoing");
        if (Directory.Exists(queueDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(queueDirectory, "*.json").Take(128))
            {
                try { var item = JsonSerializer.Deserialize<PreparedTransfer>(await File.ReadAllTextAsync(file, ct), JsonLineProtocol.Json); if (item is not null) { TransferReceiver.ValidateManifest(item.Manifest); ValidateSharedOffer(item); outgoing[item.Manifest.Id] = item; transfers[item.Manifest.Id] = Progress(item.Manifest, 0, "queued"); } }
                catch (Exception e) when (e is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { PeerError?.Invoke(this, e); }
            }
        }
        listener = new(options.BindAddress, options.Port); listener.Start(8); running = new(); StartReverseShareTest(); accepting = AcceptAsync(running.Token);
    }
    public void UpdateReceiveDirectory(string? path)
    {
        var next = string.IsNullOrWhiteSpace(path) ? null : new TransferReceiver(path); Interlocked.Exchange(ref receiver, next);
        foreach (var id in incoming.Keys)
        {
            if (transfers.TryGetValue(id, out var progress) && progress.State == "complete") continue;
            if (next is null) incoming.TryRemove(id, out _); else incoming[id] = next;
        }
    }
    public void UpdateSharedDirectories(IReadOnlyList<SharedDirectory> directories)
    {
        shares.Update(directories);
        foreach (var item in outgoing.Values.Where(x => x.SharedRootId is not null))
        {
            try { ValidateSharedOffer(item); } catch (Exception e) when (e is UnauthorizedAccessException or ArgumentException) { CancelTransfer(item.Manifest.Id); }
        }
    }
    public SharedListing ListLocalShared(string? rootId = null, string relativePath = "") => shares.List(rootId, relativePath);
    public Task<SharedPreview> GetLocalSharedPreviewAsync(string rootId, string relativePath, CancellationToken ct = default) => shares.PreviewAsync(rootId, relativePath, ct);
    public string ResolveLocalSharedPath(string rootId, string relativePath) => shares.Resolve(rootId, relativePath);
    public async Task<TransferReceipt> CopyLocalSharedAsync(string rootId, string relativePath, string receiveDirectory, CancellationToken ct = default)
    {
        var source = shares.Resolve(rootId, relativePath);
        var targetPath = CollaborationPaths.Resolve(receiveDirectory);
        if (Directory.Exists(source) && (targetPath.Equals(source, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            targetPath.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new ArgumentException("Choose a receive directory outside the source folder.");
        var item = await PreparedTransfer.CreateAsync([source], ct);
        var target = new TransferReceiver(receiveDirectory); var resume = await target.BeginAsync(item.Manifest, ct);
        foreach (var cursor in resume.Files)
        {
            var entry = item.Manifest.Entries.Single(x => x.RelativePath == cursor.RelativePath); var offset = cursor.Offset;
            while (offset < entry.Length)
            {
                var chunk = await item.ReadChunkAsync(cursor.RelativePath, offset, ct); await target.WriteChunkAsync(item.Manifest.Id, cursor.RelativePath, offset, chunk.Bytes, chunk.Sha256, ct); offset += chunk.Bytes.Length;
            }
        }
        return await target.CompleteAsync(item.Manifest.Id, ct);
    }
    public Guid? LocalCaptureRequestId => previews.LocalCaptureRequestId;
    public Guid? RemoteCaptureRequestId => previews.RemoteCaptureRequestId;
    public bool RemotePassivePreviewGenerationSupported { get { lock (peerGate) return passivePeerSeen is { } seen && clock.GetUtcNow() - seen < TimeSpan.FromSeconds(4); } }
    public PreviewSourceStatus? RemotePreviewSourceStatus => previews.RemotePreviewSourceStatus;
    public void SetLocalPreviewSource(PreviewSourceStatus status) => previews.SetLocalSource(status);
    public void SetPreviewRequested(bool enabled, int maxWidth = 960, int maxHeight = 540, int previewRefreshSeconds = PreviewCaptureCadence.DefaultRefreshSeconds, Guid? requestId = null) => previews.SetRequested(enabled, maxWidth, maxHeight, previewRefreshSeconds, requestId);
    public void SetLocalPreview(PreviewFrame frame) => previews.SetLocal(frame);
    public PreviewFrame GetRemotePreview()
    {
        lock (peerGate)
        {
            if (IsPeerConnected) return previews.GetRemote();
            previews.ClearFrames(); return PreviewFrame.Off("offline");
        }
    }
    public void SetLocalClipboardEnabled(bool enabled) => clipboard.SetLocalEnabled(enabled);
    public void SetLocalClipboard(ClipboardPayload value) => clipboard.SetLocal(value);
    public void ClearClipboard() => clipboard.ClearSharedCurrent();
    public ClipboardPayload? GetRemoteClipboard() => IsPeerConnected ? clipboard.GetRemote() : null;
    public IReadOnlyList<TransferProgress> GetTransfers() => transfers.Values.OrderBy(x => x.State == "complete").Take(100).ToArray();
    public async Task<TransferManifest> QueueFilesAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        if (outgoing.Count >= 128) throw new InvalidOperationException("Transfer queue is full.");
        var item = await PreparedTransfer.CreateAsync(paths, ct);
        return await QueuePreparedAsync(item, ct);
    }
    private async Task<TransferManifest> QueuePreparedAsync(PreparedTransfer item, CancellationToken ct)
    {
        if (outgoing.Count >= 128) throw new InvalidOperationException("Transfer queue is full.");
        await PrivateFiles.WriteAsync(QueuePath(item.Manifest.Id), JsonSerializer.Serialize(item, JsonLineProtocol.Json), ct);
        outgoing[item.Manifest.Id] = item; transfers[item.Manifest.Id] = Progress(item.Manifest, 0, "queued"); return item.Manifest;
    }
    private void ValidateSharedOffer(PreparedTransfer item)
    {
        if (item.SharedRootId is null) return;
        var current = shares.Resolve(item.SharedRootId, item.SharedRelativePath ?? "");
        if (!current.Equals(item.SharedSourcePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new UnauthorizedAccessException("The shared directory was changed.");
    }
    public void CancelTransfer(string id)
    {
        if (outgoing.TryRemove(id, out var item)) { transfers[id] = Progress(item.Manifest, 0, "cancelled"); if (File.Exists(QueuePath(id))) File.Delete(QueuePath(id)); }
    }
    private string QueuePath(string id) => Path.Combine(options.ProfileDirectory, "collaboration-outgoing", id + ".json");
    private static TransferProgress Progress(TransferManifest manifest, long bytes, string state) => new(manifest.Id, manifest.Entries[0].RelativePath.Split('/')[0], bytes, manifest.TotalBytes, state);
    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(ct);
                if (!await clientLimit.WaitAsync(0, ct)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref clientId); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); clients[id] = done.Task;
                _ = HandleTrackedAsync(id, client, done, ct);
            }
        }
        catch (Exception e) when (ct.IsCancellationRequested && e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task HandleTrackedAsync(int id, TcpClient client, TaskCompletionSource done, CancellationToken ct)
    {
        try { await HandleAsync(client, ct); }
        finally { clients.TryRemove(id, out _); clientLimit.Release(); done.TrySetResult(); }
    }
    private async Task HandleAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await using var secure = new SslStream(client.GetStream(), false);
            try
            {
                await secure.AuthenticateAsServerAsync(new() { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, EncryptionPolicy = EncryptionPolicy.RequireEncryption }, deadline.Token);
                var request = await CollaborationProtocol.ReadAsync<CollaborationRequest>(secure, deadline.Token);
                if (request.Version != 1 || request.Id is null || request.Id.Length != 32 || !request.Id.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid data request identifier.");
                byte[] expected;
                try { expected = await ReadTokenAsync(deadline.Token); }
                catch (UnauthorizedAccessException)
                {
                    await CollaborationProtocol.WriteAsync(secure, new CollaborationResponse(1, request.Id, false, CollaborationProtocol.Element(false), "unauthorized", "Pairing was revoked."), deadline.Token);
                    return;
                }
                byte[] supplied;
                try { supplied = Convert.FromBase64String(request.Token); } catch (FormatException) { supplied = []; }
                if (supplied.Length != 32 || !CryptographicOperations.FixedTimeEquals(expected, supplied))
                {
                    await CollaborationProtocol.WriteAsync(secure, new CollaborationResponse(1, request.Id, false, CollaborationProtocol.Element(false), "unauthorized", "Pairing was revoked. Import the new pairing file."), deadline.Token); return;
                }
                long authGeneration;
                try { authGeneration = await LivePreview.ObserveAuthenticatedTokenAsync(expected, deadline.Token); }
                catch (UnauthorizedAccessException)
                {
                    await CollaborationProtocol.WriteAsync(secure, new CollaborationResponse(1, request.Id, false, CollaborationProtocol.Element(false), "unauthorized", "Pairing was revoked. Import the new pairing file."), deadline.Token);
                    return;
                }
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                if (!LivePreview.TryCommitAuthenticated(authGeneration, () =>
                {
                    lock (peerGate)
                    {
                        if (passivePeerToken is not null && !CryptographicOperations.FixedTimeEquals(passivePeerToken, expected))
                        {
                            passivePeerSeen = null;
                            experimentalDisplayInvalidated = true;
                            experimentalPeerEpoch = null; experimentalPeerSeen = null;
                            clipboard.ClearCurrent();
                            previews.InvalidatePeer();
                        }
                        passivePeerToken = expected.ToArray();
                        // A new TLS request is normal. Clear only when the previous peer session actually expired.
                        if (!IsPeerConnected) { clipboard.ClearCurrent(); previews.ClearFrames(); }
                        Interlocked.Exchange(ref lastSeenTicks, clock.GetUtcNow().UtcTicks);
                    }
                }))
                {
                    await CollaborationProtocol.WriteAsync(secure, new CollaborationResponse(1, request.Id, false, CollaborationProtocol.Element(false), "unauthorized", "Pairing was revoked. Import the new pairing file."), deadline.Token);
                    return;
                }
                if (request.Action is "preview-live-subscribe" or "preview-live-upload")
                {
                    deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                    await LivePreview.HandleAsync(request, secure, expected, authGeneration, deadline.Token);
                    return;
                }
                CollaborationResponse response;
                try
                {
                    if (IsPreviewAction(request.Action))
                    {
                        JsonElement result = default;
                        var reverseStatusRequested = request.Action == "status" && request.Data.ValueKind == JsonValueKind.Object &&
                            request.Data.TryGetProperty("supportsReverseShareTest", out var reverse) && reverse.ValueKind == JsonValueKind.True;
                        if (request.Action == "reverse-share-test-complete")
                        {
                            if (!HasReverseTest) throw new NotSupportedException("Reverse-share test is disabled.");
                            var completion = CollaborationProtocol.Parse<ReverseShareTestCompletion>(request.Data);
                            if (completion.Data is null || completion.Data.Length > ReverseTestMaximumResponseBytes) throw new ArgumentException("Test response exceeds the limit.");
                            var accepted = false;
                            if (!await options.ReverseShareTestVault!.TryCommitAuthorizedAsync(options.ReverseShareTestPeer!, expected,
                                () => accepted = LivePreview.TryCommitAuthenticated(authGeneration, () => CompleteReverseShareTest(completion)), deadline.Token) || !accepted)
                                throw new UnauthorizedAccessException("Pairing was revoked.");
                            result = CollaborationProtocol.Element(true);
                        }
                        else
                        {
                            if (!LivePreview.TryCommitAuthenticated(authGeneration, () => result = DispatchPreview(request)))
                                throw new UnauthorizedAccessException("Pairing was revoked.");
                            if (reverseStatusRequested && HasReverseTest)
                            {
                                (bool Supported, ReverseShareRequest? Request, long Sequence) reverseStatus = default;
                                var accepted = false;
                                if (!await options.ReverseShareTestVault!.TryCommitAuthorizedAsync(options.ReverseShareTestPeer!, expected,
                                    () => accepted = LivePreview.TryCommitAuthenticated(authGeneration, () => reverseStatus = DeliverReverseShareTest()), deadline.Token) || !accepted)
                                    throw new UnauthorizedAccessException("Pairing was revoked.");
                                var status = CollaborationProtocol.Parse<CollaborationStatus>(result);
                                result = CollaborationProtocol.Element(status with { ReverseShareTestSupported = reverseStatus.Supported, ReverseShareRequest = reverseStatus.Request, ReverseShareTestSequence = reverseStatus.Sequence });
                            }
                        }
                        response = new(1, request.Id, true, result);
                    }
                    else response = new(1, request.Id, true, await DispatchAsync(request, deadline.Token));
                }
                catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
                {
                    PeerError?.Invoke(this, e);
                    var code = e is TransferSourceUnavailableException ? "source-changed" : e is UnauthorizedAccessException ? "denied" : e is NotSupportedException ? "unsupported" : e is FileNotFoundException or DirectoryNotFoundException ? "missing" : e is InvalidDataException ? "checksum" : e is IOException && (e.HResult & 0xffff) is 0x70 or 0x27 ? "disk-full" : "unavailable";
                    var message = code switch { "source-changed" => "The source file changed or was moved. Choose it again to send a new copy.", "denied" => "The selected location is not authorized.", "unsupported" => e.Message, "missing" => "The source file or folder is unavailable.", "checksum" => "File contents changed or the transfer is incomplete. Retry this transfer.", "disk-full" => "The receiving disk is full. Choose another receive folder.", _ => e is InvalidOperationException or ArgumentException ? e.Message : "File access failed. Check the disk and permissions, then retry." };
                    response = new(1, request.Id, false, CollaborationProtocol.Element(false), code, message);
                }
                await CollaborationProtocol.WriteAsync(secure, response, deadline.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException or JsonException or ArgumentException) { PeerError?.Invoke(this, e); }
        }
    }
    private async Task<byte[]> ReadTokenAsync(CancellationToken ct)
    {
        if (options.TokenProvider is { } provider)
        {
            var current = await provider(ct);
            return current is { Length: 32 } ? current : throw new InvalidDataException("Stored pairing token is invalid.");
        }
        byte[] value;
        try { value = Convert.FromBase64String((await File.ReadAllTextAsync(Path.Combine(options.ProfileDirectory, "pair-token"), ct)).Trim()); }
        catch (FormatException) { throw new InvalidDataException("Stored pairing token is invalid."); }
        return value.Length == 32 ? value : throw new InvalidDataException("Stored pairing token is invalid.");
    }
    private static bool IsPreviewAction(string action) => action is "status" or "preview-request" or "preview-source-status" or "preview-get" or "preview-publish" or "display-complete" or "reverse-share-test-complete";
    // Kept synchronous so the authenticated-generation guard spans each short
    // state mutation, without serializing a media stream or file I/O.
    private JsonElement DispatchPreview(CollaborationRequest request)
    {
        switch (request.Action)
        {
            case "status":
                if (request.Data.ValueKind == JsonValueKind.Object && request.Data.TryGetProperty("supportsLivePreview", out var capability) && capability.ValueKind == JsonValueKind.True) LivePreview.AdvertisePeer();
                if (request.Data.ValueKind == JsonValueKind.Object && request.Data.TryGetProperty("supportsPassivePreviewGeneration", out var passive) && passive.ValueKind == JsonValueKind.True) lock (peerGate) passivePeerSeen = clock.GetUtcNow();
                MultiPeerDisplayRequest? display = null;
                if (request.Data.ValueKind == JsonValueKind.Object && request.Data.TryGetProperty("displayEpoch", out var epochValue))
                {
                    var epoch = Guid.Empty;
                    var valid = epochValue.ValueKind == JsonValueKind.String && epochValue.TryGetGuid(out epoch) && epoch != Guid.Empty;
                    lock (peerGate)
                    {
                        if (!experimentalDisplayInvalidated && experimentalDisplayQueue is not null && valid)
                        { experimentalPeerEpoch = epoch; experimentalPeerSeen = clock.GetUtcNow(); }
                        else { experimentalPeerEpoch = null; experimentalPeerSeen = null; }
                    }
                    if (valid) display = ExperimentalDisplayQueue?.Deliver(epoch);
                }
                return CollaborationProtocol.Element(new CollaborationStatus(previews.RemoteCaptureRequested, outgoing.Values.Take(2).Select(x => x.Manifest).ToArray(), clipboard.LocalEnabled) { CaptureMaxWidth = previews.RemoteCaptureMaxWidth, CaptureMaxHeight = previews.RemoteCaptureMaxHeight, PreviewRefreshSeconds = previews.RemoteCaptureRefreshSeconds, LivePreviewSupported = true, RequestedLivePreview = LivePreview.Demand, PassivePreviewGenerationSupported = true, CaptureRequestId = previews.RemoteCaptureRequestId, LocalPreviewSourceStatus = previews.LocalPreviewSourceStatus, DisplayRequest = display });
            case "display-complete": { var result = CollaborationProtocol.Parse<DisplayCommandResult>(request.Data); (ExperimentalDisplayQueue ?? throw new NotSupportedException("显示器控制未启用。 ")).Complete(result.Id, result.Status); return CollaborationProtocol.Element(true); }
            case "preview-request": { var demand = CollaborationProtocol.Parse<PreviewRequest>(request.Data); previews.SetRemoteRequested(demand.Enabled, demand.MaxWidth, demand.MaxHeight, demand.PreviewRefreshSeconds, demand.RequestId); return CollaborationProtocol.Element(true); }
            case "preview-source-status": previews.SetRemoteSource(CollaborationProtocol.Parse<PreviewSourceStatus>(request.Data)); return CollaborationProtocol.Element(true);
            case "preview-get": return CollaborationProtocol.Element(previews.GetLocal());
            case "preview-publish": previews.SetRemote(CollaborationProtocol.Parse<PreviewFrame>(request.Data)); return CollaborationProtocol.Element(true);
            default: throw new ArgumentException("Unsupported preview action.");
        }
    }
    private async Task<JsonElement> DispatchAsync(CollaborationRequest request, CancellationToken ct)
    {
        switch (request.Action)
        {
            case "clipboard-enabled": clipboard.SetRemoteEnabled(CollaborationProtocol.Parse<bool>(request.Data)); return CollaborationProtocol.Element(true);
            case "clipboard-publish": clipboard.SetRemote(CollaborationProtocol.Parse<ClipboardPayload>(request.Data)); return CollaborationProtocol.Element(true);
            case "clipboard-clear": clipboard.ClearSharedCurrent(); return CollaborationProtocol.Element(true);
            case "clipboard-get": return CollaborationProtocol.Element(clipboard.GetLocal());
            case "shared-list": { var args = CollaborationProtocol.Parse<SharedRequest>(request.Data); return CollaborationProtocol.Element(shares.List(args.RootId, args.RelativePath)); }
            case "shared-preview": { var args = CollaborationProtocol.Parse<SharedRequest>(request.Data); return CollaborationProtocol.Element(await shares.PreviewAsync(args.RootId ?? "", args.RelativePath, ct)); }
            case "shared-offer":
            {
                var args = CollaborationProtocol.Parse<SharedRequest>(request.Data); var path = shares.Resolve(args.RootId ?? "", args.RelativePath);
                var item = await PreparedTransfer.CreateAsync([path], ct);
                item = item with { SharedRootId = args.RootId, SharedRelativePath = args.RelativePath, SharedSourcePath = path }; ValidateSharedOffer(item);
                return CollaborationProtocol.Element(await QueuePreparedAsync(item, ct));
            }
            case "receive-begin":
            {
                var manifest = CollaborationProtocol.Parse<TransferManifest>(request.Data); TransferReceiver.ValidateManifest(manifest);
                var current = incoming.TryGetValue(manifest.Id, out var previous) ? previous : receiver ?? throw new InvalidOperationException("请先在 Windows 互联设置中选择收件文件夹，然后重试。");
                current = incoming.GetOrAdd(manifest.Id, current); var status = await current.BeginAsync(manifest, ct);
                transfers[manifest.Id] = Progress(manifest, status.Files.Sum(x => x.Offset), status.Receipt is null ? "receiving" : "complete"); return CollaborationProtocol.Element(status);
            }
            case "receive-chunk":
            {
                var args = CollaborationProtocol.Parse<TransferFileRequest>(request.Data);
                await bandwidth.WaitAsync(args.Bytes?.Length ?? 0, ct);
                await (incoming.TryGetValue(args.Id, out var receiving) ? receiving : throw new InvalidOperationException("Begin the transfer before sending chunks.")).WriteChunkAsync(args.Id, args.RelativePath, args.Offset, args.Bytes ?? [], args.Sha256 ?? "", ct);
                if (transfers.TryGetValue(args.Id, out var progress)) transfers[args.Id] = progress with { BytesTransferred = progress.BytesTransferred + (args.Bytes?.Length ?? 0), State = "receiving" }; return CollaborationProtocol.Element(true);
            }
            case "receive-reset":
            {
                var args = CollaborationProtocol.Parse<TransferFileRequest>(request.Data);
                await (incoming.TryGetValue(args.Id, out var receiving) ? receiving : throw new InvalidOperationException("Begin the transfer before resetting it.")).ResetFileAsync(args.Id, args.RelativePath, ct);
                if (transfers.TryGetValue(args.Id, out var progress)) transfers[args.Id] = progress with { BytesTransferred = Math.Max(0, progress.BytesTransferred - Math.Max(0, args.Offset)), State = "receiving" };
                return CollaborationProtocol.Element(true);
            }
            case "receive-complete":
            {
                var id = CollaborationProtocol.Parse<string>(request.Data); var receipt = await (incoming.TryGetValue(id, out var receiving) ? receiving : throw new InvalidOperationException("Begin the transfer before committing it.")).CompleteAsync(id, ct);
                if (transfers.TryGetValue(id, out var progress)) transfers[id] = progress with { BytesTransferred = receipt.TotalBytes, State = "complete" }; return CollaborationProtocol.Element(receipt);
            }
            case "outgoing-chunk":
            {
                var args = CollaborationProtocol.Parse<TransferFileRequest>(request.Data);
                var item = outgoing.TryGetValue(args.Id, out var offered) ? offered : throw new FileNotFoundException("Transfer was cancelled.");
                ValidateSharedOffer(item); FileChunk chunk;
                try { chunk = await item.ReadChunkAsync(args.RelativePath, args.Offset, ct); }
                catch (Exception error) when (error is TransferSourceUnavailableException or FileNotFoundException or DirectoryNotFoundException)
                {
                    outgoing.TryRemove(args.Id, out _); transfers[args.Id] = Progress(item.Manifest, 0, "failed") with { Error = "Source file changed or was moved. Choose it again to send a new copy." };
                    if (File.Exists(QueuePath(args.Id))) File.Delete(QueuePath(args.Id));
                    throw new TransferSourceUnavailableException("The source file is no longer available.");
                }
                await bandwidth.WaitAsync(chunk.Bytes.Length, ct);
                var previous = item.Manifest.Entries.TakeWhile(x => x.RelativePath != args.RelativePath).Sum(x => x.Length);
                transfers[args.Id] = Progress(item.Manifest, previous + args.Offset + chunk.Bytes.Length, "sending"); return CollaborationProtocol.Element(chunk);
            }
            case "outgoing-complete":
            {
                var id = CollaborationProtocol.Parse<string>(request.Data);
                if (outgoing.TryRemove(id, out var item)) { transfers[id] = Progress(item.Manifest, item.Manifest.TotalBytes, "complete"); if (File.Exists(QueuePath(id))) File.Delete(QueuePath(id)); }
                return CollaborationProtocol.Element(true);
            }
            default: throw new ArgumentException("Unsupported collaboration action.");
        }
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (running is null) return;
        livePreview?.Reset(); StopReverseShareTest(); running.Cancel(); listener?.Stop(); await accepting; await Task.WhenAll(clients.Values).WaitAsync(ct);
        running.Dispose(); running = null; listener = null; certificate?.Dispose(); certificate = null;
        previews.InvalidatePeer(); clipboard.SetRemoteEnabled(false); Interlocked.Exchange(ref lastSeenTicks, 0);
        lock (peerGate) { passivePeerSeen = null; passivePeerToken = null; }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); clientLimit.Dispose(); }
}
