using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DesktopToolbox.Mac.Services.Display;

namespace DesktopToolbox.Mac.Services;

/// <summary>Each DDC request owns one child; cancellation and disposal confirm its exit before allowing another.</summary>
public sealed class MacDisplayBridgeProcess : IMacDisplayControl, IAsyncDisposable
{
    private readonly string executable;
    private readonly TimeSpan timeout;
    private readonly string[] bootstrapArguments = [];
    private readonly object sync = new();
    private readonly SemaphoreSlim disposal = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource requestCycle = new();
    private readonly HashSet<Task> requests = [];
    private readonly HashSet<Process> children = [];
    private bool disposed;
    private bool lifetimeDisposed;
    private bool stopping;

    public MacDisplayBridgeProcess(string? executable = null, TimeSpan? timeout = null)
    {
        this.executable = executable ?? Environment.ProcessPath ?? throw new IOException("Cannot locate the Mac apphost.");
        this.timeout = timeout ?? TimeSpan.FromSeconds(4);
        if (!Path.IsPathFullyQualified(this.executable) || this.timeout <= TimeSpan.Zero) throw new ArgumentException("Invalid display bridge process configuration.");
    }
    internal MacDisplayBridgeProcess(string executable, TimeSpan timeout, IReadOnlyList<string> bootstrapArguments) : this(executable, timeout) => this.bootstrapArguments = bootstrapArguments.ToArray();
    internal IReadOnlyList<int> OwnedChildIds { get { lock (sync) return children.Select(child => child.Id).ToArray(); } }
    public bool HasUnfinishedChildren { get { lock (sync) { PruneExitedChildren(); return stopping || children.Count != 0; } } }
    public async Task<IReadOnlyList<MacDisplayDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
        (await RequestAsync("list", null, null, cancellationToken)).Displays ?? throw new IOException("Display bridge omitted its display list.");
    public async Task<MacDisplayReadResult> ReadInputAsync(MacDisplayIdentity identity, CancellationToken cancellationToken = default) =>
        (await RequestAsync("read", identity, null, cancellationToken)).Read ?? throw new IOException("Display bridge omitted its read result.");
    public async Task<MacDisplaySetResult> SetInputAsync(MacDisplayIdentity identity, ushort input, CancellationToken cancellationToken = default) =>
        (await RequestAsync("set", identity, input, cancellationToken)).Set ?? throw new IOException("Display bridge omitted its set result.");

    private Task<MacDisplayBridgeReply> RequestAsync(string action, MacDisplayIdentity? identity, ushort? input, CancellationToken token)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            PruneExitedChildren();
            if (stopping || children.Count != 0) throw new IOException("上一条 Mac 显示辅助进程尚未确认退出，不会排队重复切屏。");
            var work = RequestInsideAsync(action, identity, input, token);
            requests.Add(work);
            _ = ForgetAfterCompletionAsync(work);
            return work;
        }
    }
    private async Task ForgetAfterCompletionAsync(Task work)
    {
        try { await work.ConfigureAwait(false); } catch { /* The requesting caller receives the original error. */ }
        finally { lock (sync) { requests.Remove(work); PruneExitedChildren(); } }
    }
    private async Task<MacDisplayBridgeReply> RequestInsideAsync(string action, MacDisplayIdentity? identity, ushort? input, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in bootstrapArguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--display-bridge"); start.ArgumentList.Add(action);
        if (identity is { } id)
        {
            start.ArgumentList.Add("--vendor"); start.ArgumentList.Add(id.VendorId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--product"); start.ArgumentList.Add(id.ProductId.ToString(CultureInfo.InvariantCulture));
        }
        if (input is { } value) { start.ArgumentList.Add("--input"); start.ArgumentList.Add(value.ToString(CultureInfo.InvariantCulture)); }
        var requestCycleToken = requestCycle.Token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, requestCycleToken); deadline.CancelAfter(timeout);
        deadline.Token.ThrowIfCancellationRequested();
        var child = new Process { StartInfo = start };
        try { if (!child.Start()) throw new IOException("Cannot start the owned display bridge."); }
        catch { child.Dispose(); throw; }
        lock (sync) children.Add(child);
        var output = ReadLimitedAsync(child.StandardOutput.BaseStream, 16384, deadline.Token);
        var errors = ReadLimitedAsync(child.StandardError.BaseStream, 4096, deadline.Token);
        try
        {
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false); await errors.ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<MacDisplayBridgeReply>(text, MacDisplayBridgeCommand.Json) ?? throw new IOException("Empty display bridge response.");
            if (!reply.Ok || child.ExitCode != 0) throw new IOException(reply.Error ?? "Owned display bridge failed.");
            return reply;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested && !requestCycleToken.IsCancellationRequested)
        { throw new TimeoutException("Mac 本地显示命令超时，未确认实际画面；本次辅助进程会先停止再允许重试。"); }
        finally
        {
            Exception? cleanupFailure = null;
            try { await TerminateOwnedChildAsync(child).ConfigureAwait(false); }
            catch (Exception error) { cleanupFailure = error; }
            deadline.Cancel();
            try { await Task.WhenAll(output, errors).ConfigureAwait(false); } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or DecoderFallbackException) { }
            lock (sync)
            {
                if (child.HasExited) { children.Remove(child); child.Dispose(); }
                // Retain a child whose exit was not confirmed. New requests stay blocked.
            }
            if (cleanupFailure is not null) throw new IOException("Mac 显示辅助进程停止未确认，已阻止新的切屏命令，请重试停止。", cleanupFailure);
        }
    }
    private static async Task TerminateOwnedChildAsync(Process child)
    {
        if (!child.HasExited)
        {
            try { child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (child.HasExited) { }
        }
        if (!child.HasExited) await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }
    private void PruneExitedChildren()
    {
        if (disposed || stopping || requests.Count != 0) return;
        foreach (var child in children.Where(item => item.HasExited).ToArray()) { children.Remove(child); child.Dispose(); }
    }
    private static async Task<string> ReadLimitedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[2048]; int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > limit) throw new IOException("Display bridge output exceeded the bounded protocol size.");
            await bytes.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }
    public async Task StopOwnedChildrenAsync()
    {
        await disposal.WaitAsync().ConfigureAwait(false);
        try { await StopInsideAsync(permanent: false).ConfigureAwait(false); }
        finally { disposal.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await disposal.WaitAsync().ConfigureAwait(false);
        try { await StopInsideAsync(permanent: true).ConfigureAwait(false); }
        finally { disposal.Release(); }
    }
    private async Task StopInsideAsync(bool permanent)
    {
        Task[] pending; CancellationTokenSource cycle;
        lock (sync)
        {
            if (lifetimeDisposed) return;
            if (permanent) disposed = true;
            stopping = true; pending = requests.ToArray(); cycle = requestCycle;
        }
        if (permanent) lifetime.Cancel();
        cycle.Cancel();
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { /* Confirm cleanup from retained owned process handles below. */ }
        Process[] remaining; lock (sync) remaining = children.ToArray();
        foreach (var child in remaining)
        {
            await TerminateOwnedChildAsync(child).ConfigureAwait(false);
            lock (sync) { children.Remove(child); child.Dispose(); }
        }
        lock (sync)
        {
            cycle.Dispose();
            if (permanent) { lifetime.Dispose(); lifetimeDisposed = true; }
            else requestCycle = new();
            stopping = false;
        }
        // A cleanup exception leaves stopping=true, preserving a visible retryable
        // state and preventing a replacement request until Stop confirms exit.
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
