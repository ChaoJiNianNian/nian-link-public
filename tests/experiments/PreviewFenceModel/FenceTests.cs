namespace PreviewFenceModel;

public sealed class FenceTests
{
    private static TestFence NewFence(int capacity, bool previousProcessProvenStopped)
    {
        var fence = new TestFence(capacity, previousProcessProvenStopped);
        Assert.True(fence.RegisterTrustedGrant("grant-a", "generation-1"));
        Assert.True(fence.RegisterTrustedGrant("grant-b", "generation-1"));
        Assert.True(fence.RegisterTrustedGrant("peer", "grant-v1"));
        return fence;
    }
    [Fact]
    public async Task SealBeforeBeginBlocksLateBeginAndRenew()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var id = Guid.NewGuid(); var calls = 0;
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => { calls++; return Task.FromResult(true); }));
        Assert.Equal(Result.Rejected, await fence.Renew(fence.Incarnation, "grant-a", id, "generation-1", "target-1"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RepeatedBeginAndSealDoNotRepeatSideEffects()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var id = Guid.NewGuid(); var beginCount = 0; var returnCount = 0;
        Task<bool> Begin() { beginCount++; return Task.FromResult(true); }
        Task<bool> Return() { returnCount++; return Task.FromResult(true); }
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", Begin));
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", Begin));
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", Return));
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", Return));
        Assert.Equal(1, beginCount); Assert.Equal(1, returnCount);
    }

    [Fact]
    public async Task UnconfirmedReturnKeepsTombstoneWithoutDeclaringSafetyOnRetry()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var id = Guid.NewGuid();
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(false)));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Renew(fence.Incarnation, "grant-a", id, "generation-1", "target-1"));
        Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task CapacityNeverEvictsTombstonesOrAcknowledgesUnknownSeal()
    {
        var fence = NewFence(1, previousProcessProvenStopped: true); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-a", first, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", second, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-a", first, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-a", second, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-a", first, "generation-1", "target-1", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task OperationBelongsToGrantAndOldIncarnationIsRejected()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var previous = NewFence(4, previousProcessProvenStopped: true); var id = Guid.NewGuid();
        Assert.NotEqual(previous.Incarnation, fence.Incarnation);
        Assert.Equal(Result.Rejected, await fence.Begin(previous.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Renew(fence.Incarnation, "grant-b", id, "generation-1", "target-1"));
        Assert.Equal(Result.Rejected, await fence.Seal(fence.Incarnation, "grant-b", id, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Active, await fence.Renew(fence.Incarnation, "grant-a", id, "generation-1", "target-1"));
    }

    [Fact]
    public async Task InFlightBeginIsFencedPromptlyAndSealRemainsUnconfirmedUntilItFinishes()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var id = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var begin = fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", async () => { entered.SetResult(); await finish.Task; return true; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var seal = fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true));
        Assert.Equal(Result.Unconfirmed, await seal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Result.Rejected, await fence.Renew(fence.Incarnation, "grant-a", id, "generation-1", "target-1"));
        finish.SetResult();
        Assert.Equal(Result.Unconfirmed, await begin);
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task UnknownBeginOutcomeBlocksSealAndDuplicateBegin()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var id = Guid.NewGuid(); var starts = 0; var returns = 0;
        Task<bool> Begin() { starts++; throw new IOException("synthetic lost response"); }
        Task<bool> Return() { returns++; return Task.FromResult(true); }
        Assert.Equal(Result.Unconfirmed, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", Begin));
        Assert.Equal(Result.Unconfirmed, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", Begin));
        Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", Return));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", Begin));
        Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", Return));
        Assert.Equal(1, starts); Assert.Equal(0, returns);
    }

    [Fact]
    public async Task UnknownBeginMayCommitAfterSealRequestAndMustNeverBeDeclaredSealed()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var id = Guid.NewGuid(); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateEffect = false;
        var background = Task.Run(async () => { await finish.Task; lateEffect = true; });
        Task<bool> Begin() => throw new IOException("response lost while native action still pending");
        Assert.Equal(Result.Unconfirmed, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", Begin));
        Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        finish.SetResult();
        await background.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(lateEffect);
        Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task StalledBeginDoesNotBlockOtherOperationsOrSealing()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var stalledId = Guid.NewGuid(); var otherId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = fence.Begin(fence.Incarnation, "grant-a", stalledId, "generation-1", "target-1", async () => { entered.SetResult(); await finish.Task; return true; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", stalledId, "generation-1", "target-1", () => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-b", otherId, "generation-1", "target-1", () => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { finish.SetResult(); await pending; }
    }

    [Fact]
    public async Task UncertainReturnMayCommitLateAndCannotBeClearedByAnotherReturn()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var id = Guid.NewGuid(); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateEffect = false;
        var background = Task.Run(async () => { await finish.Task; lateEffect = true; });
        Task<bool> Return() => throw new IOException("return response lost while native action still pending");
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", Return));
        Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        finish.SetResult(); await background.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(lateEffect);
    }

    [Fact]
    public async Task StalledReturnDoesNotBlockOtherOperationsAndCannotConfirmEarly()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var id = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        var pending = fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", async () => { entered.SetResult(); await finish.Task; return true; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-b", Guid.NewGuid(), "generation-1", "target-1", () => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { finish.SetResult(); }
        Assert.Equal(Result.Sealed, await pending);
    }

    [Fact]
    public async Task UnknownOldBeginBlocksNewOwnerBeforeLateEffect()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var oldId = Guid.NewGuid(); var newId = Guid.NewGuid(); var focus = "viewer";
        var allowLateCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateEffect = Task.Run(async () => { await allowLateCommit.Task; focus = "old-target"; });
        Task<bool> OldBegin() => throw new IOException("synthetic lost response while effect remains pending");
        Assert.Equal(Result.Unconfirmed, await fence.Begin(fence.Incarnation, "grant-a", oldId, "generation-1", "target-1", OldBegin));
        Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", oldId, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-b", newId, "generation-1", "target-1", () => { focus = "new-target"; return Task.FromResult(true); }));
        allowLateCommit.SetResult(); await lateEffect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("old-target", focus);
    }

    [Fact]
    public async Task OtherGrantCannotBeginWhileReturnPendingOrUnconfirmed()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var oldId = Guid.NewGuid(); var newId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "grant-a", oldId, "generation-1", "target-1", () => Task.FromResult(true)));
        var returning = fence.Seal(fence.Incarnation, "grant-a", oldId, "generation-1", "target-1", async () => { entered.SetResult(); await finish.Task; return false; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-b", newId, "generation-1", "target-1", () => Task.FromResult(true))); }
        finally { finish.SetResult(); }
        Assert.Equal(Result.Unconfirmed, await returning);
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-b", newId, "generation-1", "target-1", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task ConfirmedReturnFreesOwnershipForAnotherGrantAndUnknownSealDoesNotReturnOwner()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var oldId = Guid.NewGuid(); var newId = Guid.NewGuid(); var unrelatedId = Guid.NewGuid(); var wrongReturns = 0;
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "grant-a", oldId, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-b", newId, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-b", unrelatedId, "generation-1", "target-1", () => { wrongReturns++; return Task.FromResult(true); }));
        Assert.Equal(0, wrongReturns);
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-a", oldId, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "grant-b", newId, "generation-1", "target-1", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task PendingBeginBlocksSameAndOtherGrantWithoutBlockingUnrelatedSeal()
    {
        var fence = NewFence(5, previousProcessProvenStopped: true); var oldId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = fence.Begin(fence.Incarnation, "grant-a", oldId, "generation-1", "target-1", async () => { entered.SetResult(); await finish.Task; return true; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-a", Guid.NewGuid(), "generation-1", "target-1", () => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-b", Guid.NewGuid(), "generation-1", "target-1", () => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-b", Guid.NewGuid(), "generation-1", "target-1", () => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { finish.SetResult(); await pending; }
    }

    [Fact]
    public async Task FreshIncarnationDoesNotProvePreviousProcessWasStopped()
    {
        var oldFence = NewFence(2, previousProcessProvenStopped: true);
        var restartedFence = NewFence(2, previousProcessProvenStopped: false);
        var oldId = Guid.NewGuid(); var newId = Guid.NewGuid(); var calls = 0;
        Assert.Equal(Result.Unconfirmed, await oldFence.Begin(oldFence.Incarnation, "grant-a", oldId, "generation-1", "target-1", () => throw new IOException("unknown")));
        Assert.Equal(Result.Rejected, await restartedFence.Begin(oldFence.Incarnation, "grant-a", oldId, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Unconfirmed, await restartedFence.Begin(restartedFence.Incarnation, "grant-b", newId, "generation-1", "target-1", () => { calls++; return Task.FromResult(true); }));
        Assert.Equal(Result.Unconfirmed, await restartedFence.Seal(restartedFence.Incarnation, "grant-b", newId, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RepeatedOldSealDoesNotReturnOrReleaseNewOwner()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true);
        var oldId = Guid.NewGuid(); var newId = Guid.NewGuid(); var replayReturns = 0;
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "grant-a", oldId, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-a", oldId, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "grant-b", newId, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-a", oldId, "generation-1", "target-1", () => { replayReturns++; return Task.FromResult(true); }));
        Assert.Equal(0, replayReturns);
        Assert.Equal(Result.Active, await fence.Renew(fence.Incarnation, "grant-b", newId, "generation-1", "target-1"));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-a", Guid.NewGuid(), "generation-1", "target-1", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task SameIdMustNotAcceptDifferentGrantGenerationOrTargetDigest()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var id = Guid.NewGuid(); var calls = 0;
        Task<bool> Action() { calls++; return Task.FromResult(true); }
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "peer", id, "grant-v1", "target-a", Action));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "peer", id, "grant-v1", "target-b", Action));
        Assert.Equal(Result.Rejected, await fence.Renew(fence.Incarnation, "peer", id, "grant-v2", "target-a"));
        Assert.Equal(Result.Rejected, await fence.Seal(fence.Incarnation, "peer", id, "grant-v2", "target-a", Action));
        Assert.Equal(Result.Rejected, await fence.Seal(fence.Incarnation, "peer", id, "grant-v1", "target-b", Action));
        Assert.Equal(Result.Active, await fence.Renew(fence.Incarnation, "peer", id, "grant-v1", "target-a"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SealBeforeBeginAlsoBindsGrantGenerationAndTargetDigest()
    {
        var fence = NewFence(4, previousProcessProvenStopped: true); var id = Guid.NewGuid();
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "peer", id, "grant-v1", "target-a", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Seal(fence.Incarnation, "peer", id, "grant-v1", "target-b", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Seal(fence.Incarnation, "peer", id, "grant-v2", "target-a", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "peer", id, "grant-v1", "target-a", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task ExistingOperationCanBeSealedEvenWhenCapacityIsFull()
    {
        var fence = NewFence(1, previousProcessProvenStopped: true); var id = Guid.NewGuid();
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", Guid.NewGuid(), "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task AdvancingControlledClockDoesNotExpireTombstone()
    {
        // The model deliberately has no TTL or clock dependency.
        var clock = new TestClock(); var fence = NewFence(1, previousProcessProvenStopped: true); var id = Guid.NewGuid();
        var before = clock.GetUtcNow();
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        clock.Advance(TimeSpan.FromDays(365));
        Assert.True(clock.GetUtcNow() > before);
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-a", id, "generation-1", "target-1", () => Task.FromResult(true)));
        Assert.Equal(Result.Unconfirmed, await fence.Seal(fence.Incarnation, "grant-a", Guid.NewGuid(), "generation-1", "target-1", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task InvalidOperationAndPrincipalCannotCreateTombstonesOrRunActions()
    {
        var fence = NewFence(1, previousProcessProvenStopped: true); var calls = 0;
        Task<bool> Action() { calls++; return Task.FromResult(true); }
        Assert.Equal(Result.Rejected, await fence.Seal(fence.Incarnation, "", Guid.NewGuid(), "generation-1", "target-1", Action));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "grant-a", Guid.Empty, "generation-1", "target-1", Action));
        Assert.Equal(Result.Sealed, await fence.Seal(fence.Incarnation, "grant-a", Guid.NewGuid(), "generation-1", "target-1", Action));
        Assert.Equal(0, calls);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }
}
