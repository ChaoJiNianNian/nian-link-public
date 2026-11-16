using Nian.Input.Control;

namespace Nian.Input.Windows;

/// <summary>Physical virtual-desktop pixels, independent of JPEG size and WPF DPI.</summary>
public sealed record WindowsDesktopSourceGeometry(string SourceId, string Revision, double Left, double Top, double Width, double Height)
{
    public PreviewSourceGeometry ToPreviewGeometry(int imageWidth, int imageHeight) =>
        new(SourceId, Revision, Left, Top, Width, Height, imageWidth, imageHeight);
}

internal readonly record struct WindowsDesktopPoint(double X, double Y)
{
    internal bool IsValid => double.IsFinite(X) && double.IsFinite(Y);
}

internal readonly record struct WindowsDesktopBounds(double Left, double Top, double Width, double Height)
{
    internal bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) &&
        double.IsFinite(Height) && Width > 1 && Height > 1 && double.IsFinite(Left + Width) && double.IsFinite(Top + Height);
    internal bool Contains(WindowsDesktopPoint point) => IsValid && point.IsValid && point.X >= Left && point.Y >= Top &&
        point.X <= Left + Width - 1 && point.Y <= Top + Height - 1;
    internal WindowsDesktopPoint Clamp(WindowsDesktopPoint point) => new(
        Math.Clamp(point.X, Left, Left + Width - 1), Math.Clamp(point.Y, Top, Top + Height - 1));
}

internal sealed record WindowsDesktopDisplay(string SourceId, string Revision, double Left, double Top, double Width, double Height, bool Primary)
{
    internal WindowsDesktopBounds Bounds => new(Left, Top, Width, Height);
    internal WindowsDesktopSourceGeometry Source => new(SourceId, Revision, Left, Top, Width, Height);
    internal bool IsValid => Bounds.IsValid && Source.ToPreviewGeometry(1, 1).IsValid;
    internal bool Matches(PreviewSourceGeometry geometry) => SourceId == geometry.SourceId && Revision == geometry.Revision &&
        Left == geometry.Left && Top == geometry.Top && Width == geometry.Width && Height == geometry.Height;
}

internal sealed record WindowsDesktopEnvironment(bool InteractiveDesktop, bool InteractiveSession, bool Awake, bool SafeForeground)
{
    internal static WindowsDesktopEnvironment Unsafe { get; } = new(false, false, false, false);
    internal string Failure => !InteractiveDesktop ? "Windows 已锁屏、正在显示安全桌面或状态不明。" :
        !InteractiveSession ? "当前不是本机用户的活动交互会话。" : !Awake ? "系统正在休眠。" :
        !SafeForeground ? "前台是受保护界面、权限更高的窗口或无法确认归属。" : "";
}

internal sealed record WindowsDesktopAction(InputKind Kind, int Code = 0, int Delta = 0, PreviewSourceGeometry? ExpectedSource = null, bool Preview = false);

/// <summary>Only the native boundary injects; tests substitute this entire boundary.</summary>
internal interface IWindowsDesktopInputPlatform : IDisposable
{
    event Action? SafetyChanged;
    WindowsDesktopEnvironment ReadEnvironment(bool preview = false);
    IReadOnlyList<WindowsDesktopDisplay> ReadDisplays();
    WindowsDesktopPoint ReadPointer();
    bool IsPointerAllowed(WindowsDesktopPoint position, bool preview = false);
    void Post(WindowsDesktopAction action, WindowsDesktopPoint position);
}
