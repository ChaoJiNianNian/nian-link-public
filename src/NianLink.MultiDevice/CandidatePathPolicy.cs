using System;
using System.IO;
using System.Linq;

namespace NianLink.MultiDevice;

internal static class CandidatePathPolicy
{
    public static bool IsIsolated(string candidate, string formal)
    {
        if (!Path.IsPathFullyQualified(candidate) || !Path.IsPathFullyQualified(formal)) return false;
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(formal));
        return !Overlaps(a, b) && !Overlaps(b, a);
    }

    public static bool IsDiscoveryLaunch(string[] args)
    {
        try
        {
            if (args.Length != 3 || args[2] != "--discovery-portable" || !Path.IsPathFullyQualified(args[1]) || !IsDiscoveryProfile(args[0])) return false;
            var marker = Path.Combine(AppContext.BaseDirectory, "discovery-portable-id.txt");
            if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0) return false;
            return MatchesPackageId(args[0], File.ReadAllText(marker));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    internal static bool MatchesPackageId(string profile, string id) => id.Length == 32 &&
        id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f') &&
        string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(profile)), "test-" + id, StringComparison.Ordinal);

    internal static bool IsDiscoveryLaunchUnderRoot(string[] args, string root) =>
        args.Length == 3 && args[2] == "--discovery-portable" && Path.IsPathFullyQualified(args[1]) && IsDiscoveryProfileUnderRoot(args[0], root);

    public static bool IsDiscoveryProfile(string candidate)
    {
        try
        {
            if (!Path.IsPathFullyQualified(candidate)) return false;
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(candidate));
            if (driveRoot is null || driveRoot.Length != 3) return false;
            var drive = driveRoot.Substring(0, 2).ToUpperInvariant();
            if (!DiscoveryVolumePolicy.IsAllowedCurrentVolume(drive)) return false;
            var root = Path.Combine(driveRoot, "Projects", "nian-link", "test-profiles", "discovery-portable");
            return IsDiscoveryProfileUnderRoot(candidate, root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    public static bool ClaimDiscoveryProfile(string profile)
    {
        if (!IsDiscoveryProfile(profile)) return false;
        var root = Path.GetDirectoryName(Path.GetFullPath(profile));
        if (root is null) return false;
        var id = Path.GetFileName(profile).Substring(5);
        return ClaimDiscoveryProfileUnderRoot(profile, root, id);
    }

    internal static bool ClaimDiscoveryProfileUnderRoot(string profile, string root, string id) => ClaimDiscoveryProfileCore(profile, root, id, null);

#if DISCOVERY_PROFILE_TEST
    internal static bool ClaimDiscoveryProfileForTest(string profile, string root, string id, Action beforeCreate) => ClaimDiscoveryProfileCore(profile, root, id, beforeCreate);
#endif

    private static bool ClaimDiscoveryProfileCore(string profile, string root, string id, Action? beforeCreate)
    {
        try
        {
            if (!MatchesPackageId(profile, id) || !IsDiscoveryProfileUnderRoot(profile, root)) return false;
            var owner = Path.Combine(profile, ".discovery-owner");
            if (Directory.Exists(profile))
            {
                if (!File.Exists(owner) || (File.GetAttributes(owner) & FileAttributes.ReparsePoint) != 0) return false;
                return string.Equals(File.ReadAllText(owner), id, StringComparison.Ordinal);
            }
            if (!OperatingSystem.IsWindows()) return false;
            Directory.CreateDirectory(root);
            if (!IsDiscoveryProfileUnderRoot(profile, root)) return false;
            beforeCreate?.Invoke();
            if (!CreateDirectoryW(profile, IntPtr.Zero)) return false;
            if (!IsDiscoveryProfileUnderRoot(profile, root)) return false;
            using var file = new FileStream(owner, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false));
            writer.Write(id);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    internal static bool IsDiscoveryProfileUnderRoot(string candidate, string rootPath)
    {
        try
        {
            if (!Path.IsPathFullyQualified(candidate) || !Path.IsPathFullyQualified(rootPath)) return false;
            var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            var name = Path.GetFileName(profile);
            if (!string.Equals(Path.GetDirectoryName(profile), root, StringComparison.OrdinalIgnoreCase) ||
                name.Length != 37 || !name.StartsWith("test-", StringComparison.Ordinal) ||
                name.Substring(5).Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))) return false;
            for (string? current = profile; current is not null; current = Path.GetDirectoryName(current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    private static bool Overlaps(string candidate, string parent) =>
        string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
