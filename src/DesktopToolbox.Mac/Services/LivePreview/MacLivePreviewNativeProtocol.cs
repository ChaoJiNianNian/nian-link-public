using System.Buffers.Binary;
using System.Text;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;

namespace DesktopToolbox.Mac.Services.LivePreview;

internal static class MacLivePreviewNativeProtocol
{
    internal const int HeaderBytes=40;
    private static readonly HashSet<string> Failures=new(StringComparer.Ordinal)
    {"permission","locked","source-unavailable","source-changed","capture-failed","unsupported","stopped"};

    internal static async ValueTask<LivePreviewCapturePacket?> ReadAsync(Stream source,CancellationToken token=default)
    {
        var header=new byte[HeaderBytes];
        if(await source.ReadAsync(header.AsMemory(0,1),token).ConfigureAwait(false)==0)return null;
        await source.ReadExactlyAsync(header.AsMemory(1),token).ConfigureAwait(false);
        if(!header.AsSpan(0,4).SequenceEqual("NLMC"u8)||header[4]!=1||header[6]!=0||header[7]!=0||
           BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(36))!=0)
            throw new InvalidDataException("Mac实时媒体包头无效。");
        var kind=(LivePreviewCaptureKind)header[5];
        var sequence=BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
        var milliseconds=BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
        var width=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24));
        var height=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28));
        var length=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(32));
        if(sequence<=0||length<0||length>LivePreviewCaptureOptions.MaximumFrameBytes)
            throw new InvalidDataException("Mac实时媒体包超出限制。");
        if(kind==LivePreviewCaptureKind.Image)
        {
            if(width is <1 or >1920||height is <1 or >1080||length<4||milliseconds<=0)
                throw new InvalidDataException("Mac实时画面信息无效。");
            DateTimeOffset captured;
            try{captured=DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);}
            catch(ArgumentOutOfRangeException error){throw new InvalidDataException("Mac实时画面时间无效。",error);}
            var jpeg=new byte[length];await source.ReadExactlyAsync(jpeg,token).ConfigureAwait(false);
            // The paired network layer performs the complete JPEG dimensions check.
            if(jpeg[0]!=255||jpeg[1]!=216||jpeg[^2]!=255||jpeg[^1]!=217)
                throw new InvalidDataException("Mac实时媒体载荷不是JPEG。");
            return new(){Kind=kind,Sequence=sequence,CapturedAtUtc=captured,Width=width,Height=height,JpegBytes=jpeg};
        }
        if(milliseconds!=0||width!=0||height!=0)throw new InvalidDataException("Mac实时状态不能伪造图像信息。");
        if(kind==LivePreviewCaptureKind.HealthyIdle&&length==0)return new(){Kind=kind,Sequence=sequence};
        if(kind!=LivePreviewCaptureKind.Unavailable||length is <1 or >64)
            throw new InvalidDataException("Mac实时媒体状态无效。");
        var codeBytes=new byte[length];await source.ReadExactlyAsync(codeBytes,token).ConfigureAwait(false);
        if(codeBytes.Any(value=>value>127))throw new InvalidDataException("Mac实时媒体失败代码无效。");
        var code=Encoding.ASCII.GetString(codeBytes);
        if(!Failures.Contains(code))throw new InvalidDataException("Mac实时媒体失败代码未知。");
        return new(){Kind=kind,Sequence=sequence,FailureCode=code};
    }
}
