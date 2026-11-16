using DesktopToolbox.Core;

namespace DesktopToolbox.App.DeskLink;

public sealed record MiniWindowPreferences
{
    public bool Visible { get; init; } = true;
    public bool Pinned { get; init; }
    public bool Collapsed { get; init; }
    public string Display { get; init; } = "";
    public string DockSide { get; init; } = "right";
    public double X { get; init; } = .9;
    public double Y { get; init; } = .45;

    public MiniWindowPreferences Normalize() => this with
    {
        DockSide = DockSide is "left" or "right" or "none" ? DockSide : "right",
        Display = Display is { Length: <=256 } ? Display : "",
        X = double.IsFinite(X) ? Math.Clamp(X,0,1) : .9,
        Y = double.IsFinite(Y) ? Math.Clamp(Y,0,1) : .45
    };
}

public sealed class MiniWindowPreferencesStore(JsonFileStore store)
{
    private const string FileName = "desklink-mini-window.json";
    public async Task<MiniWindowPreferences> LoadAsync(CancellationToken token=default)
        => (await store.LoadAsync(FileName,()=>new MiniWindowPreferences(),token)).Normalize();
    public Task SaveAsync(MiniWindowPreferences preferences,CancellationToken token=default)
        => store.SaveAsync(FileName,preferences.Normalize(),token);
}
