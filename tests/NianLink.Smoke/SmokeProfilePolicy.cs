using System.IO;

namespace NianLink.Smoke;

internal static class SmokeProfilePolicy
{
    public static string Validate(string path, string allowedParent)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute isolated profile is required.");
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) throw new ArgumentException("Device paths are not allowed.");
        var parts = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or "..")) throw new ArgumentException("Relative path components are not allowed.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedParent));
        var leaf = Path.GetFileName(full);
        if (!Guid.TryParseExact(leaf, "N", out _) || !string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Profile must be a fresh GUID directory immediately under the dedicated smoke parent.");
        return full;
    }
}
