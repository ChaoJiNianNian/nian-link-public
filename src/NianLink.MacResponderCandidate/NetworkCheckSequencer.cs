namespace NianLink.MacResponderCandidate;

/// <summary>Serialize native observations, while cancellation revokes any queued or late result.</summary>
internal sealed class NetworkCheckSequencer
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stopped = new();

    internal async Task<bool> RunAsync(Func<CancellationToken, Task<bool>> check, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopped.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        Task<bool>? operation = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            operation = check(linked.Token);
            var result = await operation.WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            gate.Release();
            if (operation is not null && !operation.IsCompleted)
                _ = operation.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal async Task StopAndDrainAsync()
    {
        stopped.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
    }
}
