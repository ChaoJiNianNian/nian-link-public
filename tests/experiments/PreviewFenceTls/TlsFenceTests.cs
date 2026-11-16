using PreviewFenceModel;
using System.Security.Authentication;

namespace PreviewFenceTls;

public sealed class TlsFenceTests
{
    [Theory]
    [InlineData("seal")]
    [InlineData("renew")]
    public async Task SharedAllowedTargetDoesNotLetAnotherPrincipalSealOrRenewAnOperation(string action)
    {
        await using var server = new TestTlsServer(); server.Start(); var id = Guid.NewGuid();
        Assert.Equal(Result.Active, await server.SendAsync("a", "begin", id, target: "target"));
        Assert.Equal(Result.Rejected, await server.SendAsync("b", action, id, target: "target"));
        Assert.Equal(0, server.FakeReturns);
        Assert.Equal(Result.Active, await server.SendAsync("a", "renew", id, target: "target"));
        Assert.Equal(Result.Sealed, await server.SendAsync("a", "seal", id, target: "target"));
        Assert.Equal(1, server.FakeReturns);
    }

    [Theory]
    [InlineData("seal")]
    [InlineData("renew")]
    public async Task TwoAllowedTargetsDoNotLetTheSamePrincipalRebindAnOperation(string action)
    {
        await using var server = new TestTlsServer(); server.Start(); var id = Guid.NewGuid();
        Assert.Equal(Result.Active, await server.SendAsync("a", "begin", id, target: "one"));
        Assert.Equal(Result.Rejected, await server.SendAsync("a", action, id, target: "target"));
        Assert.Equal(0, server.FakeReturns);
        Assert.Equal(Result.Active, await server.SendAsync("a", "renew", id, target: "one"));
        Assert.Equal(Result.Sealed, await server.SendAsync("a", "seal", id, target: "one"));
        Assert.Equal(1, server.FakeReturns);
    }

    [Fact]
    public async Task ValidTokenCannotBeginOnAnotherPairsTarget()
    {
        await using var server = new TestTlsServer(); server.Start(); var id = Guid.NewGuid();
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "begin", id, target: "target-b"));
        Assert.Equal(0, server.FakeStarts);
        Assert.Equal(Result.Active, await server.SendAsync("b", "begin", id, target: "target-b"));
        Assert.Equal(1, server.FakeStarts);
    }

    [Fact]
    public async Task ValidTokenCannotSealOrRenewAnotherPairsTargetOrBlockItsOwner()
    {
        await using var server = new TestTlsServer(); server.Start(); var id = Guid.NewGuid();
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "seal", id, target: "target-b"));
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "renew", id, target: "target-b"));
        Assert.Equal(Result.Active, await server.SendAsync("b", "begin", id, target: "target-b"));
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "seal", id, target: "target-b"));
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "renew", id, target: "target-b"));
        Assert.Equal(Result.Active, await server.SendAsync("b", "renew", id, target: "target-b"));
        Assert.Equal(0, server.FakeReturns);
        Assert.Equal(Result.Sealed, await server.SendAsync("b", "seal", id, target: "target-b"));
    }

    [Fact]
    public async Task RepairedGrantCannotExpandAllowedTargetsAndOldTokenStaysRevoked()
    {
        await using var server = new TestTlsServer(); server.Start();
        var oldToken = server.GetTestToken("a");
        server.ReplaceTestGrant("a");
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "begin", Guid.NewGuid(), target: "target-a", token: oldToken));
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "begin", Guid.NewGuid(), target: "target-b"));
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "seal", Guid.NewGuid(), target: "target-b"));
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "renew", Guid.NewGuid(), target: "target-b"));
        Assert.Equal(0, server.FakeStarts);
        Assert.Equal(Result.Active, await server.SendAsync("a", "begin", Guid.NewGuid(), target: "target-a"));
    }

    [Fact]
    public async Task SealOverSeparateTlsConnectionRejectsPreviouslyArrivedButUndispatchedBegin()
    {
        await using var server = new TestTlsServer();
        server.DelayDispatch = true;
        server.Start();
        var id = Guid.NewGuid();
        var begin = server.SendAsync("a", "begin", id);
        await server.BeginReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(Result.Sealed, await server.SendAsync("a", "seal", id));
            server.ReleaseDispatch.TrySetResult();
            Assert.Equal(Result.Rejected, await begin.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, server.FakeStarts);
        }
        finally { server.ReleaseDispatch.TrySetResult(); }
    }

    [Fact]
    public async Task InFlightActionKeepsSealUnconfirmedAndBlocksNewOwnerUntilReturnCompletes()
    {
        await using var server = new TestTlsServer { DelayAction = true };
        server.Start();
        var oldId = Guid.NewGuid(); var newId = Guid.NewGuid();
        var begin = server.SendAsync("a", "begin", oldId);
        await server.ActionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(Result.Unconfirmed, await server.SendAsync("a", "seal", oldId));
            Assert.Equal(Result.Rejected, await server.SendAsync("b", "begin", newId));
            Assert.Equal(0, server.FakeReturns);
            server.ReleaseAction.TrySetResult();
            Assert.Equal(Result.Unconfirmed, await begin.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(Result.Sealed, await server.SendAsync("a", "seal", oldId));
            Assert.Equal(1, server.FakeReturns);
            Assert.Equal(Result.Active, await server.SendAsync("b", "begin", newId));
        }
        finally { server.ReleaseAction.TrySetResult(); }
    }

    [Fact]
    public async Task TokenAndPinAreRequiredAndCannotProduceActions()
    {
        await using var server = new TestTlsServer(); server.Start();
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "begin", Guid.NewGuid(), token: "wrong"));
        await Assert.ThrowsAnyAsync<AuthenticationException>(() => server.SendAsync("a", "begin", Guid.NewGuid(), pin: new byte[32]));
        Assert.Equal(0, server.FakeStarts);
    }

    [Fact]
    public async Task DifferentPrincipalOrTargetAndOldIncarnationCannotOperateOnExistingId()
    {
        await using var server = new TestTlsServer(); server.Start(); var id = Guid.NewGuid();
        Assert.Equal(Result.Active, await server.SendAsync("a", "begin", id, target: "one"));
        Assert.Equal(Result.Rejected, await server.SendAsync("b", "seal", id, target: "one"));
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "seal", id, target: "two"));
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "seal", id, target: "one", incarnation: Guid.NewGuid()));
        Assert.Equal(Result.Active, await server.SendAsync("a", "renew", id, target: "one"));
        Assert.Equal(0, server.FakeReturns);
        Assert.Equal(Result.Sealed, await server.SendAsync("a", "seal", id, target: "one"));
    }

    [Fact]
    public async Task OldVersionAndUnknownActionCannotBypassProtectedExperiment()
    {
        await using var server = new TestTlsServer(); server.Start(); var id = Guid.NewGuid();
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "begin", id, version: 1));
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "preview-begin", id));
        Assert.Equal(0, server.FakeStarts);
        Assert.Equal(Result.Sealed, await server.SendAsync("a", "seal", id));
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "begin", id));
    }

    [Fact]
    public async Task RevokingGrantDuringInFlightActionFencesItAndBlocksAnotherOwner()
    {
        await using var server = new TestTlsServer { DelayAction = true }; server.Start(); var id = Guid.NewGuid();
        var begin = server.SendAsync("a", "begin", id);
        await server.ActionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(Result.Unconfirmed, server.Revoke("a"));
            Assert.Equal(Result.Rejected, await server.SendAsync("a", "seal", id));
            Assert.Equal(Result.Rejected, await server.SendAsync("b", "begin", Guid.NewGuid()));
            server.ReleaseAction.TrySetResult();
            Assert.Equal(Result.Unconfirmed, await begin.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(Result.Rejected, await server.SendAsync("a", "renew", id));
            Assert.Equal(0, server.FakeReturns);
        }
        finally { server.ReleaseAction.TrySetResult(); }
    }

    [Fact]
    public async Task LostResponsesAndRepeatedRequestsNeverRepeatSyntheticActions()
    {
        await using var server = new TestTlsServer(); server.Start(); var id = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => server.SendAsync("a", "begin", id, dropResponse: true));
        Assert.Equal(Result.Active, await server.SendAsync("a", "begin", id));
        Assert.Equal(1, server.FakeStarts);
        await Assert.ThrowsAsync<IOException>(() => server.SendAsync("a", "seal", id, dropResponse: true));
        Assert.Equal(Result.Sealed, await server.SendAsync("a", "seal", id));
        Assert.Equal(1, server.FakeReturns);
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "begin", id));
    }

    [Fact]
    public async Task MalformedTargetDoesNotCreateAnOperationOrUseAnAction()
    {
        await using var server = new TestTlsServer(); server.Start(); var id = Guid.NewGuid();
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "begin", id, target: " target"));
        Assert.Equal(Result.Rejected, await server.SendAsync("a", "begin", id, target: new string('a', 65)));
        Assert.Equal(0, server.FakeStarts);
        Assert.Equal(Result.Sealed, await server.SendAsync("a", "seal", id, target: "target"));
    }

    [Fact]
    public async Task RePairRejectsOldTokenAndCannotReleaseUnconfirmedOldOwner()
    {
        await using var server = new TestTlsServer { DelayAction = true }; server.Start(); var id = Guid.NewGuid();
        var oldToken = server.GetTestToken("a");
        var begin = server.SendAsync("a", "begin", id);
        await server.ActionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            server.ReplaceTestGrant("a");
            Assert.Equal(Result.Rejected, await server.SendAsync("a", "seal", id, token: oldToken));
            Assert.Equal(Result.Rejected, await server.SendAsync("a", "begin", Guid.NewGuid()));
            server.ReleaseAction.TrySetResult();
            Assert.Equal(Result.Unconfirmed, await begin.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(Result.Rejected, await server.SendAsync("a", "seal", id));
            Assert.Equal(0, server.FakeReturns);
        }
        finally { server.ReleaseAction.TrySetResult(); }
    }
}
