namespace DesktopToolbox.DeskLink.Collaboration;

internal sealed record TransferSource(string Path, long Length, DateTime LastWriteUtc, string Root, string RelativePath);
internal sealed record PreparedTransfer(TransferManifest Manifest, IReadOnlyDictionary<string, TransferSource> Sources)
{
    public string? SharedRootId { get; init; }
    public string? SharedRelativePath { get; init; }
    public string? SharedSourcePath { get; init; }
    public static async Task<PreparedTransfer> CreateAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var entries = new List<TransferEntry>(); var sources = new Dictionary<string, TransferSource>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Choose absolute local file paths.");
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var name = Path.GetFileName(full); CollaborationPaths.ValidateRelative(name);
            if (!names.Add(name)) throw new ArgumentException("Files with the same top-level name must be sent separately.");
            var sourceRoot = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
            await AddAsync(full, name, sourceRoot);
        }
        if (entries.Count == 0) throw new ArgumentException("Choose files to send.");
        var manifest = new TransferManifest(Guid.NewGuid().ToString("N"), entries);
        TransferReceiver.ValidateManifest(manifest);
        return new(manifest, sources);

        async Task AddAsync(string path, string relative, string sourceRoot)
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(path).Equals(".desklink-partials", StringComparison.OrdinalIgnoreCase)) return;
            CollaborationPaths.ValidateRelative(relative); var withinRoot = Path.GetRelativePath(sourceRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            CollaborationPaths.Resolve(sourceRoot, withinRoot == "." ? "" : withinRoot);
            if (entries.Count >= 10000) throw new ArgumentException("Choose at most 10000 files and folders per transfer.");
            if (Directory.Exists(path))
            {
                entries.Add(new(relative, true, 0, ""));
                foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal)) await AddAsync(child, relative + "/" + Path.GetFileName(child), sourceRoot);
            }
            else
            {
                var info = new FileInfo(path);
                if (!info.Exists) throw new FileNotFoundException("The chosen file no longer exists.", path);
                if (info.Length > 1024L * 1024 * 1024 * 1024) throw new ArgumentException("File exceeds the transfer size limit.");
                var source = new TransferSource(path, info.Length, info.LastWriteTimeUtc, sourceRoot, Path.GetRelativePath(sourceRoot, path).Replace(Path.DirectorySeparatorChar, '/'));
                var hash = await CollaborationPaths.HashFileAsync(path, ct: ct);
                info.Refresh(); if (source.Length != info.Length || source.LastWriteUtc != info.LastWriteTimeUtc) throw new IOException("File changed while preparing the transfer.");
                entries.Add(new(relative, false, info.Length, hash)); sources.Add(relative, source);
            }
        }
    }
    public async Task<FileChunk> ReadChunkAsync(string relative, long offset, CancellationToken ct = default)
    {
        if (!Sources.TryGetValue(relative, out var source)) throw new ArgumentException("Unknown offered file.");
        CollaborationPaths.Resolve(source.Root, source.RelativePath);
        var info = new FileInfo(source.Path);
        if (!info.Exists || info.Length != source.Length || info.LastWriteTimeUtc != source.LastWriteUtc) throw new TransferSourceUnavailableException("Source file changed or is missing. Choose it again to send a new copy.");
        if (offset < 0 || offset >= source.Length) throw new ArgumentException("Invalid file chunk offset.");
        var bytes = new byte[(int)Math.Min(CollaborationPaths.ChunkBytes, source.Length - offset)];
        await using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, CollaborationPaths.ChunkBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
        input.Position = offset; await input.ReadExactlyAsync(bytes, ct); return new(bytes, CollaborationPaths.Hash(bytes));
    }
}

internal sealed class TransferSourceUnavailableException(string message) : IOException(message);
