using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>Runs one owned display helper at a time and retains a child whose exit is unconfirmed.</summary>
public sealed class SafeDisplayBridgeProcess : IDisplayBridge
{
    private readonly string executable;
    private readonly string[] prefix;
    private readonly TimeSpan timeout;
    private readonly object sync = new();
    private Process? owned;
    private bool running;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SafeDisplayBridgeProcess(string executable) : this(executable, [], TimeSpan.FromSeconds(12)) { }
    internal SafeDisplayBridgeProcess(string executable, IReadOnlyList<string> prefix, TimeSpan timeout)
    {
        if (!Path.IsPathFullyQualified(executable) || timeout <= TimeSpan.Zero) throw new ArgumentException("切屏组件路径或超时无效。");
        this.executable = executable; this.prefix = prefix.ToArray(); this.timeout = timeout;
    }

    internal int? OwnedChildId { get { lock (sync) return owned?.Id; } }
    public bool HasUnfinishedChild { get { lock (sync) { PruneExited(); return running || owned is not null; } } }

    public async Task<DisplayMonitorInfo[]> ListAsync(CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<DisplayMonitorInfo[]>(await RunAsync(["--list"], cancellationToken), Json)
            ?? throw new InvalidDataException("显示器检测没有返回有效结果。");

    public async Task ApplyAsync(string configurationFile, string target, bool test, CancellationToken cancellationToken)
    {
        if (target is not ("local" or "remote")) throw new ArgumentException("切换目标无效。", nameof(target));
        var args = new List<string> { "--config", configurationFile, "--target", target };
        if (test) args.Add("--test");
        using var document = JsonDocument.Parse(await RunAsync(args, cancellationToken));
        if (!document.RootElement.TryGetProperty("commandAccepted", out var accepted) || accepted.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("显示器没有确认接收切换命令。");
    }

    private async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executable)) throw new FileNotFoundException("切屏组件尚未就绪。", executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var value in prefix) start.ArgumentList.Add(value);
        foreach (var value in arguments) start.ArgumentList.Add(value);
        var child = new Process { StartInfo = start };
        lock (sync)
        {
            PruneExited();
            if (running || owned is not null) { child.Dispose(); throw new InvalidOperationException("上一次切屏组件尚未确认退出。"); }
            running = true;
            try { if (!child.Start()) throw new IOException("无法启动切屏组件。"); owned = child; }
            catch { running = false; child.Dispose(); throw; }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var output = ReadLimitedAsync(child.StandardOutput, deadline.Token);
        var errors = ReadLimitedAsync(child.StandardError, deadline.Token);
        try
        {
            await Task.WhenAll(child.WaitForExitAsync(deadline.Token), output, errors);
            if (child.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(errors.Result) ? "显示器操作未完成。" : errors.Result[..Math.Min(errors.Result.Length, 800)].Trim());
            return output.Result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("显示器组件响应超时，正在确认其退出。"); }
        finally
        {
            Exception? failure = null;
            try
            {
                if (!child.HasExited)
                {
                    try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) when (child.HasExited) { }
                    if (!child.HasExited) await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or IOException or UnauthorizedAccessException or NotSupportedException)
            { failure = error; }
            deadline.Cancel();
            if (failure is null) try { await Task.WhenAll(output, errors); } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
            else { _ = output.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted); _ = errors.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted); }
            lock (sync) { running = false; if (failure is null) { owned = null; child.Dispose(); } }
            if (failure is not null) throw new IOException("切屏组件退出未确认，已禁止新的显示器操作。", failure);
        }
    }

    private void PruneExited()
    {
        if (running || owned is null) return;
        try { if (owned.HasExited) { owned.Dispose(); owned = null; } }
        catch (InvalidOperationException) { /* Keep the entry if its exit cannot be verified. */ }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096]; var text = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (text.Length + count > 65536) throw new InvalidDataException("显示器组件输出超出范围。");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
