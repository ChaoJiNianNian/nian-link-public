using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;

namespace DesktopToolbox.Windows.DeskSwitch.Collaboration.LivePreview;

/// <summary>Owns reusable JPEG settings and a bounded staging stream, returning independent immutable packets.</summary>
internal sealed class GdiLivePreviewJpegEncoder : IDisposable
{
    private static readonly Lazy<ImageCodecInfo> JpegCodec = new(() => ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid));
    private readonly EncoderParameters parameters = new(1);
    private readonly BoundedJpegStream stream = new();
    private bool disposed;
    public GdiLivePreviewJpegEncoder(int quality)
    {
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
    }
    public byte[] Encode(Bitmap bitmap)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(bitmap);
        stream.Reset();
        if (!GdiLivePreviewNative.GdiFlush()) throw new ExternalException("The native capture target did not finish drawing.");
        try { bitmap.Save(stream, JpegCodec.Value, parameters); }
        catch (Exception) when (stream.LimitExceeded) { throw new WindowsLiveCaptureUnavailableException("frame-invalid"); }
        return stream.ToArray(); // Never expose the reusable staging buffer as a published frame.
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { parameters.Dispose(); } finally { stream.Dispose(); }
    }

    private sealed class BoundedJpegStream : MemoryStream
    {
        public bool LimitExceeded { get; private set; }
        public BoundedJpegStream() : base(64 * 1024) { }
        public void Reset() { LimitExceeded = false; SetLength(0); Position = 0; }
        private void Check(long end)
        {
            if (end is < 0 or > LivePreviewCaptureOptions.MaximumFrameBytes)
            { LimitExceeded = true; throw new IOException("The encoded preview exceeded its frame limit."); }
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(checked(Position + count)); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(checked(Position + buffer.Length)); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(checked(Position + 1)); base.WriteByte(value); }
        public override void SetLength(long value) { Check(value); base.SetLength(value); }
    }
}
