using System;
using System.IO;

namespace NianLink.MultiDevice;

// The release-test profile is separate from both the formal app and the older
// D/H discovery tests. Each package is bound to one randomly named profile.
internal static class BetaPathPolicy
{
    private const string Flag = "--beta-portable";

    private static string ProfileRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NianLinkMultiDeviceBeta", "Profiles");

    internal static bool IsLaunch(string[] args) => IsLaunchUnderRoot(args, ProfileRoot, AppContext.BaseDirectory);
    internal static bool ClaimProfile(string profile) => ClaimProfileUnderRoot(profile, ProfileRoot);
    internal static bool TryResolveLaunch(string[] args, out string[] resolved) =>
        TryResolveUnderRoot(args, ProfileRoot, AppContext.BaseDirectory, out resolved);

    internal static bool TryResolveUnderRoot(string[] args, string profileRoot, string appDirectory, out string[] resolved)
    {
        resolved = Array.Empty<string>();
        try
        {
            if (args.Length != 1 || args[0] != Flag || !Path.IsPathFullyQualified(profileRoot) || !Path.IsPathFullyQualified(appDirectory)) return false;
            var app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
            var marker = Path.Combine(app, "discovery-portable-id.txt");
            if (!File.Exists(marker) || !NoLinks(marker)) return false;
            var id = File.ReadAllText(marker);
            if (id.Length != 32) return false;
            var profile = Path.Combine(profileRoot, "test-" + id);
            var engine = Path.GetFullPath(Path.Combine(app, "..", "Keyboard", "deskflow-core.exe"));
            var candidate = new[] { profile, engine, Flag };
            if (!IsLaunchUnderRoot(candidate, profileRoot, app)) return false;
            resolved = candidate;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    internal static bool IsLaunchUnderRoot(string[] args, string profileRoot, string appDirectory)
    {
        try
        {
            if (args.Length != 3 || args[2] != Flag || !Path.IsPathFullyQualified(appDirectory) ||
                !Path.IsPathFullyQualified(args[1]) || !CandidatePathPolicy.IsDiscoveryProfileUnderRoot(args[0], profileRoot)) return false;
            var app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
            var marker = Path.Combine(app, "discovery-portable-id.txt");
            var engine = Path.GetFullPath(Path.Combine(app, "..", "Keyboard", "deskflow-core.exe"));
            if (!string.Equals(Path.GetFullPath(args[1]), engine, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(marker) || !File.Exists(engine) || !NoLinks(marker) || !NoLinks(engine)) return false;
            return CandidatePathPolicy.MatchesPackageId(args[0], File.ReadAllText(marker));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    internal static bool ClaimProfileUnderRoot(string profile, string root)
    {
        if (!CandidatePathPolicy.IsDiscoveryProfileUnderRoot(profile, root)) return false;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(profile));
        return CandidatePathPolicy.ClaimDiscoveryProfileUnderRoot(profile, root, name.Substring(5));
    }

    private static bool NoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
