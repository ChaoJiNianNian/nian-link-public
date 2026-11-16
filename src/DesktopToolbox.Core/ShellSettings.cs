using System.Text.Json;

namespace DesktopToolbox.Core;

public sealed record ShellSettings
{
    public string Theme { get; init; } = "system";
    public bool ReduceTransparency { get; init; }
    public string[] Favorites { get; init; } = ["sort", "search", "notes"];
    public bool CloseToTray { get; init; } = true;
    public bool StartWithWindows { get; init; }

    public ShellSettings Apply(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("设置内容必须是对象。");
        var next = this;
        try
        {
            foreach (var property in patch.EnumerateObject())
            {
                next = property.Name switch
                {
                    "theme" => next with { Theme = property.Value.GetString() ?? "" },
                    "reduceTransparency" => next with { ReduceTransparency = property.Value.GetBoolean() },
                    "favorites" => next with { Favorites = property.Value.Deserialize<string[]>() ?? throw new ArgumentException("常用工具设置不能为空。") },
                    "closeToTray" => next with { CloseToTray = property.Value.GetBoolean() },
                    "startWithWindows" => next with { StartWithWindows = property.Value.GetBoolean() },
                    _ => throw new ArgumentException("设置项无效。")
                };
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            throw new ArgumentException("设置值的格式无效。", exception);
        }
        string[] allowed = ["sort", "search", "notes", "storage", "startup", "resource", "uninstall", "clipboard", "workspace", "lock", "agent", "desk-switch"];
        if (next.Theme is not ("system" or "light" or "dark") || next.Favorites is null || next.Favorites.Length > allowed.Length || next.Favorites.Any(item => !allowed.Contains(item)))
            throw new ArgumentException("外观或常用工具设置无效。");
        return next with { Favorites = next.Favorites.Distinct().ToArray() };
    }
}
