using System.Text.Json;
using Nian.Input.Control;

namespace DesktopToolbox.Mac.Services;

public sealed record MacScreenCaptureReply
{
    public string Status { get; init; } = "captureFailed";
    public string? Message { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[]? JpegBytes { get; init; }
    public PreviewSourceGeometry? Geometry { get; init; }
    public bool IsFrame => Status == "frame" && JpegBytes is not null;

    public static MacScreenCaptureReply Parse(string json)
    {
        if (json.Length > 2 * 1024 * 1024) throw new InvalidDataException("屏幕预览回复超过限制。");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        var status = root.GetProperty("status").GetString() ?? "captureFailed";
        var message = root.TryGetProperty("message", out var text) ? text.GetString() : null;
        if (status != "frame") return new() { Status = status, Message = message };
        var width = root.GetProperty("width").GetInt32(); var height = root.GetProperty("height").GetInt32();
        if (width is < 1 or > 1920 || height is < 1 or > 1920) throw new InvalidDataException("屏幕预览尺寸无效。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(root.GetProperty("jpeg").GetString() ?? ""); }
        catch (FormatException error) { throw new InvalidDataException("屏幕预览编码无效。", error); }
        if (bytes.Length is < 4 or > 1048576 || bytes[0] != 255 || bytes[1] != 216 || bytes[^2] != 255 || bytes[^1] != 217) throw new InvalidDataException("屏幕预览不是有效的JPEG。");
        return new() { Status = status, Message = message, Width = width, Height = height, JpegBytes = bytes, Geometry = ReadGeometry(root,width,height) };
    }

    public static PreviewSourceGeometry? ParseSourceGeometry(string json,int imageWidth,int imageHeight)
    {
        if(json.Length>16384)throw new InvalidDataException("屏幕源回复超过限制。");
        using var document=JsonDocument.Parse(json,new JsonDocumentOptions {MaxDepth=8});
        var root=document.RootElement;
        return root.GetProperty("status").GetString()=="sourceGeometry" ? ReadGeometry(root,imageWidth,imageHeight) : null;
    }

    private static PreviewSourceGeometry? ReadGeometry(JsonElement root,int imageWidth,int imageHeight)
    {
        if(!root.TryGetProperty("geometry",out var value)||value.ValueKind==JsonValueKind.Null)return null;
        try
        {
            var geometry=value.Deserialize<PreviewSourceGeometry>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if(geometry?.IsValid!=true||geometry.ImageWidth!=imageWidth||geometry.ImageHeight!=imageHeight)
                throw new InvalidDataException("原生屏幕源与编码画面不一致。");
            return geometry;
        }
        catch(Exception error) when(error is JsonException or NotSupportedException or InvalidOperationException)
        {throw new InvalidDataException("原生屏幕源几何信息无效。",error);}
    }
}
