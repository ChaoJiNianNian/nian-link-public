using System.Text.Json;

namespace DesktopToolbox.Mac.Models;

/// <summary>Bounded, explicit UI protocol. Native file drops are marked only by the trusted WK delegate.</summary>
public sealed record MacWebRequest(string? Id, string Action, JsonElement Payload, bool Native)
{
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "ready", "switch", "preview.setEnabled", "preview.setRefreshSeconds", "preview.open", "clipboard.setEnabled", "window.setPinned",
        "window.collapse", "window.expand", "window.drag", "window.resize", "window.close", "files.choose",
        "files.chooseFolder", "files.drop", "files.pause", "files.retry", "shared.list", "shared.preview", "shared.download",
        "directory.choose", "inbox.open", "connection.openSettings", "connection.start", "connection.stop", "wireless.setEnabled", "wireless.retry"
    };
    public static bool TryParse(string json, out MacWebRequest? request)
    {
        request = null;
        if (json.Length > 1024 * 1024) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("action", out var value) || value.ValueKind != JsonValueKind.String) return false;
            var action = value.GetString()!;
            var native = root.TryGetProperty("native", out value) && value.ValueKind == JsonValueKind.True;
            if (!Actions.Contains(action) && !(native && action is "files.nativeDrop" or "native.loaded" or "native.reload" or "native.error")) return false;
            var id = root.TryGetProperty("id", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (id?.Length > 80 || (!native && string.IsNullOrEmpty(id))) return false;
            var payload = root.TryGetProperty("payload", out value) ? value : JsonSerializer.SerializeToElement(new { });
            if (payload.ValueKind != JsonValueKind.Object) return false;
            request = new(id, action, payload.Clone(), native); return true;
        }
        catch (JsonException) { return false; }
    }
    public static bool IsValidSize(double width, double height) => width is 398 or 83 && double.IsFinite(height) && height is >= 100 and <= 2000;
}
