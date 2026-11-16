namespace PreviewOwnerGateExperiment;

public sealed class OwnerGateTests
{
    [Fact]
    public async Task ConcurrentBeginHasOneOwnerUntilReturnIsProven()
    {
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<BeginProof>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend { Begin = _ => { begun.TrySetResult(); return release.Task; } };
        var gate = new OwnerPreviewGate(backend);
        var first = gate.BeginAsync();
        await begun.Task;
        Assert.False(gate.CanBegin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.BeginAsync());
        Assert.Equal(1, backend.BeginCount);
        release.SetResult(BeginProof.Active("one"));
        await first;
        Assert.False(gate.CanBegin);
        await gate.ReturnAsync();
        Assert.True(gate.CanBegin);
    }

    [Fact]
    public async Task EmergencyReturnWithoutAnOwnedOperationStillCannotEstablishThatNoActionIsInFlight()
    {
        var gate = new OwnerPreviewGate(new FakeBackend());
        await gate.AttemptEmergencyReturnAsync();
        Assert.False(gate.CanBegin);
    }

    [Fact]
    public async Task ProvenNoHandoffAllowsAnotherAttempt()
    {
        var backend = new FakeBackend { Begin = _ => Task.FromResult(BeginProof.NoHandoff()) };
        var gate = new OwnerPreviewGate(backend);
        await gate.BeginAsync();
        Assert.True(gate.CanBegin);
        await gate.BeginAsync();
        Assert.Equal(2, backend.BeginCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginFailureOrUnprovenResultPermanentlyBlocksAnotherBegin(bool returnsUnknown)
    {
        var backend = new FakeBackend { Begin = _ => returnsUnknown
            ? Task.FromResult(BeginProof.Unknown())
            : Task.FromException<BeginProof>(new IOException("response lost")) };
        var gate = new OwnerPreviewGate(backend);
        await Assert.ThrowsAnyAsync<Exception>(() => gate.BeginAsync());
        Assert.False(gate.CanBegin);
        await gate.AttemptEmergencyReturnAsync();
        Assert.False(gate.CanBegin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.BeginAsync());
    }

    [Fact]
    public async Task CanceledBeginRemainsBlockedEvenWhenItsLateResponseArrives()
    {
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<BeginProof>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend { Begin = _ => { begun.TrySetResult(); return release.Task; } };
        var gate = new OwnerPreviewGate(backend);
        using var cancel = new CancellationTokenSource();
        var first = gate.BeginAsync(cancel.Token);
        await begun.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult(BeginProof.Active("late"));
        await gate.AttemptEmergencyReturnAsync();
        Assert.False(gate.CanBegin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.BeginAsync());
    }

    [Fact]
    public async Task MalformedActiveEvidenceCannotOpenAnUnownedReturn()
    {
        var gate = new OwnerPreviewGate(new FakeBackend { Begin = _ => Task.FromResult(BeginProof.Active(" ")) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.BeginAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ReturnAsync());
        Assert.False(gate.CanBegin);
    }

    [Fact]
    public async Task DefaultBeginEvidenceIsUnknownAndCannotReleaseTheOwner()
    {
        var gate = new OwnerPreviewGate(new FakeBackend { Begin = _ => Task.FromResult(default(BeginProof)) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.BeginAsync());
        Assert.False(gate.CanBegin);
    }

    [Fact]
    public async Task CanceledReturnCannotBeReleasedByItsLateSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReturnProof>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend { Return = (_, _) => { entered.TrySetResult(); return release.Task; } };
        var gate = new OwnerPreviewGate(backend);
        await gate.BeginAsync();
        using var cancel = new CancellationTokenSource();
        var returning = gate.ReturnAsync(cancel.Token);
        await entered.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => returning);
        release.SetResult(ReturnProof.Confirmed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.BeginAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ReturnAsync());
    }

    [Fact]
    public async Task EmergencyDuringReturnDoesNotLetItsCompletionClearTheLock()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReturnProof>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend { Return = (_, _) => { entered.TrySetResult(); return release.Task; } };
        var gate = new OwnerPreviewGate(backend);
        await gate.BeginAsync();
        var returning = gate.ReturnAsync();
        await entered.Task;
        await gate.AttemptEmergencyReturnAsync();
        release.SetResult(ReturnProof.Confirmed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => returning);
        Assert.False(gate.CanBegin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedReturnStaysBlockedEvenIfRetriedOrEmergencyReturnSucceeds(bool returnsUnknown)
    {
        var backend = new FakeBackend();
        var gate = new OwnerPreviewGate(backend);
        await gate.BeginAsync();
        backend.Return = (_, _) => returnsUnknown ? Task.FromResult(ReturnProof.Unknown)
            : Task.FromException<ReturnProof>(new IOException("response lost"));
        await Assert.ThrowsAnyAsync<Exception>(() => gate.ReturnAsync());
        backend.Return = (_, _) => Task.FromResult(ReturnProof.Confirmed);
        await gate.AttemptEmergencyReturnAsync();
        Assert.False(gate.CanBegin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ReturnAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.BeginAsync());
    }

    [Fact]
    public async Task EmergencyReturnWhileBeginIsInFlightDoesNotReleaseTheOwner()
    {
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<BeginProof>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend { Begin = _ => { begun.TrySetResult(); return release.Task; } };
        var gate = new OwnerPreviewGate(backend);
        var first = gate.BeginAsync();
        await begun.Task;
        await gate.AttemptEmergencyReturnAsync();
        Assert.False(gate.CanBegin);
        release.SetResult(BeginProof.Active("one"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.False(gate.CanBegin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ReturnAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.BeginAsync());
    }

    private sealed class FakeBackend : IPreviewProofBackend
    {
        public Func<CancellationToken, Task<BeginProof>> Begin { get; set; } = _ => Task.FromResult(BeginProof.Active("one"));
        public Func<string, CancellationToken, Task<ReturnProof>> Return { get; set; } = (_, _) => Task.FromResult(ReturnProof.Confirmed);
        public int BeginCount;
        public Task<BeginProof> BeginAsync(CancellationToken ct) { Interlocked.Increment(ref BeginCount); return Begin(ct); }
        public Task<ReturnProof> ReturnAsync(string id, CancellationToken ct) => Return(id, ct);
        public Task AttemptEmergencyReturnAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
