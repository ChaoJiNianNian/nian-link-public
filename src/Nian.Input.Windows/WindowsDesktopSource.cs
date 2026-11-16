namespace Nian.Input.Windows;

/// <summary>Read-only source identity. Installs no hooks, notifications, timers or input receiver.</summary>
public static class WindowsDesktopSource
{
    public static WindowsDesktopSourceGeometry? ReadCurrentPreviewSource()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var displays = Win32DesktopInputPlatform.ReadNativeDisplays();
            if (displays.Count is < 1 or > 64 || displays.Any(d => !d.IsValid) || displays.Count(d => d.Primary) != 1 ||
                displays.Select(d => d.SourceId).Distinct(StringComparer.Ordinal).Count() != displays.Count) return null;
            return displays.Single(d => d.Primary).Source;
        }
        catch { return null; }
    }
}
