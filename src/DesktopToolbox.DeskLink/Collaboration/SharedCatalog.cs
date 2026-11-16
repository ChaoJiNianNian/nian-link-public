namespace DesktopToolbox.DeskLink.Collaboration;

internal sealed class SharedCatalog
{
    private IReadOnlyList<SharedDirectory> roots = [];
    public SharedCatalog(IReadOnlyList<SharedDirectory> directories) => Update(directories);
    public void Update(IReadOnlyList<SharedDirectory> directories)
    {
        if (directories.Count > 64 || directories.Select(x => x.RootId).Distinct(StringComparer.Ordinal).Count() != directories.Count) throw new ArgumentException("Invalid shared directory list.");
        foreach (var root in directories)
        {
            if (root.RootId.Length is < 1 or > 64 || root.RootId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') || root.Name.Length is < 1 or > 128) throw new ArgumentException("Invalid shared directory identity.");
            CollaborationPaths.Resolve(root.Path);
        }
        Interlocked.Exchange(ref roots, directories.ToArray());
    }
    public SharedListing List(string? rootId, string relative)
    {
        if (rootId is null)
        {
            if (relative != "") throw new ArgumentException("Choose a shared directory first.");
            return new(null, "", roots.Select(x => new SharedItem(x.RootId, "", x.Name, true, 0, Directory.Exists(x.Path) ? Directory.GetLastWriteTimeUtc(x.Path) : DateTime.UnixEpoch)).ToArray());
        }
        var path = Resolve(rootId, relative);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Shared folder is unavailable.");
        var items = new List<SharedItem>();
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            if (items.Count >= 10000) throw new InvalidOperationException("This folder has too many entries. Choose a narrower shared folder.");
            var name = Path.GetFileName(child);
            if (name.Equals(".desklink-partials", StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            try { CollaborationPaths.ValidateRelative(name); } catch (ArgumentException) { continue; }
            var directory = Directory.Exists(child);
            items.Add(new(rootId, relative == "" ? name : relative + "/" + name, name, directory, directory ? 0 : new FileInfo(child).Length, File.GetLastWriteTimeUtc(child)));
        }
        return new(rootId, relative, items.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }
    public string Resolve(string rootId, string relative)
    {
        var root = roots.SingleOrDefault(x => x.RootId == rootId) ?? throw new UnauthorizedAccessException("This folder is not shared.");
        return CollaborationPaths.Resolve(root.Path, relative);
    }
    public async Task<SharedPreview> PreviewAsync(string rootId, string relative, CancellationToken ct = default)
    {
        var path = Resolve(rootId, relative); var info = new FileInfo(path);
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", ".pdf" => "application/pdf",
            ".txt" or ".md" or ".csv" or ".log" or ".json" => "text/plain", _ => throw new NotSupportedException("This format needs saving locally and opening with a suitable application.")
        };
        var maximum = type == "text/plain" ? 65536 : 4 * 1024 * 1024;
        if (type != "text/plain" && info.Length > maximum) throw new NotSupportedException("Save this large file locally to preview it.");
        var bytes = new byte[(int)Math.Min(info.Length, maximum)];
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        await input.ReadExactlyAsync(bytes, ct);
        if (type.StartsWith("image/", StringComparison.Ordinal) && !SharedImageBounds.IsSafe(bytes, type))
            throw new NotSupportedException("图片尺寸过大、包含动画或格式暂不支持，请保存到本机后打开。");
        return new(info.Name, type, bytes, info.Length > maximum);
    }
}
