using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>Installs a separately labelled, verified toolbox build without modifying the original Deskflow package.</summary>
public sealed class DeskLinkRuntimePackage
{
    private const string ManifestName = "toolbox-package-manifest.json";
    private readonly string packageDirectory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DeskLinkRuntimePackage(string packageDirectory) => this.packageDirectory = Path.GetFullPath(packageDirectory);
    public bool IsAvailable => File.Exists(Path.Combine(packageDirectory, ManifestName)) && File.Exists(Path.Combine(packageDirectory, "deskflow-core.exe"));

    public async Task<string> PrepareAsync(string destination, CancellationToken cancellationToken = default)
    {
        destination = Path.GetFullPath(destination);
        if (Inside(packageDirectory, destination) || Inside(destination, packageDirectory))
            throw new ArgumentException("引擎来源和私有运行目录不能重叠。");
        EnsureOrdinaryPath(packageDirectory);
        EnsureOrdinaryPath(destination);
        var manifestFile = Path.Combine(packageDirectory, ManifestName);
        EnsureOrdinaryPath(manifestFile);
        if (!File.Exists(manifestFile) || new FileInfo(manifestFile).Length > 1024 * 1024)
            throw new InvalidDataException("集成共享引擎缺少有效校验清单，请使用完整的新版本工具箱。");
        RuntimeManifest manifest;
        try { manifest = JsonSerializer.Deserialize<RuntimeManifest>(await File.ReadAllTextAsync(manifestFile, cancellationToken), Json) ?? throw new InvalidDataException("引擎清单为空。"); }
        catch (JsonException error) { throw new InvalidDataException("引擎清单格式无效。", error); }
        if (manifest.Version != 1 || manifest.UpstreamVersion != "1.27.0" || manifest.ControlProtocolVersion != 1 || manifest.Files is null || manifest.Files.Length is < 1 or > 256)
            throw new InvalidDataException("集成共享引擎版本或控制协议不兼容。");
        var files = new Dictionary<string, RuntimeFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null || !ValidRelative(file.Path) || file.Path.StartsWith("settings/", StringComparison.OrdinalIgnoreCase) ||
                file.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || !ValidHash(file.Sha256) || !files.TryAdd(file.Path, file))
                throw new InvalidDataException("引擎清单含无效、重复或私有设置路径。");
        }
        if (!files.ContainsKey("deskflow-core.exe")) throw new InvalidDataException("集成引擎缺少原生键鼠核心。");
        foreach (var relative in EnumerateSafeFiles(packageDirectory))
            if (!relative.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) && !files.ContainsKey(relative))
                throw new InvalidDataException("引擎来源含清单外文件：" + relative);
        foreach (var file in files.Values)
            await VerifyAsync(Path.Combine(packageDirectory, file.Path), file.Sha256, cancellationToken);

        if (Directory.Exists(destination))
        {
            foreach (var relative in EnumerateSafeFiles(destination))
            {
                if (relative.StartsWith("settings/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!files.TryGetValue(relative, out var file)) throw new InvalidDataException("私有引擎存在未登记文件：" + relative);
                await VerifyAsync(Path.Combine(destination, relative), file.Sha256, cancellationToken);
            }
        }
        EnsureOrdinaryPath(destination);
        Directory.CreateDirectory(destination);
        foreach (var file in files.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, file.Path);
            EnsureOrdinaryPath(target);
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(Path.Combine(packageDirectory, file.Path), temporary);
                await VerifyAsync(temporary, file.Sha256, cancellationToken);
                File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var settings = Path.Combine(destination, "settings");
        EnsureOrdinaryPath(settings);
        Directory.CreateDirectory(settings);
        var marker = Path.Combine(settings, "Deskflow.conf");
        EnsureOrdinaryPath(marker);
        if (!File.Exists(marker)) await File.WriteAllTextAsync(marker, "[core]\n", new UTF8Encoding(false), cancellationToken);
        return Path.Combine(destination, "deskflow-core.exe");
    }

    internal static void EnsureOrdinaryPath(string path)
    {
        for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("共享引擎与配置不能经过文件链接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static IEnumerable<string> EnumerateSafeFiles(string directory)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new(directory));
        var count = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            EnsureOrdinaryPath(current.FullName);
            foreach (var item in current.EnumerateFileSystemInfos())
            {
                if (++count > 4096) throw new InvalidDataException("引擎目录文件数超出范围。");
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("引擎目录含文件链接。");
                if (item is DirectoryInfo child) pending.Push(child);
                else yield return Path.GetRelativePath(directory, item.FullName).Replace(Path.DirectorySeparatorChar, '/');
            }
        }
    }

    private static bool ValidRelative(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 240 &&
        !Path.IsPathRooted(path) && !path.Contains((char)92) && !path.Contains(':') && !path.Any(char.IsControl) &&
        path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c));
    private static bool Inside(string root, string candidate) => string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static async Task VerifyAsync(string path, string expected, CancellationToken token)
    {
        EnsureOrdinaryPath(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 128 * 1024 * 1024) throw new InvalidDataException("引擎文件缺失或超出范围：" + info.Name);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
        if (actual != expected) throw new InvalidDataException("引擎文件校验失败，保留原文件：" + info.Name);
    }
    private sealed record RuntimeFile(string Path, string Sha256);
    private sealed record RuntimeManifest(int Version, string UpstreamVersion, int ControlProtocolVersion, RuntimeFile[] Files);
}