using System.Text;
using System.Text.Json;

namespace DesktopToolbox.Mac.Services;

public sealed class MacPrivateStore
{
    public string DirectoryPath { get; }
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true, MaxDepth = 12 };
    public MacPrivateStore(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("资料目录必须使用绝对路径。");
        DirectoryPath = Path.GetFullPath(path);
    }
    public async Task<T?> ReadAsync<T>(string name, CancellationToken ct)
    {
        var file = Resolve(name); RejectLinks(file);
        if (!File.Exists(file)) return default;
        if (new FileInfo(file).Length > 65536) throw new InvalidDataException("本机配对资料过大，已保留原文件。");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(file, ct), Json);
    }
    public async Task WriteAsync<T>(string name, T value, CancellationToken ct)
    {
        var file = Resolve(name); RejectLinks(file);
        Directory.CreateDirectory(DirectoryPath); RejectLinks(DirectoryPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var text = JsonSerializer.Serialize(value, Json);
        if (Encoding.UTF8.GetByteCount(text) > 65536) throw new InvalidDataException("配对资料超过 64 KB。");
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            RejectLinks(file); File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private string Resolve(string name)
    {
        if (name is not ("connection.json" or "paired-control.json" or "collaboration.json")) throw new ArgumentException("未知的本机资料名称。");
        return Path.Combine(DirectoryPath, name);
    }
    private static void RejectLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("配对资料不能经过文件链接。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
