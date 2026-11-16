using System.Diagnostics;
using System.Text.Json;

namespace DesktopToolbox.Windows.DeskSwitch;

public interface IDisplayBridge
{
    Task<DisplayMonitorInfo[]> ListAsync(CancellationToken cancellationToken);
    Task ApplyAsync(string configurationFile, string target, bool test, CancellationToken cancellationToken);
}

public sealed class DisplayBridgeProcess(string executable) : IDisplayBridge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<DisplayMonitorInfo[]> ListAsync(CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<DisplayMonitorInfo[]>(await RunAsync(["--list"], cancellationToken), Json)
        ?? throw new InvalidDataException("显示器检测没有返回有效结果。");

    public async Task ApplyAsync(string configurationFile, string target, bool test, CancellationToken cancellationToken)
    {
        if (target is not ("local" or "remote")) throw new ArgumentException("切换目标无效。");
        var arguments = new List<string> { "--config", configurationFile, "--target", target };
        if (test) arguments.Add("--test");
        var result = await RunAsync(arguments, cancellationToken);
        using var document = JsonDocument.Parse(result);
        if (!document.RootElement.TryGetProperty("commandAccepted", out var accepted) || accepted.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("显示器没有确认接收切换命令。");
    }

    private async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("切屏组件尚未就绪，请使用完整工具箱目录。", executable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动切屏组件。");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var message = await output;
            var error = await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "显示器操作未完成。" : error[..Math.Min(error.Length, 800)].Trim());
            if (message.Length > 65536) throw new InvalidDataException("显示器返回的数据超出范围。");
            return message;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            if (!cancellationToken.IsCancellationRequested) throw new TimeoutException("显示器驱动响应超时，本次操作已停止。");
            throw;
        }
    }
}
