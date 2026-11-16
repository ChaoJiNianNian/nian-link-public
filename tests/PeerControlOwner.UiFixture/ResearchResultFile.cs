using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// Test-only result sink: never open an existing file or leave the configured work root.
internal static class ResearchResultFile
{
    public static StreamWriter? Create(Assembly assembly, string resultPath)
    {
        try
        {
            var values = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(attribute => attribute.Key == "NianResearchWorkRoot").ToArray();
            if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0].Value))
                throw new InvalidOperationException("Missing isolated research work root.");
            return CreateWithin(values[0].Value!, resultPath);
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("Result file refused: " + error.Message);
            return null;
        }
    }

    internal static StreamWriter CreateWithin(string workRoot, string resultPath)
    {
        if (!Path.IsPathFullyQualified(workRoot) || !Path.IsPathFullyQualified(resultPath))
            throw new ArgumentException("Absolute paths are required.");
        var root = Path.GetFullPath(workRoot);
        var path = Path.GetFullPath(resultPath);
        if (Path.GetFileName(path).Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("Result filename must not contain an alternate data stream.");
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || Path.IsPathFullyQualified(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Result must be below the isolated work directory.");
        var parent = Path.GetDirectoryName(path) ?? throw new ArgumentException("Missing result parent.");
        if (!Directory.Exists(root) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("The isolated work directory and result parent must already exist.");
        for (string? current = parent; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked result path is not allowed.");
        // CreateNew atomically refuses any pre-existing file, including a dangling link.
        return new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false));
    }
}
