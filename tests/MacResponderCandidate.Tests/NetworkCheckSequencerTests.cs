using NianLink.MacResponderCandidate;

public sealed class NetworkCheckSequencerTests
{
    [Fact]
    public async Task MonitorAndHandshakeChecksDoNotOverlap()
    {
        var gate = new NetworkCheckSequencer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        async Task<bool> Sample(CancellationToken cancellation)
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            entered.TrySetResult();
            try { await release.Task.WaitAsync(cancellation); return true; }
            finally { Interlocked.Decrement(ref active); }
        }
        var first = gate.RunAsync(Sample, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = gate.RunAsync(Sample, default);
        Assert.False(second.IsCompleted);
        release.SetResult();
        Assert.True(await first);
        Assert.True(await second);
    }

    [Fact]
    public async Task CancellingQueuedCheckDoesNotCancelCurrentCheck()
    {
        var gate = new NetworkCheckSequencer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = gate.RunAsync(async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); return true; }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancelled = new CancellationTokenSource();
        var second = gate.RunAsync(_ => Task.FromResult(true), cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.SetResult();
        Assert.True(await first);
    }

    [Fact]
    public async Task RevocationCannotPublishLateSuccessfulSample()
    {
        var gate = new NetworkCheckSequencer();
        using var cancelled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = gate.RunAsync(_ => { entered.TrySetResult(); return late.Task; }, cancelled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        late.SetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
