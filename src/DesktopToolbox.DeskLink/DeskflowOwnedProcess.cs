using System.Diagnostics;
namespace DesktopToolbox.DeskLink;

public interface IDeskflowOwnedProcess : IAsyncDisposable
{
    int Id { get; }
    bool HasExited { get; }
    Task<int> Completion { get; }
    Task StopAsync(CancellationToken ct = default);
}

public sealed record DeskflowSessionDependencies
{
    public Func<int?, bool> HasConflictingCore { get; init; } = DefaultConflict;
    public Func<ProcessStartInfo, CancellationToken, Task<IDeskflowOwnedProcess>> StartProcessAsync { get; init; } = DeskflowOwnedProcess.StartAsync;
    public Func<int, CancellationToken, Task<Stream>> ConnectCoreIpcAsync { get; init; } = LocalControlTransport.ConnectOwnedCoreAsync;
    public Func<int, CancellationToken, Task<NativeControlClient>> ConnectNativeAsync { get; init; } = NativeControlClient.ConnectAsync;
    internal Func<string, string, CancellationToken, Task> WritePeerTrustAsync { get; init; } = PrivateFiles.WriteAsync;
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(6);
    private static bool DefaultConflict(int? ownedId)
    {
        foreach (var name in new[] { "deskflow-core", "nian-deskflow-core" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process) if (process.Id != ownedId) return true;
        return false;
    }
}
internal sealed class DeskflowOwnedProcess : IDeskflowOwnedProcess
{
    private readonly Process process;
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public Task<int> Completion { get; }
    private DeskflowOwnedProcess(Process process) { this.process = process; Completion = WaitAsync(); }
    public static Task<IDeskflowOwnedProcess> StartAsync(ProcessStartInfo start, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows() && !File.Exists(Path.Combine(Path.GetDirectoryName(start.FileName)!, "settings", "Deskflow.conf")))
            throw new InvalidOperationException("The private Windows engine is missing its portable settings/Deskflow.conf marker.");
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("Deskflow core could not start.");
            // Drain both redirected streams to prevent the core blocking on a full output pipe.
            _ = DrainAsync(process.StandardOutput); _ = DrainAsync(process.StandardError);
            return Task.FromResult<IDeskflowOwnedProcess>(new DeskflowOwnedProcess(process));
        }
        catch { process.Dispose(); throw; }
    }
    private static async Task DrainAsync(TextReader input)
    {
        var buffer = new char[4096];
        try { while (await input.ReadAsync(buffer) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
    private async Task<int> WaitAsync() { await process.WaitForExitAsync(); return process.ExitCode; }
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (process.HasExited) return;
        // This object is created only for a child we started; never finds or kills a process by name.
        if (process.CloseMainWindow())
        {
            try { await Completion.WaitAsync(TimeSpan.FromSeconds(2), ct); return; } catch (TimeoutException) { }
        }
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await Completion.WaitAsync(ct);
    }
    public ValueTask DisposeAsync() { process.Dispose(); return ValueTask.CompletedTask; }
}
