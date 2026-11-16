using Nian.Input.Windows;

namespace DesktopToolbox.Windows.DeskSwitch.Collaboration.LivePreview;

internal static class WindowsLiveCapturePolicy
{
    public static string? Failure(bool interactive, WindowsDesktopSourceGeometry? before, WindowsDesktopSourceGeometry? after)
    {
        if (!interactive) return "locked";
        if (before is null || before != after || !Physical(before)) return "display-unavailable";
        return null;
    }
    private static bool Physical(WindowsDesktopSourceGeometry value) => value.ToPreviewGeometry(1, 1).IsValid &&
        value.Left == Math.Truncate(value.Left) && value.Top == Math.Truncate(value.Top) &&
        value.Width == Math.Truncate(value.Width) && value.Height == Math.Truncate(value.Height) &&
        value.Width <= 32768 && value.Height <= 32768;
}
