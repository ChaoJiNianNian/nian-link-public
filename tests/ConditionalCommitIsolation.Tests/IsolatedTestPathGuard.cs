namespace DesktopToolbox.DeskLink;

// This guard is defense in depth for test helpers, not a general filesystem authorization API.
internal static class IsolatedTestPathGuard
{
    internal static void Validate(string directory, string temporary, string target, string expectedName)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        const string prefix = "nian-live-vault-";
        var name = Path.GetFileName(root);
        var expected = Path.Combine(root, expectedName);
        var temp = Path.GetFullPath(temporary);
        var tempPrefix = expected + ".";
        if (!string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
            !string.Equals(Path.GetFullPath(target), expected, StringComparison.OrdinalIgnoreCase) ||
            !temp.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) || !temp.EndsWith(".tmp", StringComparison.Ordinal) ||
            !Guid.TryParseExact(temp[tempPrefix.Length..^4], "N", out _))
            throw new InvalidOperationException("Live test publish paths must belong to an isolated temporary store.");
        RejectLinks(root); RejectLinks(temp); RejectLinks(expected);
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Live test paths cannot contain links."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
