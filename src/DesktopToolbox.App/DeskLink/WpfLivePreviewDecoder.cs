using System.Buffers;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;

namespace DesktopToolbox.App.DeskLink;

/// <summary>Decodes and normalizes pixels off the dispatcher; the UI only uploads a bounded buffer.</summary>
internal static class WpfLivePreviewDecoder
{
    internal sealed class Frame(int width,int height,byte[] pixels):IDisposable
    {
        private byte[]? owned=pixels;
        internal int Width=>width;
        internal int Height=>height;
        internal int Stride=>checked(width*4);
        internal byte[] Pixels=>owned??throw new ObjectDisposedException(nameof(Frame));
        public void Dispose()
        {
            var released=Interlocked.Exchange(ref owned,null);
            if(released is not null)ArrayPool<byte>.Shared.Return(released,clearArray:true);
        }
    }

    internal static Task<Frame> DecodeAsync(LivePreviewCapturePacket packet,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream=new MemoryStream(packet.JpegBytes,false);
        var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;
        // StreamSource has no URI cache key. IgnoreImageCache asks WPF to remove
        // that null key and throws before any live frame can be displayed.
        image.CreateOptions=BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource=stream;image.EndInit();image.Freeze();
        if(image.PixelWidth!=packet.Width||image.PixelHeight!=packet.Height)
            throw new InvalidDataException("The live image dimensions do not match its packet.");
        var normalized=new FormatConvertedBitmap(image,PixelFormats.Pbgra32,null,0);normalized.Freeze();
        var stride=checked(packet.Width*4);var pixels=ArrayPool<byte>.Shared.Rent(checked(stride*packet.Height));
        try
        {
            normalized.CopyPixels(pixels,stride,0);token.ThrowIfCancellationRequested();
            return Task.FromResult(new Frame(packet.Width,packet.Height,pixels));
        }
        catch{ArrayPool<byte>.Shared.Return(pixels,clearArray:true);throw;}
    }
}
