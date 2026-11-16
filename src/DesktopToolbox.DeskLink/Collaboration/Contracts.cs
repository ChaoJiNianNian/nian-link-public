using Nian.Input.Control;

namespace DesktopToolbox.DeskLink.Collaboration;

public sealed record SharedDirectory(string RootId, string Name, string Path);
public sealed record CollaborationServerOptions
{
    public required string ProfileDirectory { get; init; }
    public string? ReceiveDirectory { get; init; }
    public System.Net.IPAddress BindAddress { get; init; } = System.Net.IPAddress.Loopback;
    public int Port { get; init; } = 24802;
    public IReadOnlyList<SharedDirectory> SharedDirectories { get; init; } = [];
    public int BytesPerSecond { get; init; } = 8 * 1024 * 1024;
    internal string? CertificateFile { get; init; }
    internal Func<CancellationToken, Task<byte[]>>? TokenProvider { get; init; }
    internal bool EnableExperimentalDisplay { get; init; }
    internal MultiDeviceCredentialVault? ReverseShareTestVault { get; init; }
    internal string? ReverseShareTestPeer { get; init; }
}
public sealed record TransferEntry(string RelativePath, bool IsDirectory, long Length, string Sha256);
public sealed record TransferManifest(string Id, IReadOnlyList<TransferEntry> Entries)
{
    public long TotalBytes => Entries.Sum(x => x.Length);
}
public sealed record TransferProgress(string Id, string Name, long BytesTransferred, long TotalBytes, string State, string? Error = null);
public sealed record TransferReceipt(string Id, IReadOnlyList<string> SavedPaths, long TotalBytes);
public sealed record SharedItem(string RootId, string RelativePath, string Name, bool IsDirectory, long Length, DateTimeOffset LastWriteTimeUtc);
public sealed record SharedListing(string? RootId, string RelativePath, IReadOnlyList<SharedItem> Items);
public sealed record SharedPreview(string Name, string ContentType, byte[] Bytes, bool Truncated = false);
/// <summary>Source-owned state, independent of whether a remote viewer currently requests captures.</summary>
public sealed record PreviewSourceStatus(Guid InstanceId, long Revision, string Availability);
/// <summary>JpegBytes is empty whenever State is not live. Never display a stale image after off/error.</summary>
public sealed record PreviewFrame
{
    public string State { get; init; } = "off";
    public string? Error { get; init; }
    public string ContentType { get; init; } = "image/jpeg";
    public int Width { get; init; }
    public int Height { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public byte[] JpegBytes { get; init; } = [];
    /// <summary>Native source coordinates observed around this capture. Absent on legacy view-only frames.</summary>
    public PreviewSourceGeometry? Geometry { get; init; }
    /// <summary>Issued only by the source host after registering this exact JPEG and geometry.</summary>
    public PreviewFrameProof? ControlProof { get; init; }
    public Guid? RequestId { get; init; }
    public PreviewSourceStatus? SourceStatus { get; init; }
    /// <summary>Source-assigned at capture start, monotonic within this request and source instance.</summary>
    public long? CaptureSequence { get; init; }
    public static PreviewFrame Off(string state = "off", string? error = null) => new() { State = state, Error = error };
}
/// <summary>Mac polls this status; CaptureRequested is Windows' renewable five-second demand for Mac capture.</summary>
public sealed record CollaborationStatus(bool CaptureRequested, IReadOnlyList<TransferManifest> PendingTransfers, bool RemoteClipboardEnabled = false)
{
    public int CaptureMaxWidth { get; init; } = 960;
    public int CaptureMaxHeight { get; init; } = 540;
    public int PreviewRefreshSeconds { get; init; } = PreviewCaptureCadence.DefaultRefreshSeconds;
    public bool LivePreviewSupported { get; init; }
    public LivePreview.LivePreviewDemand? RequestedLivePreview { get; init; }
    public bool PassivePreviewGenerationSupported { get; init; }
    public Guid? CaptureRequestId { get; init; }
    public PreviewSourceStatus? LocalPreviewSourceStatus { get; init; }
    public Coordination.MultiPeerDisplayRequest? DisplayRequest { get; init; }
    public bool ReverseShareTestSupported { get; init; }
    public ReverseShareRequest? ReverseShareRequest { get; init; }
    public long ReverseShareTestSequence { get; init; }
}
internal sealed record ReverseShareTestCompletion(Guid Authorization, Guid RequestId, byte[] Data);
internal sealed record DisplayCommandResult(Guid Id, string Status);
internal sealed record ReceiveCursor(string RelativePath, long Offset, string PrefixSha256);
internal sealed record ReceiveStatus(string Id, IReadOnlyList<ReceiveCursor> Files, TransferReceipt? Receipt = null);
internal sealed record FileChunk(byte[] Bytes, string Sha256);

/// <summary>Only the current clipboard value, never history. Exactly one of Text or PngBytes is supplied.</summary>
public sealed record ClipboardPayload(string Id, string Origin, DateTimeOffset CreatedAtUtc, string? Text = null, byte[]? PngBytes = null);

internal sealed record PreviewRequest(bool Enabled, int MaxWidth = 960, int MaxHeight = 540, int PreviewRefreshSeconds = PreviewCaptureCadence.DefaultRefreshSeconds, Guid? RequestId = null);
