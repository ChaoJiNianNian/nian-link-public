using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace NianLink.MultiDevice;

// These are OS-reported volume identities, not protection from concurrent remapping.
internal readonly record struct DiscoveryVolumeEvidence(bool Present, bool Fixed, bool Ready, string? Device, string? Volume);

internal static class DiscoveryVolumePolicy
{
    internal static bool Allowed(string drive, DiscoveryVolumeEvidence target, DiscoveryVolumeEvidence system, DiscoveryVolumeEvidence mechanical)
    {
        if (drive is not ("D:" or "H:") || !target.Present || !target.Fixed || !target.Ready ||
            !IsWholeVolumeDevice(target.Device) || string.IsNullOrWhiteSpace(target.Volume)) return false;
        foreach (var other in new[] { system, mechanical })
        {
            if (!other.Present) continue;
            if (string.IsNullOrWhiteSpace(other.Volume) || string.Equals(target.Volume, other.Volume, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target.Device, other.Device, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool IsWholeVolumeDevice(string? device)
    {
        const string prefix = @"\Device\HarddiskVolume";
        if (device is null || !device.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || device.Length == prefix.Length) return false;
        return device.Substring(prefix.Length).All(c => c >= '0' && c <= '9');
    }

    internal static bool IsAllowedCurrentVolume(string drive)
    {
        if (!OperatingSystem.IsWindows() || drive is not ("D:" or "H:")) return false;
        try { return Allowed(drive, Query(drive), Query("C:"), Query("F:")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static DiscoveryVolumeEvidence Query(string drive)
    {
        var root = drive + @"\";
        var type = GetDriveTypeW(root);
        if (type == 1) return new(false, false, false, null, null);
        if (type != 3) return new(true, false, false, null, null);
        var device = new StringBuilder(4096);
        if (QueryDosDeviceW(drive, device, (uint)device.Capacity) == 0) return new(true, true, false, null, null);
        var volume = new StringBuilder(128);
        if (!GetVolumeNameForVolumeMountPointW(root, volume, (uint)volume.Capacity)) return new(true, true, false, device.ToString(), null);
        return new(true, true, new DriveInfo(root).IsReady, device.ToString(), volume.ToString());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string device, StringBuilder target, uint capacity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint length);
}
