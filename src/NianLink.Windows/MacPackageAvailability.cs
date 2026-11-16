namespace NianLink.Windows;

internal static class MacPackageAvailability
{
    // This is only a lightweight availability check for the UI. Export still
    // validates the manifest, hashes, paths, and destination before copying.
    internal static bool Present(string applicationDirectory, Func<string, bool> fileExists)
    {
        var root = Path.Combine(applicationDirectory, "MacClient");
        return fileExists(Path.Combine(root, "NianDeskLink-macOS-arm64.zip")) &&
               fileExists(Path.Combine(root, "manifest.json"));
    }
}
