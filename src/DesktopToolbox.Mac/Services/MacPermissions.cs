using System.Diagnostics;

namespace DesktopToolbox.Mac.Services;

public static class MacPermissions
{
    public static void OpenAccessibility() => Open("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
    public static void OpenScreenRecording() => Open("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture");
    public static void OpenInputMonitoring() => Open("x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent");
    private static void Open(string uri)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("请在 Mac 上打开系统权限设置。");
        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        start.ArgumentList.Add(uri);
        using var process = Process.Start(start) ?? throw new IOException("无法打开系统设置，请手动打开隐私与安全性。");
    }
}
