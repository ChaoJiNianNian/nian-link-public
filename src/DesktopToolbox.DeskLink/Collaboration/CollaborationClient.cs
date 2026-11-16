using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
namespace DesktopToolbox.DeskLink.Collaboration;

/// <summary>Mac initiates every authenticated connection; Windows never needs an inbound connection to Mac.</summary>
public sealed partial class CollaborationClient : IAsyncDisposable
{
    private readonly DeskLinkPairingBundle bundle;
    private readonly int port;
    private readonly CollaborationBandwidth bandwidth = new(8 * 1024 * 1024);
    private readonly SemaphoreSlim receiveGate = new(1, 1);
    private readonly SemaphoreSlim transferGate = new(2, 2);
    private bool disposed;
    private readonly object reverseShareTestGate = new();
    private ReverseShareRequest? reverseShareTestRequest;
    private long reverseShareTestLatestDelivery;
    public CollaborationClient(DeskLinkPairingBundle bundle, bool userApproved, int dataPort = 24802)
    {
        ArgumentNullException.ThrowIfNull(bundle); bundle.Validate();
        if (!userApproved) throw new InvalidOperationException("Approve the pairing file before connecting.");
        if (dataPort is < 1 or > 65535) throw new ArgumentException("Invalid collaboration data port.");
        this.bundle = bundle; port = dataPort; livePreview = new(this);
    }
    public async Task<CollaborationStatus> GetStatusAsync(CancellationToken ct = default, bool supportsLivePreview = false, bool supportsPassivePreviewGeneration = false, Guid? displayEpoch = null, bool supportsReverseShareTest = false)
    {
        object body = displayEpoch.HasValue ? new { supportsLivePreview, supportsPassivePreviewGeneration, displayEpoch, supportsReverseShareTest } :
            supportsLivePreview || supportsPassivePreviewGeneration || supportsReverseShareTest ? new { supportsLivePreview, supportsPassivePreviewGeneration, supportsReverseShareTest } : (object)true;
        var status = await RequestAsync<CollaborationStatus>("status", body, ct);
        if (supportsReverseShareTest && status.ReverseShareTestSupported && status.ReverseShareRequest is { } request)
            lock (reverseShareTestGate)
            {
                if (status.ReverseShareTestSequence > reverseShareTestLatestDelivery) { reverseShareTestLatestDelivery = status.ReverseShareTestSequence; reverseShareTestRequest = request; }
                else status = status with { ReverseShareRequest = null };
            }
        return status;
    }
    internal async Task CompleteReverseShareTestAsync(ReverseShareRequest request, byte[] result, CancellationToken ct = default)
    {
        lock (reverseShareTestGate)
            if (reverseShareTestRequest != request) throw new NotSupportedException("Reverse-share test request was not negotiated.");
        if (result is null || result.Length > 65536) throw new ArgumentException("Reverse-share test response is too large.");
        await RequestAsync<bool>("reverse-share-test-complete", new ReverseShareTestCompletion(request.Authorization, request.Id, result), ct);
        lock (reverseShareTestGate) if (reverseShareTestRequest == request) reverseShareTestRequest = null;
    }
    internal Task CompleteDisplayRequestAsync(Guid id, string status, CancellationToken ct = default) => RequestAsync<bool>("display-complete", new DisplayCommandResult(id, status), ct);
    public Task SetPreviewRequestedAsync(bool enabled, CancellationToken ct = default, int maxWidth = 960, int maxHeight = 540, int previewRefreshSeconds = PreviewCaptureCadence.DefaultRefreshSeconds, Guid? requestId = null)
    {
        PreviewCaptureCadence.ValidateRefreshSeconds(previewRefreshSeconds);
        return RequestAsync<bool>("preview-request", new PreviewRequest(enabled, maxWidth, maxHeight, previewRefreshSeconds, requestId), ct);
    }
    public Task<PreviewFrame> GetPreviewAsync(CancellationToken ct = default) => RequestAsync<PreviewFrame>("preview-get", true, ct);
    public Task PublishPreviewAsync(PreviewFrame frame, CancellationToken ct = default) => RequestAsync<bool>("preview-publish", frame, ct);
    public Task SetPreviewSourceAsync(PreviewSourceStatus status, CancellationToken ct = default) => RequestAsync<bool>("preview-source-status", status, ct);
    public Task SetClipboardEnabledAsync(bool enabled, CancellationToken ct = default) => RequestAsync<bool>("clipboard-enabled", enabled, ct);
    public Task PublishClipboardAsync(ClipboardPayload payload, CancellationToken ct = default) { CollaborationClipboardExchange.Validate(payload); return RequestAsync<bool>("clipboard-publish", payload, ct); }
    public Task ClearClipboardAsync(CancellationToken ct = default) => RequestAsync<bool>("clipboard-clear", true, ct);
    public Task<ClipboardPayload?> GetLatestClipboardAsync(CancellationToken ct = default) => RequestAsync<ClipboardPayload?>("clipboard-get", true, ct);
    public Task<SharedListing> ListSharedAsync(string? rootId = null, string relativePath = "", CancellationToken ct = default) => RequestAsync<SharedListing>("shared-list", new SharedRequest(rootId, relativePath), ct);
    public Task<SharedPreview> GetSharedPreviewAsync(string rootId, string relativePath, CancellationToken ct = default) => RequestAsync<SharedPreview>("shared-preview", new SharedRequest(rootId, relativePath), ct);
    public Task<TransferReceipt> SendFilesAsync(IEnumerable<string> paths, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) => SendCoreAsync(paths, null, progress, ct);
    /// <summary>Hosts may retain the progress ID plus original paths to resume the same offer after relaunch.</summary>
    public async Task<TransferReceipt> ResumeSendFilesAsync(IEnumerable<string> paths, string transferId, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        => await SendCoreAsync(paths, transferId, progress, ct);
    private async Task<TransferReceipt> SendCoreAsync(IEnumerable<string> paths, string? transferId, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        await transferGate.WaitAsync(ct);
        try
        {
            var prepared = await PreparedTransfer.CreateAsync(paths, ct);
            if (transferId is not null) prepared = prepared with { Manifest = prepared.Manifest with { Id = transferId } };
            return await UploadAsync(prepared, progress, ct);
        }
        finally { transferGate.Release(); }
    }
    private async Task<TransferReceipt> UploadAsync(PreparedTransfer item, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var manifest = item.Manifest; var name = manifest.Entries[0].RelativePath.Split('/')[0]; long sent = 0;
        progress?.Report(new(manifest.Id, name, 0, manifest.TotalBytes, "preparing"));
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var status = await RequestAsync<ReceiveStatus>("receive-begin", manifest, ct);
                    if (status.Receipt is not null) return status.Receipt;
                    sent = status.Files.Sum(x => x.Offset);
                    foreach (var entry in manifest.Entries.Where(x => !x.IsDirectory))
                    {
                        var cursor = status.Files.Single(x => x.RelativePath == entry.RelativePath);
                        if (!cursor.PrefixSha256.Equals(await CollaborationPaths.HashFileAsync(item.Sources[entry.RelativePath].Path, cursor.Offset, ct), StringComparison.OrdinalIgnoreCase))
                        {
                            await RequestAsync<bool>("receive-reset", new TransferFileRequest(manifest.Id, entry.RelativePath, cursor.Offset), ct);
                            sent -= cursor.Offset; cursor = cursor with { Offset = 0, PrefixSha256 = CollaborationPaths.Hash([]) };
                        }
                        var offset = cursor.Offset;
                        while (offset < entry.Length)
                        {
                            ct.ThrowIfCancellationRequested(); var chunk = await item.ReadChunkAsync(entry.RelativePath, offset, ct);
                            await bandwidth.WaitAsync(chunk.Bytes.Length, ct);
                            await RequestAsync<bool>("receive-chunk", new TransferFileRequest(manifest.Id, entry.RelativePath, offset, chunk.Bytes, chunk.Sha256), ct);
                            offset += chunk.Bytes.Length; sent += chunk.Bytes.Length; progress?.Report(new(manifest.Id, name, sent, manifest.TotalBytes, "sending"));
                        }
                    }
                    var receipt = await RequestAsync<TransferReceipt>("receive-complete", manifest.Id, ct);
                    progress?.Report(new(manifest.Id, name, manifest.TotalBytes, manifest.TotalBytes, "complete")); return receipt;
                }
                catch (Exception error) when (attempt < 2 && !ct.IsCancellationRequested && error is SocketException or EndOfStreamException or InvalidDataException)
                {
                    progress?.Report(new(manifest.Id, name, sent, manifest.TotalBytes, "reconnecting")); await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), ct);
                }
            }
        }
        catch (Exception error)
        {
            progress?.Report(new(manifest.Id, name, sent, manifest.TotalBytes, ct.IsCancellationRequested ? "paused" : "failed", error.Message)); throw;
        }
    }
    public async Task<IReadOnlyList<TransferReceipt>> ReceivePendingAsync(string receiveDirectory, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        await receiveGate.WaitAsync(ct);
        try
        {
            var status = await GetStatusAsync(ct); var receipts = new List<TransferReceipt>();
            IOException? firstError = null;
            foreach (var manifest in status.PendingTransfers)
            {
                try { receipts.Add(await DownloadAsync(manifest, receiveDirectory, progress, ct)); }
                catch (IOException error) when (!ct.IsCancellationRequested) { firstError ??= error; }
            }
            if (receipts.Count == 0 && firstError is not null) throw firstError;
            return receipts;
        }
        finally { receiveGate.Release(); }
    }
    public async Task<TransferReceipt> DownloadSharedAsync(string rootId, string relativePath, string receiveDirectory, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        await receiveGate.WaitAsync(ct);
        try { var manifest = await RequestAsync<TransferManifest>("shared-offer", new SharedRequest(rootId, relativePath), ct); return await DownloadAsync(manifest, receiveDirectory, progress, ct); }
        finally { receiveGate.Release(); }
    }
    private async Task<TransferReceipt> DownloadAsync(TransferManifest manifest, string directory, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        await transferGate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await DownloadCoreAsync(manifest, directory, progress, ct); }
                catch (InvalidDataException) when (attempt < 1 && !ct.IsCancellationRequested) { }
            }
        }
        finally { transferGate.Release(); }
    }
    private async Task<TransferReceipt> DownloadCoreAsync(TransferManifest manifest, string directory, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var receiver = new TransferReceiver(directory); var status = await receiver.BeginAsync(manifest, ct); var name = manifest.Entries[0].RelativePath.Split('/')[0]; var received = status.Files.Sum(x => x.Offset);
        try
        {
            if (status.Receipt is not null) { await RequestAsync<bool>("outgoing-complete", manifest.Id, ct); return status.Receipt; }
            foreach (var cursor in status.Files)
            {
                var entry = manifest.Entries.Single(x => x.RelativePath == cursor.RelativePath); var offset = cursor.Offset;
                while (offset < entry.Length)
                {
                    ct.ThrowIfCancellationRequested(); var chunk = await RequestAsync<FileChunk>("outgoing-chunk", new TransferFileRequest(manifest.Id, cursor.RelativePath, offset), ct);
                    await receiver.WriteChunkAsync(manifest.Id, cursor.RelativePath, offset, chunk.Bytes, chunk.Sha256, ct);
                    offset += chunk.Bytes.Length; received += chunk.Bytes.Length; progress?.Report(new(manifest.Id, name, received, manifest.TotalBytes, "receiving"));
                }
            }
            var receipt = await receiver.CompleteAsync(manifest.Id, ct); await RequestAsync<bool>("outgoing-complete", manifest.Id, ct);
            progress?.Report(new(manifest.Id, name, manifest.TotalBytes, manifest.TotalBytes, "complete")); return receipt;
        }
        catch (Exception error) { progress?.Report(new(manifest.Id, name, received, manifest.TotalBytes, ct.IsCancellationRequested ? "paused" : "failed", error.Message)); throw; }
    }
    private async Task<T> RequestAsync<T>(string action, object body, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var client = new TcpClient { NoDelay = true }; await client.ConnectAsync(bundle.Host, port, deadline.Token);
        await using var secure = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null && DeskLinkFingerprint.Matches(bundle.CertificateSha256, Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()))));
        await secure.AuthenticateAsClientAsync(new() { TargetHost = "nian-desklink", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, EncryptionPolicy = EncryptionPolicy.RequireEncryption }, deadline.Token);
        var id = Guid.NewGuid().ToString("N"); await CollaborationProtocol.WriteAsync(secure, new CollaborationRequest(1, id, bundle.Token, action, CollaborationProtocol.Element(body)), deadline.Token);
        var response = await CollaborationProtocol.ReadAsync<CollaborationResponse>(secure, deadline.Token);
        if (response.Version != 1 || response.Id != id) throw new InvalidDataException("Data response did not match this request.");
        if (!response.Ok)
        {
            var message = LocalizeRemoteError(response.Error);
            if (response.Code == "unauthorized") throw new UnauthorizedAccessException(message);
            if (response.Code == "denied") throw new UnauthorizedAccessException(message);
            if (response.Code == "unsupported") throw new NotSupportedException(message);
            if (response.Code == "checksum") throw new InvalidDataException(message);
            if (response.Code == "source-changed") throw new TransferSourceUnavailableException(message ?? "Source file is no longer available.");
            throw new IOException(message ?? "File collaboration request failed.");
        }
        return response.Data.ValueKind == JsonValueKind.Null ? default! : CollaborationProtocol.Parse<T>(response.Data);
    }
    internal static string? LocalizeRemoteError(string? value) => value == "Choose a Windows receive folder before sending files."
        ? "请先在 Windows 互联设置中选择收件文件夹，然后重试。" : value;
    public ValueTask DisposeAsync() { disposed = true; livePreview?.Stop(); return ValueTask.CompletedTask; }
}
