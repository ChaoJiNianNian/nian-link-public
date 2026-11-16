using DesktopToolbox.Core;

namespace DesktopToolbox.Windows.DeskSwitch;

internal sealed class PairingFileExport(IUserPrompts prompts, Func<CancellationToken, Task<string>> generate)
{
    private string? lastExport;

    internal async Task<PairingExportResult> ExportAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var chosen = await prompts.PickSaveFileAsync("保存配对文件（含凭证，仅私发自己的 Mac）", "JSON 配对文件 (*.json)|*.json", "年年互联配对.json");
        token.ThrowIfCancellationRequested();
        if (chosen is null) return new(true, null, "已取消导出，未生成外部配对文件。");
        if (!System.IO.Path.IsPathFullyQualified(chosen)) throw new ArgumentException("请选择完整的保存位置。");
        var destination = System.IO.Path.GetFullPath(chosen);
        if (!string.Equals(System.IO.Path.GetExtension(destination), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请保存为 JSON 配对文件。");
        var parent = System.IO.Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) throw new DirectoryNotFoundException("保存位置不存在。");
        EnsurePlainPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("保存位置已有同名文件，请换一个名称。");

        var sourcePath = await generate(token);
        EnsurePlainPath(sourcePath);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length is < 1 or > 65536) throw new InvalidDataException("生成的配对文件无效，请重新启动共享后再试。");
        var temporary = System.IO.Path.Combine(parent, ".nian-pairing-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false; var committed = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await source.CopyToAsync(output, token);
                await output.FlushAsync(token);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            EnsurePlainPath(destination);
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException error) when (File.Exists(destination) || Directory.Exists(destination))
            { throw new IOException("保存位置出现了同名文件，请换一个名称；原文件未修改。", error); }
            committed = true;
        }
        finally { if (created && !committed && File.Exists(temporary)) File.Delete(temporary); }
        lastExport = destination;
        return new(false, destination, "配对文件已保存。请只通过私密方式发送给自己的 Mac。");
    }

    internal async Task<PairingRevealResult> RevealAsync()
    {
        if (lastExport is null) return new(false, "尚未导出配对文件。");
        try
        {
            await prompts.RevealAsync(lastExport, selectFile: true);
            return new(true, "已请求打开保存位置；如果没有显示，请按保存路径查找。");
        }
        catch (Exception) { return new(false, "配对文件已保存，但未能打开位置，请按保存路径查找。"); }
    }

    private static void EnsurePlainPath(string path)
    {
        for (string? part = System.IO.Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = System.IO.Path.GetDirectoryName(part))
        {
            try { if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("保存路径包含文件链接，请选择普通文件夹。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

internal sealed record PairingExportResult(bool Canceled, string? Path, string Message);
internal sealed record PairingRevealResult(bool Opened, string Message);
