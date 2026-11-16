using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Services;

/// <summary>Checks the actual Mac window without starting a shared-input core or requesting permissions.</summary>
internal static class MacStartupCheck
{
    public static async Task WriteAsync(string directory, Window window, IMacLinkService service)
    {
        var state = await service.RefreshAsync();
        if (!window.IsVisible || window.Bounds.Width <= 0 || window.Bounds.Height <= 0)
            throw new InvalidOperationException("Mac 窗口没有完成显示。");
        var wirelessSwitch = window.GetVisualDescendants().OfType<ToggleSwitch>().SingleOrDefault(control => control.Name == "WirelessSmoothToggle");
        if (wirelessSwitch is null || !wirelessSwitch.IsEffectivelyVisible || wirelessSwitch.Bounds.Width <= 0 || wirelessSwitch.Bounds.Height <= 0)
            throw new InvalidOperationException("Mac 无线顺畅模式开关没有完成显示。");
        if (state.IsRunning || state.IsConnected)
            throw new InvalidOperationException("隔离启动检查不允许启动键鼠核心。");
        var fields = MacStartupDiagnostics.Create(state, service.GetType().Name, File.Exists);
        fields["version"] = typeof(MacStartupCheck).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        fields["os"] = RuntimeInformation.OSDescription;
        fields["architecture"] = RuntimeInformation.ProcessArchitecture.ToString();
        fields["windowVisible"] = window.IsVisible;
        fields["width"] = window.Bounds.Width; fields["height"] = window.Bounds.Height;
        fields["IsRunning"] = state.IsRunning; fields["IsConnected"] = state.IsConnected;
        fields["ControlConnected"] = state.ControlConnected; fields["HasApprovedPairing"] = state.HasApprovedPairing;
        fields["wirelessSwitchVisible"] = true; fields["wirelessSmoothEnabled"] = wirelessSwitch.IsChecked == true;
        fields["wirelessSwitchEnabled"] = wirelessSwitch.IsEnabled;
        fields["checkedAtUtc"] = DateTimeOffset.UtcNow;
        for (var current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("启动检查目录不能经过文件链接。");
        }
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var report = Path.Combine(directory, "startup-check.json");
        await using (var stream = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, fields, new JsonSerializerOptions { WriteIndented = true });
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(report, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
