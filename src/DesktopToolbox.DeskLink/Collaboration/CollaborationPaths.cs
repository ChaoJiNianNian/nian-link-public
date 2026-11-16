using System.Security.Cryptography;
namespace DesktopToolbox.DeskLink.Collaboration;

internal static class CollaborationPaths
{
    public const int ChunkBytes = 256 * 1024;
    public static void ValidateRelative(string relative, bool allowEmpty = false)
    {
        if (allowEmpty && relative is not null && relative.Length == 0) return;
        if (string.IsNullOrEmpty(relative) || relative.Length > 4096 || relative.Contains('\\') || Path.IsPathRooted(relative))
            throw new ArgumentException("Invalid relative file path.");
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c))) throw new ArgumentException("Unsafe file name.");
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9')
                throw new ArgumentException("Reserved file name.");
        }
        if (relative.Split('/')[0].Equals(".desklink-partials", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Reserved receive directory.");
    }
    public static string Resolve(string root, string relative = "")
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute local directory is required.");
        ValidateRelative(relative, true);
        var fullRoot = Path.GetFullPath(root);
        var anchor = Path.GetPathRoot(fullRoot)!;
        var current = anchor; RejectLink(current);
        foreach (var segment in fullRoot[anchor.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment); RejectLink(current);
        }
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries)) { current = Path.Combine(current, segment); RejectLink(current); }
        return current;
    }
    public static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Symbolic links and directory junctions are not shared or transferred.");
    }
    public static bool IsHash(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static async Task<string> HashFileAsync(string path, long limit = long.MaxValue, CancellationToken ct = default)
    {
        RejectLink(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkBytes];
        while (limit > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit)), ct);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read); limit -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
