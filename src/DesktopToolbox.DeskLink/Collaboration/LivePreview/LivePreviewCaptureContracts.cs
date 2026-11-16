namespace DesktopToolbox.DeskLink.Collaboration.LivePreview;

/// <summary>Temporary operation limits, never persisted as the passive viewing cadence.</summary>
public sealed record LivePreviewCaptureOptions
{
    public int FramesPerSecond { get; init; } = 30;
    public int MaxWidth { get; init; } = 1280;
    public int MaxHeight { get; init; } = 720;
    public int JpegQuality { get; init; } = 70;
    public const int MaximumFrameBytes = 1024 * 1024;
    public void Validate()
    {
        if (FramesPerSecond is < 15 or > 60) throw new ArgumentOutOfRangeException(nameof(FramesPerSecond));
        if (MaxWidth is < 1 or > 1920) throw new ArgumentOutOfRangeException(nameof(MaxWidth));
        if (MaxHeight is < 1 or > 1080) throw new ArgumentOutOfRangeException(nameof(MaxHeight));
        if (JpegQuality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(JpegQuality));
    }
}

public enum LivePreviewCaptureKind : byte { Image = 1, HealthyIdle = 2, Unavailable = 3 }

/// <summary>Published JPEG bytes are immutable. Idle never re-stamps an old image.</summary>
public sealed record LivePreviewCapturePacket
{
    public required LivePreviewCaptureKind Kind { get; init; }
    public required long Sequence { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] JpegBytes { get; init; } = [];
    public string? FailureCode { get; init; }
}

public interface ILivePreviewCaptureProvider
{
    bool IsSupported { get; }
    IAsyncEnumerable<LivePreviewCapturePacket> CaptureAsync(LivePreviewCaptureOptions options, CancellationToken cancellationToken = default);
}
