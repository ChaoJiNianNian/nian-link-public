namespace PreviewFenceModel;

public sealed class RevocationTests
{
    [Fact]
    public async Task ForgedContextCannotUseARegisteredGrant()
    {
        var fence = new Fence(4, true); var id = Guid.NewGuid(); var effects = 0;
        var trusted = fence.RegisterTrustedGrant("peer", "v1");
        Assert.NotNull(trusted);
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, new object(), id, "target", () => { effects++; return Task.FromResult(true); }));
        Assert.Equal(0, effects);
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, trusted, id, "target", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task ReplayedRevokedCleanupDoesNotReturnOrReleaseNewOwner()
    {
        var fence = new TestFence(4, true); var oldId = Guid.NewGuid(); var newId = Guid.NewGuid(); var incorrectReturns = 0;
        Assert.True(fence.RegisterTrustedGrant("peer", "v1"));
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "peer", oldId, "v1", "target", () => Task.FromResult(true)));
        Assert.True(fence.ReplaceTrustedGrant("peer", "v1", "v2"));
        Assert.Equal(Result.Sealed, await fence.CleanupRevoked(oldId, () => Task.FromResult(true)));
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "peer", newId, "v2", "target", () => Task.FromResult(true)));
        Assert.Equal(Result.Sealed, await fence.CleanupRevoked(oldId, () => { incorrectReturns++; return Task.FromResult(true); }));
        Assert.Equal(0, incorrectReturns);
        Assert.Equal(Result.Active, await fence.Renew(fence.Incarnation, "peer", newId, "v2", "target"));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "peer", Guid.NewGuid(), "v2", "target", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task RevocationDuringPendingReturnDoesNotStartConcurrentCleanup()
    {
        var fence = new TestFence(4, true); var id = Guid.NewGuid(); var incorrectReturns = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fence.RegisterTrustedGrant("peer", "v1"));
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "peer", id, "v1", "target", () => Task.FromResult(true)));
        var pending = fence.Seal(fence.Incarnation, "peer", id, "v1", "target", async () => { entered.SetResult(); await finish.Task; return true; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(Result.Unconfirmed, fence.RevokeTrustedGrant("peer", "v1"));
            Assert.Equal(Result.Unconfirmed, await fence.CleanupRevoked(id, () => { incorrectReturns++; return Task.FromResult(true); }).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, incorrectReturns);
        }
        finally { finish.SetResult(); }
        Assert.Equal(Result.Sealed, await pending);
        Assert.Equal(Result.Sealed, await fence.CleanupRevoked(id, () => { incorrectReturns++; return Task.FromResult(true); }));
        Assert.Equal(0, incorrectReturns);
    }
    [Fact]
    public async Task TrustedReplacementAtomicallyRevokesOldGenerationWithoutPriorExplicitRevoke()
    {
        var fence = new TestFence(4, true); var id = Guid.NewGuid();
        Assert.True(fence.RegisterTrustedGrant("same-peer-and-fingerprint", "v1"));
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "same-peer-and-fingerprint", id, "v1", "target", () => Task.FromResult(true)));
        Assert.False(fence.RegisterTrustedGrant("same-peer-and-fingerprint", "v2"));
        Assert.True(fence.ReplaceTrustedGrant("same-peer-and-fingerprint", "v1", "v2"));
        Assert.Equal(Result.Rejected, await fence.Renew(fence.Incarnation, "same-peer-and-fingerprint", id, "v1", "target"));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "same-peer-and-fingerprint", Guid.NewGuid(), "v2", "target", () => Task.FromResult(true)));
        Assert.Equal(Result.Sealed, await fence.CleanupRevoked(id, () => Task.FromResult(true)));
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "same-peer-and-fingerprint", Guid.NewGuid(), "v2", "target", () => Task.FromResult(true)));
    }
    [Fact]
    public async Task RevocationDeniesOldBeginRenewAndSeal()
    {
        var fence = new TestFence(4, true); var id = Guid.NewGuid();
        Assert.True(fence.RegisterTrustedGrant("peer", "v1"));
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "peer", id, "v1", "target", () => Task.FromResult(true)));
        Assert.Equal(Result.Unconfirmed, fence.RevokeTrustedGrant("peer", "v1"));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "peer", Guid.NewGuid(), "v1", "target", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Renew(fence.Incarnation, "peer", id, "v1", "target"));
        Assert.Equal(Result.Rejected, await fence.Seal(fence.Incarnation, "peer", id, "v1", "target", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task NewGrantForSameIdentityDoesNotReviveOldGrantOrOwnership()
    {
        var fence = new TestFence(4, true); var id = Guid.NewGuid();
        Assert.True(fence.RegisterTrustedGrant("same-peer-and-fingerprint", "v1"));
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "same-peer-and-fingerprint", id, "v1", "target", () => Task.FromResult(true)));
        Assert.Equal(Result.Unconfirmed, fence.RevokeTrustedGrant("same-peer-and-fingerprint", "v1"));
        Assert.False(fence.RegisterTrustedGrant("same-peer-and-fingerprint", "v1"));
        Assert.True(fence.RegisterTrustedGrant("same-peer-and-fingerprint", "v2"));
        Assert.Equal(Result.Rejected, await fence.Renew(fence.Incarnation, "same-peer-and-fingerprint", id, "v1", "target"));
        Assert.Equal(Result.Rejected, await fence.Seal(fence.Incarnation, "same-peer-and-fingerprint", id, "v2", "target", () => Task.FromResult(true)));
        Assert.Equal(Result.Rejected, await fence.Begin(fence.Incarnation, "same-peer-and-fingerprint", Guid.NewGuid(), "v2", "target", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task RevocationWhileBeginInFlightRemainsUnconfirmedAndDoesNotBlockOtherRequests()
    {
        var fence = new TestFence(4, true); var id = Guid.NewGuid();
        Assert.True(fence.RegisterTrustedGrant("peer", "v1"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = fence.Begin(fence.Incarnation, "peer", id, "v1", "target", async () => { entered.SetResult(); await finish.Task; return true; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(Result.Unconfirmed, fence.RevokeTrustedGrant("peer", "v1"));
            Assert.Equal(Result.Unconfirmed, await fence.CleanupRevoked(id, () => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(Result.Rejected, await fence.Renew(fence.Incarnation, "peer", id, "v1", "target").WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { finish.SetResult(); }
        Assert.Equal(Result.Unconfirmed, await pending);
        Assert.Equal(Result.Sealed, await fence.CleanupRevoked(id, () => Task.FromResult(true)));
    }

    [Fact]
    public async Task TrustedInternalCleanupCanReturnRevokedOwnerWithoutReauthorizingIt()
    {
        var fence = new TestFence(4, true); var id = Guid.NewGuid(); var returns = 0;
        Assert.True(fence.RegisterTrustedGrant("peer", "v1"));
        Assert.Equal(Result.Active, await fence.Begin(fence.Incarnation, "peer", id, "v1", "target", () => Task.FromResult(true)));
        Assert.Equal(Result.Unconfirmed, fence.RevokeTrustedGrant("peer", "v1"));
        Assert.Equal(Result.Sealed, await fence.CleanupRevoked(id, () => { returns++; return Task.FromResult(true); }));
        Assert.Equal(1, returns);
        Assert.Equal(Result.Rejected, await fence.Renew(fence.Incarnation, "peer", id, "v1", "target"));
    }
}
