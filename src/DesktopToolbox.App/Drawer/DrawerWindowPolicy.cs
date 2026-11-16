namespace DesktopToolbox.App.Drawer;

internal readonly record struct DrawerArea(int Left, int Top, int Width, int Height)
{
    public int Right => checked(Left + Width);
    public int Bottom => checked(Top + Height);
}
internal readonly record struct DrawerForeground(bool Known, long Handle, int ProcessId, string? Executable, bool Fullscreen);

internal readonly record struct DrawerDockPosition(string Side, double Offset);

internal static class DrawerWindowPolicy
{
    public static DrawerArea Place(DrawerArea work, double dpi, bool expanded) => Place(work, dpi, expanded, "right", .5);
    public static DrawerArea Place(DrawerArea work, double dpi, bool expanded, string side, double offset)
    {
        if (work.Width <= 0 || work.Height <= 0 || !double.IsFinite(dpi) || dpi < 48 || dpi > 768
            || side is not ("left" or "right") || !double.IsFinite(offset) || offset < 0 || offset > 1)
            throw new ArgumentException("显示器工作区、缩放或贴边位置无效。");
        var scale = dpi / 96d;
        int Pixels(double value) => Math.Max(1, (int)Math.Round(value * scale));
        var margin = expanded ? Math.Min(Pixels(16), Math.Max(0, (work.Width - 1) / 4)) : 0;
        var vertical = expanded ? Math.Min(Pixels(20), Math.Max(0, (work.Height - 1) / 4)) : 0;
        var width = Math.Min(Pixels(expanded ? 402 : 48), work.Width - margin * 2);
        var height = Math.Min(Pixels(expanded ? 690 : 128), work.Height - vertical * 2);
        var edgeHeight = Math.Min(Pixels(128), work.Height);
        var edgeTop = work.Top + (work.Height - edgeHeight) * offset;
        var top = expanded ? edgeTop + edgeHeight / 2d - height / 2d : edgeTop;
        top = Math.Clamp(top, work.Top + vertical, work.Bottom - vertical - height);
        return new(side == "left" ? work.Left + margin : work.Right - margin - width, (int)Math.Round(top), width, height);
    }
    public static DrawerDockPosition Snap(DrawerArea work, double dpi, DrawerArea current)
    {
        var edge = Place(work, dpi, false);
        var side = current.Left + current.Width / 2d < work.Left + work.Width / 2d ? "left" : "right";
        var top = current.Top + (current.Height - edge.Height) / 2d;
        var offset = work.Height == edge.Height ? .5 : Math.Clamp((top - work.Top) / (work.Height - edge.Height), 0, 1);
        return new(side, offset);
    }
    public static (int X, int Y) PointerAnchor(DrawerArea bounds, double logicalWidth, double logicalHeight, double x, double y)
    {
        if (!double.IsFinite(logicalWidth) || !double.IsFinite(logicalHeight) || logicalWidth <= 0 || logicalHeight <= 0
            || !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > logicalWidth || y > logicalHeight)
            throw new ArgumentException("拖动起点无效。");
        return (checked(bounds.Left + (int)Math.Round(x * bounds.Width / logicalWidth)), checked(bounds.Top + (int)Math.Round(y * bounds.Height / logicalHeight)));
    }
    public static bool IsDesktopClass(string windowClass) => windowClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "SHELLDLL_DefView";
    public static bool KeepVisible(bool started, bool quiet, bool frozen, bool stopping) => started && !quiet && !frozen && !stopping;

    public static bool IsQuiet(DrawerForeground foreground, int ownProcess, bool fullscreen, IReadOnlyList<string> quietApps, bool previous)
    {
        if (!foreground.Known || foreground.Handle == 0) return previous;
        if (foreground.ProcessId == ownProcess) return false;
        return fullscreen && foreground.Fullscreen
            || quietApps.Any(app => string.Equals(app, foreground.Executable, StringComparison.OrdinalIgnoreCase));
    }

    public static bool AllowsSurfaceEvent(string name) => name.StartsWith("drawer.", StringComparison.Ordinal) || name is "archive.state" or "archive.operation" or "archive.error" or "archive.pending";

    public static bool AllowsSurfaceMethod(string method) => method.StartsWith("drawer.", StringComparison.Ordinal)
        || method == "window.close";
}
