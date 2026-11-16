using System.Buffers.Binary;
using System.Text;
namespace DesktopToolbox.DeskLink.Collaboration.LivePreview;

/// <summary>Authenticated connection's bounded binary media path, independent of business RPC.</summary>
public static class LivePreviewFrameProtocol
{
    public const int HeaderSize = 52;
    public static async ValueTask WriteAsync(Stream stream, Guid streamId, LivePreviewCapturePacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); ArgumentNullException.ThrowIfNull(packet);
        if (streamId == Guid.Empty) throw new ArgumentException("An active stream generation is required.", nameof(streamId));
        LivePreviewFrameValidation.Validate(packet);
        var payload = packet.Kind == LivePreviewCaptureKind.Unavailable ? Encoding.ASCII.GetBytes(packet.FailureCode!) : packet.JpegBytes;
        var header = new byte[HeaderSize]; "NLP1"u8.CopyTo(header); header[4] = 1; header[5] = (byte)packet.Kind;
        streamId.TryWriteBytes(header.AsSpan(8, 16), true, out _);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(24, 8), packet.Sequence);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(32, 8), packet.Kind == LivePreviewCaptureKind.Image ? packet.CapturedAtUtc.ToUnixTimeMilliseconds() : 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(40, 4), packet.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(44, 4), packet.Height);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(48, 4), payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class LivePreviewFrameReader
{
    private readonly Stream stream;
    private readonly Guid expectedStreamId;
    private long lastSequence;
    private bool failed;
    public LivePreviewFrameReader(Stream stream, Guid expectedStreamId)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (expectedStreamId == Guid.Empty) throw new ArgumentException("An active stream generation is required.", nameof(expectedStreamId));
        this.stream = stream; this.expectedStreamId = expectedStreamId;
    }
    public async ValueTask<LivePreviewCapturePacket> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (failed) throw new InvalidDataException("The media stream has already failed.");
        try
        {
            var header = new byte[LivePreviewFrameProtocol.HeaderSize];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, 4).SequenceEqual("NLP1"u8) || header[4] != 1 || header[6] != 0 || header[7] != 0 ||
                new Guid(header.AsSpan(8, 16), true) != expectedStreamId) throw new InvalidDataException("Unexpected media stream header.");
            var kind = (LivePreviewCaptureKind)header[5]; var sequence = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(24, 8));
            var time = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(32, 8));
            var width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(40, 4)); var height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(44, 4));
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(48, 4));
            if (sequence <= lastSequence || sequence <= 0 || length < 0 || length > LivePreviewCaptureOptions.MaximumFrameBytes ||
                kind is not (LivePreviewCaptureKind.Image or LivePreviewCaptureKind.HealthyIdle or LivePreviewCaptureKind.Unavailable)) throw new InvalidDataException("Invalid media frame limits.");
            if (kind == LivePreviewCaptureKind.Image ? width is < 1 or > 1920 || height is < 1 or > 1080 || time <= 0 || length == 0 :
                width != 0 || height != 0 || time != 0 || (kind == LivePreviewCaptureKind.HealthyIdle ? length != 0 : length is < 1 or > 64)) throw new InvalidDataException("Invalid media frame shape.");
            var payload = new byte[length]; if (length > 0) await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            var packet = new LivePreviewCapturePacket
            {
                Kind = kind, Sequence = sequence, CapturedAtUtc = time == 0 ? default : DateTimeOffset.FromUnixTimeMilliseconds(time),
                Width = width, Height = height, JpegBytes = kind == LivePreviewCaptureKind.Image ? payload : [],
                FailureCode = kind == LivePreviewCaptureKind.Unavailable ? Encoding.ASCII.GetString(payload) : null
            };
            try { LivePreviewFrameValidation.Validate(packet); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid media frame body.", error); }
            lastSequence = sequence; return packet;
        }
        catch { failed = true; throw; }
    }
}
