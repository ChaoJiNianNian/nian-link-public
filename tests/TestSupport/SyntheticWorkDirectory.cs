using System.IO;
using System.Reflection;

// Test-only synthetic profiles. The compiled-in work root belongs to this checkout.
internal sealed class SyntheticWorkDirectory : IDisposable
{
    public string Path { get; }
    private SyntheticWorkDirectory(string path) => Path = path;

    public static SyntheticWorkDirectory Create(string prefix)
    {
        var roots = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Key == "NianSyntheticWorkRoot").ToArray();
        if (roots.Length != 1 || string.IsNullOrWhiteSpace(roots[0].Value))
            throw new InvalidOperationException("Missing checkout-local synthetic work root.");
        return CreateUnder(roots[0].Value!, prefix);
    }

    internal static SyntheticWorkDirectory CreateUnder(string workRoot, string prefix)
    {
        if (!System.IO.Path.IsPathFullyQualified(workRoot)) throw new ArgumentException("Work root must be absolute.");
        if (string.IsNullOrEmpty(prefix) || prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid synthetic directory prefix.");
        var root = System.IO.Path.GetFullPath(workRoot);
        CheckPath(root);
        Directory.CreateDirectory(root);
        CheckPath(root);
        var path = System.IO.Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Synthetic directory already exists.");
        Directory.CreateDirectory(path);
        CheckPath(path);
        return new SyntheticWorkDirectory(path);
    }

    public void Dispose()
    {
        CheckPath(Path);
        if (!Directory.Exists(Path)) return;
        CheckTree(Path);
        Directory.Delete(Path, true);
    }

    private static void CheckTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Synthetic test tree contains a link.");
            if ((attributes & FileAttributes.Directory) != 0) CheckTree(entry);
        }
    }

    private static void CheckPath(string path)
    {
        for (var current = System.IO.Path.GetFullPath(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Synthetic test path contains a link.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
