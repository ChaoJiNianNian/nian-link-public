using System.Collections.Concurrent;
using System.Text.Json;
namespace DesktopToolbox.DeskLink.Collaboration;

internal sealed class TransferReceiver
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string root;
    private readonly SemaphoreSlim gate;
    public TransferReceiver(string directory)
    {
        root = CollaborationPaths.Resolve(directory);
        gate = Locks.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
    }
    public void Validate(TransferManifest manifest) => ValidateManifest(manifest);
    internal static void ValidateManifest(TransferManifest manifest)
    {
        if (manifest is null || manifest.Id is null || manifest.Entries is null || manifest.Id.Length != 32 || !manifest.Id.All(Uri.IsHexDigit) || manifest.Entries.Count is < 1 or > 10000) throw new ArgumentException("Invalid transfer manifest.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var item in manifest.Entries)
        {
            if (item is null || item.Sha256 is null) throw new ArgumentException("Invalid file entry.");
            CollaborationPaths.ValidateRelative(item.RelativePath);
            var prefix = "";
            foreach (var part in item.RelativePath.Split('/'))
            {
                prefix = prefix == "" ? part : prefix + "/" + part;
                var normalized = prefix.Normalize(System.Text.NormalizationForm.FormC);
                if (prefixes.TryGetValue(normalized, out var original) && original != prefix) throw new ArgumentException("File names conflict in case or Unicode normalization.");
                prefixes[normalized] = prefix;
            }
            if (!paths.Add(item.RelativePath) || item.Length < 0 || item.Length > 1024L * 1024 * 1024 * 1024 ||
                item.IsDirectory && (item.Length != 0 || item.Sha256 != "") || !item.IsDirectory && !CollaborationPaths.IsHash(item.Sha256))
                throw new ArgumentException("Invalid file entry.");
            size = checked(size + item.Length);
            if (size > 1024L * 1024 * 1024 * 1024) throw new ArgumentException("Transfer exceeds the size limit.");
        }
        var filePaths = new HashSet<string>(manifest.Entries.Where(x => !x.IsDirectory).Select(x => x.RelativePath), StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Entries)
        {
            var parent = item.RelativePath;
            while (parent.Contains('/'))
            {
                parent = parent[..parent.LastIndexOf('/')];
                if (filePaths.Contains(parent)) throw new ArgumentException("A file cannot contain another entry.");
            }
        }
    }
    public async Task<ReceiveStatus> BeginAsync(TransferManifest manifest, CancellationToken ct = default)
    {
        Validate(manifest); await gate.WaitAsync(ct);
        try
        {
            var stage = Stage(manifest.Id); Directory.CreateDirectory(stage);
            var manifestPath = Path.Combine(stage, "manifest.json");
            if (File.Exists(manifestPath))
            {
                var existing = await ReadAsync<TransferManifest>(manifestPath, ct);
                if (JsonSerializer.Serialize(existing, JsonLineProtocol.Json) != JsonSerializer.Serialize(manifest, JsonLineProtocol.Json)) throw new InvalidDataException("Transfer identifier belongs to different contents.");
            }
            else await WriteAsync(manifestPath, manifest, ct);
            var receiptPath = Path.Combine(stage, "receipt.json");
            if (File.Exists(receiptPath)) return new(manifest.Id, [], await ReadAsync<TransferReceipt>(receiptPath, ct));
            if (File.Exists(Path.Combine(stage, "commit.json"))) return new(manifest.Id, [], await CompleteCoreAsync(manifest.Id, ct));
            var content = Path.Combine(stage, "content"); Directory.CreateDirectory(content);
            var cursors = new List<ReceiveCursor>();
            foreach (var item in manifest.Entries)
            {
                var target = CollaborationPaths.Resolve(content, item.RelativePath);
                if (item.IsDirectory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) await File.WriteAllBytesAsync(target, [], ct);
                var length = new FileInfo(target).Length;
                var hash = await CollaborationPaths.HashFileAsync(target, ct: ct);
                if (length > item.Length || length == item.Length && !hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    await ResetFileCoreAsync(manifest.Id, item.RelativePath, ct); length = 0; hash = CollaborationPaths.Hash([]);
                }
                cursors.Add(new(item.RelativePath, length, hash));
            }
            return new(manifest.Id, cursors);
        }
        finally { gate.Release(); }
    }
    public async Task WriteChunkAsync(string id, string relativePath, long offset, byte[] bytes, string sha256, CancellationToken ct = default)
    {
        if (bytes.Length is < 1 or > CollaborationPaths.ChunkBytes || !CollaborationPaths.IsHash(sha256) || !CollaborationPaths.Hash(bytes).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("File chunk checksum does not match.");
        await gate.WaitAsync(ct);
        try
        {
            var manifest = await ManifestAsync(id, ct);
            var entry = manifest.Entries.SingleOrDefault(x => x.RelativePath == relativePath && !x.IsDirectory) ?? throw new ArgumentException("Unknown transfer file.");
            if (offset < 0 || offset > entry.Length - bytes.Length) throw new ArgumentException("Chunk is outside the offered file.");
            var target = CollaborationPaths.Resolve(Path.Combine(Stage(id), "content"), relativePath);
            await using var output = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None, CollaborationPaths.ChunkBytes, FileOptions.Asynchronous);
            if (output.Length != offset) throw new InvalidDataException("Resume offset changed. Query the receive state again.");
            output.Position = offset; await output.WriteAsync(bytes, ct); await output.FlushAsync(ct);
        }
        finally { gate.Release(); }
    }
    internal async Task ResetFileAsync(string id, string relative, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await ResetFileCoreAsync(id, relative, ct); }
        finally { gate.Release(); }
    }
    private async Task ResetFileCoreAsync(string id, string relative, CancellationToken ct)
    {
        var stage = Stage(id);
        if (File.Exists(Path.Combine(stage, "receipt.json")) || File.Exists(Path.Combine(stage, "commit.json"))) throw new InvalidOperationException("Committed files are never reset.");
        var manifest = await ManifestAsync(id, ct);
        if (!manifest.Entries.Any(x => !x.IsDirectory && x.RelativePath == relative)) throw new ArgumentException("Unknown partial file.");
        var target = CollaborationPaths.Resolve(Path.Combine(stage, "content"), relative);
        await using var output = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        output.SetLength(0); await output.FlushAsync(ct);
    }
    public async Task<TransferReceipt> CompleteAsync(string id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await CompleteCoreAsync(id, ct); }
        finally { gate.Release(); }
    }
    private async Task<TransferReceipt> CompleteCoreAsync(string id, CancellationToken ct)
    {
        var stage = Stage(id); var receiptPath = Path.Combine(stage, "receipt.json");
        if (File.Exists(receiptPath)) return await ReadAsync<TransferReceipt>(receiptPath, ct);
        var manifest = await ManifestAsync(id, ct); var content = Path.Combine(stage, "content");
        var journalPath = Path.Combine(stage, "commit.json");
        var journal = File.Exists(journalPath) ? await ReadAsync<Dictionary<string, string>>(journalPath, ct) : new Dictionary<string, string>();
        foreach (var item in manifest.Entries.Where(x => !x.IsDirectory))
        {
            var top = item.RelativePath.Split('/')[0];
            var file = CollaborationPaths.Resolve(content, item.RelativePath);
            var isPrivatePartial = File.Exists(file);
            if (!File.Exists(file) && journal.TryGetValue(top, out var committed))
                file = CollaborationPaths.Resolve(root, committed + item.RelativePath[top.Length..]);
            if (!File.Exists(file) || new FileInfo(file).Length != item.Length || !item.Sha256.Equals(await CollaborationPaths.HashFileAsync(file, ct: ct), StringComparison.OrdinalIgnoreCase))
            {
                if (isPrivatePartial && !File.Exists(journalPath)) await ResetFileCoreAsync(id, item.RelativePath, ct);
                throw new InvalidDataException("Received file is incomplete or its checksum changed. The private partial will restart from zero.");
            }
        }
        var saved = new List<string>();
        foreach (var name in manifest.Entries.Select(x => x.RelativePath.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var source = CollaborationPaths.Resolve(content, name);
            if (!File.Exists(source) && !Directory.Exists(source))
            {
                if (!journal.TryGetValue(name, out var moved)) throw new InvalidDataException("Receive item is missing.");
                var destination = CollaborationPaths.Resolve(root, moved);
                if (!File.Exists(destination) && !Directory.Exists(destination)) throw new InvalidDataException("Committed item is missing.");
                saved.Add(destination); continue;
            }
            for (var suffix = 0; ; suffix++)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = suffix == 0 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({suffix}){Path.GetExtension(name)}";
                var destination = CollaborationPaths.Resolve(root, candidate);
                if (File.Exists(destination) || Directory.Exists(destination)) continue;
                journal[name] = candidate; await WriteAsync(journalPath, journal, ct);
                try
                {
                    if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination, false);
                    saved.Add(destination); break;
                }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
            }
        }
        var receipt = new TransferReceipt(id, saved, manifest.TotalBytes);
        await WriteAsync(receiptPath, receipt, ct); return receipt;
    }
    private string Stage(string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid transfer identifier.");
        CollaborationPaths.Resolve(root);
        var partials = Path.Combine(root, ".desklink-partials"); CollaborationPaths.RejectLink(partials);
        var stage = Path.Combine(partials, id); CollaborationPaths.RejectLink(stage); return stage;
    }
    private Task<TransferManifest> ManifestAsync(string id, CancellationToken ct) => ReadAsync<TransferManifest>(Path.Combine(Stage(id), "manifest.json"), ct);
    private static async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        CollaborationPaths.Resolve(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct), JsonLineProtocol.Json) ?? throw new InvalidDataException("Missing transfer metadata.");
    }
    private static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        CollaborationPaths.Resolve(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            { await JsonSerializer.SerializeAsync(output, value, JsonLineProtocol.Json, ct); await output.FlushAsync(ct); }
            CollaborationPaths.Resolve(Path.GetDirectoryName(path)!, Path.GetFileName(path));
            CollaborationPaths.RejectLink(temporary); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
