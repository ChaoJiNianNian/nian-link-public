using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.Core;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>Exports only the verified installer that was included with this installation.</summary>
internal sealed class MacInstallerExport(string packageDirectory, IUserPrompts prompts, string expectedMode = "one-to-one")
{
    private const string ArchiveName = "NianDeskLink-macOS-arm64.zip";
    private const int ManifestLimit = 16 * 1024;
    private const long PackageLimit = 512L * 1024 * 1024;
    private readonly string root = Path.GetFullPath(packageDirectory);

    internal async Task<object> ExportAsync(CancellationToken token)
    {
        var archive = Path.Combine(root, ArchiveName);
        var manifest = Path.Combine(root, "manifest.json");
        token.ThrowIfCancellationRequested();
        EnsurePlainPath(archive);
        EnsurePlainPath(manifest);
        if (!File.Exists(archive) || !File.Exists(manifest))
            throw new FileNotFoundException("当前独立测试版尚未附带 Mac 安装包，暂时无法导出。");

        using var manifestStream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (manifestStream.Length is < 1 or > ManifestLimit) throw new InvalidDataException("Mac 测试安装包清单无效，暂时无法导出。");
        using var document = await JsonDocument.ParseAsync(manifestStream, cancellationToken: token);
        var record = document.RootElement;
        var actualMode = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("mode", out var mode)
            ? mode.ValueKind == JsonValueKind.String ? mode.GetString() : null : "one-to-one";
        if (record.ValueKind != JsonValueKind.Object ||
            expectedMode is not ("one-to-one" or "multi-device-test") || actualMode != expectedMode ||
            !record.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String || file.GetString() != ArchiveName ||
            !record.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || !Version.TryParse(version.GetString(), out _) ||
            !record.TryGetProperty("bytes", out var size) || !size.TryGetInt64(out var expectedSize) || expectedSize is < 1 or > PackageLimit ||
            !record.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String || !TryDigest(sha.GetString(), out var expectedDigest) ||
            !record.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.String || channel.GetString() != "internal-test" ||
            !record.TryGetProperty("includesDeskflow", out var deskflow) || deskflow.ValueKind != JsonValueKind.True ||
            !record.TryGetProperty("notarized", out var notarized) || notarized.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Mac 测试安装包清单无效，暂时无法导出。");

        // Retain an open read-only handle from verification through the copy so
        // another process cannot replace the payload while the dialog is open.
        await using var source = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != expectedSize || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(source, token), expectedDigest))
            throw new InvalidDataException("Mac 测试安装包校验失败，暂时无法导出。");

        var chosen = await prompts.PickSaveFileAsync("导出 Mac 测试安装包", "ZIP 压缩包 (*.zip)|*.zip", ArchiveName);
        token.ThrowIfCancellationRequested();
        if (chosen is null) return new { canceled = true, path = (string?)null, message = "已取消导出。" };
        if (!Path.IsPathFullyQualified(chosen)) throw new ArgumentException("请选择完整的保存位置。");
        var destination = Path.GetFullPath(chosen);
        if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请将 Mac 安装包保存为 ZIP 文件。");
        if (string.Equals(destination, archive, StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择工具箱安装目录之外的保存位置。");
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) throw new DirectoryNotFoundException("保存位置不存在。");
        EnsurePlainPath(destination);
        if (Directory.Exists(destination)) throw new IOException("保存位置是文件夹，请选择文件名。");
        if (File.Exists(destination)) throw new IOException("保存位置已有同名文件，请换一个名称再导出。");

        var temporary = Path.Combine(parent, ".nian-mac-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        var committed = false;
        try
        {
            source.Position = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                await source.CopyToAsync(output, token);
                await output.FlushAsync(token);
            }
            await using (var verify = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (verify.Length != expectedSize || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(verify, token), expectedDigest))
                    throw new IOException("导出文件校验失败，原文件没有被更改。");
            }
            token.ThrowIfCancellationRequested();
            EnsurePlainPath(destination);
            CommitNewFile(temporary, destination);
            committed = true;
        }
        finally
        {
            if (created && !committed && File.Exists(temporary)) File.Delete(temporary);
        }
        return new { canceled = false, path = destination, message = "Mac 测试安装包已保存，未经过 Apple 公证，可通过微信私发到自己的 Mac。" };
    }

    private static bool TryDigest(string? value, out byte[] digest)
    {
        digest = [];
        if (value is not { Length: 64 }) return false;
        try { digest = Convert.FromHexString(value); return digest.Length == 32; }
        catch (FormatException) { return false; }
    }

    // Never replace a file, even if it appeared during the copy after the last
    // path check. The operating system performs this move without overwriting.
    internal static void CommitNewFile(string temporary, string destination)
    {
        try { File.Move(temporary, destination, overwrite: false); }
        catch (IOException error) when (File.Exists(destination) || Directory.Exists(destination))
        { throw new IOException("保存位置出现了同名文件，请换一个名称再导出；原文件未修改。", error); }
    }

    private static void EnsurePlainPath(string path)
    {
        for (string? part = path; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("安装包或保存位置包含文件链接，请选择普通文件夹。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
