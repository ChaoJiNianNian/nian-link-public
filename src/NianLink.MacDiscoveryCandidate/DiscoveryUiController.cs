using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace NianLink.MacDiscoveryCandidate;

// Publish the task before invoking work: a synchronous completion cannot be stored back as active.
internal sealed class FilelessWorkSlot
{
    private readonly object gate = new();
    private Task? current;
    private bool poisoned;
    internal Task? Current { get { lock (gate) return current; } }
    internal bool IsPoisoned { get { lock (gate) return poisoned; } }

    // Startup may have failed before a session was returned; retain the cleanup failure.
    internal static void ThrowIfStartupCleanupFailed(Exception error)
    {
        if (error is PairingStartupCleanupException)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    internal bool TryStart(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        TaskCompletionSource completion;
        lock (gate)
        {
            if (poisoned || current is not null) return false;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            current = completion.Task;
        }
        _ = completion.Task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = RunAsync(operation, completion);
        return true;
    }

    private async Task RunAsync(Func<Task> operation, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { await (operation() ?? throw new InvalidOperationException("Pairing work did not start.")).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        lock (gate)
        {
            if (failure is not null) poisoned = true;
            if (ReferenceEquals(current, completion.Task)) current = null;
        }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }
}

internal interface IMacDiscoveryUiBackend
{
    Task<IReadOnlyList<IPAddress>> GetNetworksAsync(CancellationToken ct);
    Task<IMacDiscoveryUiSession> StartAsync(IPAddress address, PeerIdentityOffer offer, TimeSpan duration, CancellationToken ct);
}

internal interface IMacDiscoveryUiSession : IAsyncDisposable
{
    Task Completion { get; }
    Exception? Error { get; }
#if FILELESS_PAIRING_CANDIDATE
    PairingIncomingRequest? GetPendingPairingRequest();
    bool RejectPairingRequest(Guid id);
#endif
}

// Real adapter must use this only when startup could not fully release its resources.
internal sealed class MacDiscoveryStartupCleanupException(Exception cause) : Exception("发现启动清理未完成。", cause);

internal sealed class DiscoveryUiController(IMacDiscoveryUiBackend backend) : IAsyncDisposable
{
    private readonly object gate = new();
    private IReadOnlyList<IPAddress> networks = [];
    private IMacDiscoveryUiSession? current;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource? pending;
    private Task? stopping;
    private long generation;
    private bool closed, poisoned;
    internal bool IsDiscoverable { get { lock (gate) return current is { Completion.IsCompleted: false }; } }
    internal bool CanStart { get { lock (gate) return !closed && !poisoned && pending is null && current is null && stopping is null; } }
    internal IReadOnlyList<IPAddress> Networks { get { lock (gate) return networks; } }
    internal string Status { get; private set; } = "发现功能准备中，仍可使用身份卡配对。";

#if FILELESS_PAIRING_CANDIDATE
    // Snapshot only; the responder itself validates and owns each incoming request.
    internal IMacDiscoveryUiSession? CurrentFilelessSession
    { get { lock (gate) return closed || poisoned ? null : current; } }
    internal bool IsCurrentFilelessSession(IMacDiscoveryUiSession expected)
    { lock (gate) return !closed && !poisoned && ReferenceEquals(current, expected); }
#endif

    internal async Task RefreshAsync()
    {
        await StopAsync();
        long version;
        lock (gate) { RequireUsable(); version = generation; networks = []; }
        var found = await backend.GetNetworksAsync(CancellationToken.None);
        if (found is null || found.Count > 1024 || found.Any(x => x is null || x.AddressFamily != AddressFamily.InterNetwork) || found.Distinct().Count() != found.Count)
            throw new InvalidOperationException("无法确认可用的局域网。");
        lock (gate)
        {
            if (closed || version != generation) return;
            networks = found.Select(x => new IPAddress(x.GetAddressBytes())).ToArray();
            Status = networks.Count == 0 ? "发现功能准备中或没有可用网络；仍可使用身份卡配对。" : "请选择局域网。";
        }
    }

    internal async Task<bool> StartAsync(IPAddress? address, Func<Task<PeerIdentityOffer>> identity, Func<IPAddress, PeerIdentityOffer, Task<bool>> confirm)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(confirm);
        CancellationTokenSource source;
        TaskCompletionSource done;
        long version;
        IPAddress selected;
        lock (gate)
        {
            RequireUsable();
            if (pending is not null || current is not null || stopping is not null) throw new InvalidOperationException("发现正在启动或运行。");
            if (address is null || !networks.Contains(address)) throw new InvalidOperationException("请先选择活动局域网。");
            selected = new IPAddress(address.GetAddressBytes());
            version = ++generation; source = new(); cancellation = source;
            pending = done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Status = "正在准备本机身份…";
        }
        try
        {
            var offer = await identity();
            offer.Validate(DateTimeOffset.UtcNow);
            if (offer.Platform != "mac") throw new InvalidOperationException("本机身份类型无效。");
            if (!IsCurrent(version) || !await confirm(selected, offer) || !IsCurrent(version)) return false;
            var run = await backend.StartAsync(selected, offer, TimeSpan.FromSeconds(30), source.Token);
            if (run is null) throw new InvalidOperationException("发现服务没有启动。");
            lock (gate)
            {
                if (!closed && !poisoned && generation == version && !source.IsCancellationRequested && !run.Completion.IsCompleted)
                { current = run; Status = "本机可被发现，最多持续 30 秒。"; run = null!; }
            }
            if (run is not null)
            {
                try { await run.DisposeAsync(); }
                catch { lock (gate) poisoned = true; throw; }
                return false;
            }
            return true;
        }
        catch (MacDiscoveryStartupCleanupException) { lock (gate) poisoned = true; throw; }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { return false; }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, done)) pending = null;
                if (ReferenceEquals(cancellation, source) && current is null) { cancellation = null; source.Dispose(); }
                if (generation == version && current is null) Status = "发现未开启。";
            }
            done.TrySetResult();
        }
    }

    internal Task StopAsync()
    {
        lock (gate) return StopInsideLock();
    }

    private Task StopInsideLock()
    {
        if (stopping is not null) return stopping;
        ++generation;
        var run = current; current = null;
        var source = cancellation; cancellation = null;
        var start = pending?.Task;
        Status = "正在停止发现…";
        Task cancel;
        try { cancel = source?.CancelAsync() ?? Task.CompletedTask; }
        catch (Exception error) { cancel = Task.FromException(error); }
        return stopping = StopCoreAsync(run, source, start, cancel);
    }

    private Task StopObservedAsync(IMacDiscoveryUiSession observed, long version)
    {
        lock (gate)
        {
            if (generation != version || !ReferenceEquals(current, observed)) return Task.CompletedTask;
            return StopInsideLock();
        }
    }

    private async Task StopCoreAsync(IMacDiscoveryUiSession? run, CancellationTokenSource? source, Task? start, Task cancel)
    {
        await Task.Yield();
        Exception? failure = null;
        try { await cancel; } catch (Exception error) { failure = error; }
        try { if (start is not null) await start; } catch (Exception error) { failure ??= error; }
        try { if (run is not null) await run.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        source?.Dispose();
        lock (gate)
        {
            if (failure is not null) poisoned = true;
            Status = poisoned ? "发现停止失败，请关闭测试版。" : "发现已停止。";
            if (poisoned && failure is null) failure = new InvalidOperationException("发现清理失败。");
            stopping = null;
        }
        if (failure is not null) throw new InvalidOperationException("发现停止失败。", failure);
    }

    internal async Task ObserveCompletionAsync()
    {
        IMacDiscoveryUiSession? run;
        long version;
        lock (gate) { run = current; version = generation; }
        if (run is null || !run.Completion.IsCompleted) return;
        var failed = run.Error is not null || run.Completion.IsFaulted || run.Completion.IsCanceled;
        await StopObservedAsync(run, version);
        if (failed) lock (gate)
            if (generation == version + 1 && current is null && !closed) Status = "发现因网络变化或错误停止，请刷新网络后重试。";
    }

    private bool IsCurrent(long version) { lock (gate) return !closed && !poisoned && generation == version && cancellation is { IsCancellationRequested: false }; }
    private void RequireUsable() { if (closed || poisoned) throw new InvalidOperationException("发现界面已关闭或不可用。"); }
    public async ValueTask DisposeAsync() { lock (gate) closed = true; await StopAsync(); }
}
