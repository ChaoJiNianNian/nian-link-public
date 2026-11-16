using System.Security.Cryptography;
using System.Text;

namespace DesktopToolbox.DeskLink;

/// <summary>Stable public name for a device, generated once in its private profile.</summary>
public static class LocalDeviceName
{
    public static async Task<string> OpenAsync(string directory, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An absolute profile path is required.", nameof(directory));
        directory = Path.GetFullPath(directory);
        RejectLinks(directory);
        PrivateFiles.EnsureDirectory(directory);
        RejectLinks(directory);
        var path = Path.Combine(directory, "device-name.txt");
        RejectLinks(path);
        if (!File.Exists(path))
        {
            var name = "PC-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                RejectLinks(temporary);
                await File.WriteAllTextAsync(temporary, name, new UTF8Encoding(false, true), ct);
                RejectLinks(path);
                try { File.Move(temporary, path, false); }
                catch (IOException) when (File.Exists(path)) { /* Another process created the identity. */ }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        RejectLinks(path);
        var info = new FileInfo(path);
        if (info.Length != 15) throw new InvalidDataException("The saved device name is damaged; the file was retained.");
        var saved = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), ct);
        if (saved.Length != 15 || !saved.StartsWith("PC-", StringComparison.Ordinal) ||
            saved.AsSpan(3).ContainsAnyExcept("0123456789ABCDEF"))
            throw new InvalidDataException("The saved device name is damaged; the file was retained.");
        return saved;
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Device identity path cannot contain links."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
