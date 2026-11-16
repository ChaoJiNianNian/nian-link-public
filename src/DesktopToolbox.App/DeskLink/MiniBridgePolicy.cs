using System.Text.Json;

namespace DesktopToolbox.App.DeskLink;

/// <summary>The local mini-page has a deliberately narrow capability boundary.</summary>
public static class MiniBridgePolicy
{
    public const string Page="https://nian-collaboration.local/index.html";
    public static bool IsPage(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.Host=="nian-collaboration.local"&&uri.Port==443&&uri.AbsolutePath=="/index.html"&&uri.Query.Length==0&&uri.Fragment.Length==0&&uri.UserInfo.Length==0;
    public static MiniBridgeRequest Parse(string json)
    {
        if(json.Length>65536)throw new ArgumentException("小窗请求过大。");
        using var document=JsonDocument.Parse(json,new JsonDocumentOptions {MaxDepth=12});var value=document.RootElement;
        if(value.ValueKind!=JsonValueKind.Object||value.EnumerateObject().Any(x=>x.Name is not ("id" or "action" or "payload")))throw new ArgumentException("无效的小窗请求。");
        var id=Text(value,"id",32);if(id.Length!=32||!id.All(Uri.IsHexDigit))throw new ArgumentException("无效的请求编号。");
        var action=Text(value,"action",64);var payload=value.GetProperty("payload");if(payload.ValueKind!=JsonValueKind.Object)throw new ArgumentException("无效的请求内容。");
        var allowed=action switch
        {
            "ready" or "preview.open" or "window.collapse" or "window.expand" or "window.close" or "files.choose" or "files.chooseFolder" or "inbox.open" or "shared.stop" or "connection.start" or "connection.stop" or "connection.openSettings"=>Array.Empty<string>(),
            "switch"=>["platform"],"device.select"=>["name"],"device.rendered"=>["name","version"],"preview.setEnabled" or "clipboard.setEnabled"=>["enabled"],"preview.setRefreshSeconds"=>["seconds"],"window.setPinned"=>["pinned"],
            "window.resize"=>["width","height"],"window.drag"=>["surface","anchor"],"files.drop"=>["names"],
            "shared.list" or "shared.preview" or "shared.download"=>["rootId","relativePath"],"directory.choose"=>["kind"],
            _=>throw new ArgumentException("此小窗不支持该操作。")
        };
        if(payload.EnumerateObject().Any(x=>!allowed.Contains(x.Name,StringComparer.Ordinal)))throw new ArgumentException("请求包含未授权的参数。");
        switch(action)
        {
            case "switch":if(Text(payload,"platform",7) is not ("Windows" or "Mac"))throw new ArgumentException("无效的电脑。");break;
            case "device.select":DesktopToolbox.DeskLink.DeskflowProfile.ValidateName(Text(payload,"name",64));break;
            case "device.rendered":DesktopToolbox.DeskLink.DeskflowProfile.ValidateName(Text(payload,"name",64));if(!payload.GetProperty("version").TryGetInt64(out var deviceVersion)||deviceVersion<0)throw new ArgumentException("设备版本无效。");break;
            case "preview.setEnabled":case "clipboard.setEnabled":Boolean(payload,"enabled");break;
            case "preview.setRefreshSeconds":
                if(!payload.GetProperty("seconds").TryGetInt32(out var seconds))throw new ArgumentException("请选择有效的刷新档位。");
                DesktopToolbox.DeskLink.Collaboration.PreviewCaptureCadence.ValidateRefreshSeconds(seconds);break;
            case "window.setPinned":Boolean(payload,"pinned");break;
            case "directory.choose":if(Text(payload,"kind",7) is not ("receive" or "shared"))throw new ArgumentException("无效的目录用途。");break;
            case "window.resize":var width=Number(payload,"width");var height=Number(payload,"height");if(width is not (398 or 83)||height<100||height>2400)throw new ArgumentException("无效的小窗尺寸。");break;
            case "window.drag":if(Text(payload,"surface",10) is not ("header" or "capsule"))throw new ArgumentException("无效的拖动区域。");var anchor=payload.GetProperty("anchor");if(anchor.ValueKind!=JsonValueKind.Object||anchor.EnumerateObject().Any(x=>x.Name is not ("x" or "y"))||Number(anchor,"x") is <0 or >398||Number(anchor,"y") is <0 or >2400)throw new ArgumentException("无效的拖动位置。");break;
            case "files.drop":var names=payload.GetProperty("names");if(names.ValueKind!=JsonValueKind.Array||names.GetArrayLength() is <1 or >256||names.EnumerateArray().Any(x=>x.ValueKind!=JsonValueKind.String||x.GetString()!.Length>512))throw new ArgumentException("一次最多拖入256项。");break;
            case "shared.list":case "shared.preview":case "shared.download":
                if(payload.TryGetProperty("rootId",out var root)&&root.ValueKind!=JsonValueKind.Null)Text(payload,"rootId",128);
                else if(action!="shared.list")throw new ArgumentException("请选择共享目录。");
                Text(payload,"relativePath",4096);break;
        }
        return new(id,action,payload.Clone());
    }
    internal static string Text(JsonElement value,string key,int maximum){var item=value.GetProperty(key);if(item.ValueKind!=JsonValueKind.String||item.GetString()!.Length>maximum)throw new ArgumentException("无效的文本参数。");return item.GetString()!;}
    internal static bool Boolean(JsonElement value,string key){var item=value.GetProperty(key);if(item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("无效的开关参数。");return item.GetBoolean();}
    internal static double Number(JsonElement value,string key){var item=value.GetProperty(key);if(!item.TryGetDouble(out var number)||!double.IsFinite(number))throw new ArgumentException("无效的数值参数。");return number;}
}
public sealed record MiniBridgeRequest(string Id,string Action,JsonElement Payload);
