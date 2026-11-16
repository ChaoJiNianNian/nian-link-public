using System;
using System.Threading;
using System.Threading.Tasks;
using NianLink.CandidateDiscovery;
using DesktopToolbox.DeskLink;

namespace NianLink.MultiDevice;

// Owns at most one explicit discovery run, including a start that has not returned yet.
internal sealed class DiscoveryUiLifetime<T> where T : class, IAsyncDisposable
{
    private readonly object gate = new();
    private T? current;
    private Task<bool>? pendingStart;
    private Task? pendingStop;
    private long generation;
    private bool retired, poisoned;
    private CancellationTokenSource? runningCancellation;

    internal T? Current { get { lock (gate) return current; } }

    internal Task<bool> StartAsync(Func<CancellationToken, Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        TaskCompletionSource<bool> completion;
        long captured;
        CancellationTokenSource cancellation;
        lock (gate)
        {
            if (retired || poisoned || pendingStop is not null || pendingStart is not null || current is not null)
                throw new InvalidOperationException("Discovery is unavailable or already running.");
            captured = ++generation;
            cancellation = new CancellationTokenSource();
            runningCancellation = cancellation;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingStart = completion.Task;
        }
        _ = Task.Run(() => CompleteStartAsync(factory, captured, cancellation, completion));
        return completion.Task;
    }

    private async Task CompleteStartAsync(Func<CancellationToken, Task<T>> factory, long captured, CancellationTokenSource cancellation, TaskCompletionSource<bool> completion)
    {
        T? run = null;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            run = await factory(cancellation.Token).ConfigureAwait(false) ?? throw new InvalidOperationException("Discovery did not start.");
            bool accepted;
            lock (gate)
            {
                accepted = !retired && !poisoned && pendingStop is null && captured == generation;
                if (accepted) { current = run; run = null; }
            }
            if (run is not null)
            {
                try { await run.DisposeAsync().ConfigureAwait(false); }
                catch { lock (gate) poisoned = true; throw; }
            }
            lock (gate) pendingStart = null;
            completion.TrySetResult(accepted);
        }
        catch (Exception error)
        {
            bool releaseCancellation;
            lock (gate)
            {
                pendingStart = null;
                if (error is DiscoveryStartupCleanupException or PairingStartupCleanupException) poisoned = true;
                releaseCancellation = ReferenceEquals(runningCancellation, cancellation);
                if (releaseCancellation) runningCancellation = null;
            }
            if (releaseCancellation) cancellation.Dispose();
            completion.TrySetException(error);
        }
    }

    internal Task StopAsync()
    {
        TaskCompletionSource completion;
        Task<bool>? starting;
        T? run;
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (pendingStop is not null) return pendingStop;
            ++generation;
            run = current; current = null;
            starting = pendingStart;
            cancellation = runningCancellation; runningCancellation = null;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingStop = completion.Task;
        }
        _ = CompleteStopAsync(run, starting, cancellation, completion);
        return completion.Task;
    }

    private async Task CompleteStopAsync(T? run, Task<bool>? starting, CancellationTokenSource? cancellation, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { cancellation?.Cancel(); } catch (Exception error) { failure = error; }
        try { if (starting is not null) await starting.ConfigureAwait(false); }
        catch { /* Start reports its own failure; a failed cleanup is tracked below. */ }
        try { if (run is not null) await run.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failure ??= error; }
        cancellation?.Dispose();
        lock (gate)
        {
            if (failure is not null) poisoned = true;
            if (poisoned && failure is null) failure = new InvalidOperationException("Discovery cleanup failed.");
            pendingStop = null;
        }
        if (failure is null) completion.TrySetResult();
        else completion.TrySetException(new InvalidOperationException("Discovery cleanup failed.", failure));
    }

    internal Task CloseAsync()
    {
        lock (gate) retired = true;
        return StopAsync();
    }
}

internal static class DiscoveryUiStop
{
    // Begin revocation before awaiting a possibly slow transport drain.
    internal static async Task StopAndReleaseAsync(Func<Task> release, Func<Task> stop)
    {
        Task releasing;
        try { releasing = release(); }
        catch (Exception error) { releasing = Task.FromException(error); }
        try { await stop().ConfigureAwait(false); }
        finally { await releasing.ConfigureAwait(false); }
    }
}
