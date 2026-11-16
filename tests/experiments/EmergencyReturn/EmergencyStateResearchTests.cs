using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace EmergencyReturn.Research;

public sealed class EmergencyStateResearchTests
{
    [Fact]
    public async Task FrozenWrapperDoesNotReadExpireDisposeOrBeginOldPreview()
    {
        var clock = new ResearchClock(); var input = new ResearchKeyboard("peer"); var display = new ResearchDisplay();
        var manager = new MultiPeerPreviewManager("host", ["peer"], input, clock, display);
        var auth = new ResearchAuthorization(); auth.Add("peer", "test-token");
        var owner = new EmergencyManagerResearch(manager, input, auth, "host", ["peer", "other"]);
        try
        {
            var preview = await owner.BeginAsync("peer");
            Assert.True(preview.IsActive);
            Assert.Equal(ResearchReturn.AlreadyHostUnconfirmed, await owner.TryReturnAsync("peer", "test-token"));
            clock.Advance(TimeSpan.FromSeconds(9));
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReadAsync("peer"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.BeginAsync("peer"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.RenewAsync("peer", preview.Id!));
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.NormalSwitchAsync("peer", "peer"));
            await owner.DisposeAsync();
            Assert.Equal("host", input.Active);
            Assert.Equal(0, display.Calls);
        }
        finally { await manager.DisposeAsync(); } // Only synthetic cleanup after assertions.
    }

    [Fact]
    public async Task FrozenFocusEventsDoNotApplyDisplay()
    {
        var input = new ResearchKeyboard("peer"); var display = new ResearchDisplay();
        var manager = new MultiPeerPreviewManager("host", ["peer"], input, display: display);
        var auth = new ResearchAuthorization(); auth.Add("peer", "test-token");
        await using var owner = new EmergencyManagerResearch(manager, input, auth, "host", ["peer", "other"]);
        Assert.Equal(ResearchReturn.AttemptedUnconfirmed, await owner.TryReturnAsync("peer", "test-token"));
        await owner.OnFocusAsync(new FocusChangedEvent("host", input.Revision));
        Assert.Equal(0, display.Calls);
        Assert.True(owner.Unconfirmed);
        await manager.DisposeAsync(); // Synthetic cleanup.
    }

    [Fact]
    public async Task OtherDevicesOrWrongCredentialsCannotRequestReturn()
    {
        var input = new ResearchKeyboard("other"); var display = new ResearchDisplay();
        var manager = new MultiPeerPreviewManager("host", ["peer", "other"], input, display: display);
        var auth = new ResearchAuthorization(); auth.Add("peer", "test-token");
        await using var owner = new EmergencyManagerResearch(manager, input, auth, "host", ["peer", "other"]);
        Assert.Equal(ResearchReturn.Unauthorized, await owner.TryReturnAsync("peer", "wrong"));
        Assert.Equal(ResearchReturn.Rejected, await owner.TryReturnAsync("peer", "test-token"));
        Assert.False(owner.Unconfirmed);
        Assert.Equal("other", input.Active);
        Assert.Equal(0, input.SwitchCalls);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task CredentialForUnlistedSyntheticPeerIsRejected()
    {
        var input = new ResearchKeyboard("outsider") { Connected = ["outsider"] };
        var manager = new MultiPeerPreviewManager("host", ["peer", "other"], input);
        var auth = new ResearchAuthorization(); auth.Add("outsider", "test-token");
        await using var owner = new EmergencyManagerResearch(manager, input, auth, "host", ["peer", "other"]);
        Assert.Equal(ResearchReturn.Unauthorized, await owner.TryReturnAsync("outsider", "test-token"));
        Assert.False(owner.Unconfirmed);
        Assert.Equal(0, input.SwitchCalls);
    }

    [Fact]
    public async Task CredentialClaimingSyntheticHostIsRejected()
    {
        var input = new ResearchKeyboard("host");
        var manager = new MultiPeerPreviewManager("host", ["peer", "other"], input);
        var auth = new ResearchAuthorization(); auth.Add("host", "test-token");
        await using var owner = new EmergencyManagerResearch(manager, input, auth, "host", ["peer", "other"]);
        Assert.Equal(ResearchReturn.Unauthorized, await owner.TryReturnAsync("host", "test-token"));
        Assert.False(owner.Unconfirmed);
        Assert.Equal(0, input.SwitchCalls);
    }

    [Fact]
    public async Task RequesterNotInSyntheticConnectedListIsRejected()
    {
        var input = new ResearchKeyboard("peer") { Connected = ["other"] };
        var manager = new MultiPeerPreviewManager("host", ["peer", "other"], input);
        var auth = new ResearchAuthorization(); auth.Add("peer", "test-token");
        await using var owner = new EmergencyManagerResearch(manager, input, auth, "host", ["peer", "other"]);
        Assert.Equal(ResearchReturn.Rejected, await owner.TryReturnAsync("peer", "test-token"));
        Assert.False(owner.Unconfirmed);
        Assert.Equal(0, input.SwitchCalls);
    }

    [Fact]
    public async Task ARequesterCannotTakeOverAnotherPeersPreview()
    {
        var input = new ResearchKeyboard("other"); var display = new ResearchDisplay();
        var manager = new MultiPeerPreviewManager("host", ["peer", "other"], input, display: display);
        var auth = new ResearchAuthorization(); auth.Add("peer", "test-token");
        await using var owner = new EmergencyManagerResearch(manager, input, auth, "host", ["peer", "other"]);
        try
        {
            Assert.True((await owner.BeginAsync("other")).IsActive);
            Assert.Equal("host", input.Active);
            var calls = input.SwitchCalls;
            Assert.Equal(ResearchReturn.Rejected, await owner.TryReturnAsync("peer", "test-token"));
            Assert.Equal(calls, input.SwitchCalls);
            Assert.False(owner.Unconfirmed);
        }
        finally { await manager.DisposeAsync(); }
    }

    [Fact]
    public async Task RepeatedReturnRemainsUnconfirmedAndCannotResumeControl()
    {
        var input = new ResearchKeyboard("peer"); var display = new ResearchDisplay();
        var manager = new MultiPeerPreviewManager("host", ["peer"], input, display: display);
        var auth = new ResearchAuthorization(); auth.Add("peer", "test-token");
        await using var owner = new EmergencyManagerResearch(manager, input, auth, "host", ["peer", "other"]);
        Assert.Equal(ResearchReturn.AttemptedUnconfirmed, await owner.TryReturnAsync("peer", "test-token"));
        Assert.Equal(ResearchReturn.AlreadyHostUnconfirmed, await owner.TryReturnAsync("peer", "test-token"));
        Assert.True(owner.Unconfirmed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.BeginAsync("peer"));
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task SyntheticActionAlreadyAdmittedCanCompleteAfterRevocation()
    {
        var input = new ResearchKeyboard("peer") { HoldNextSwitch = true }; var display = new ResearchDisplay();
        var manager = new MultiPeerPreviewManager("host", ["peer"], input, display: display);
        var auth = new ResearchAuthorization(); auth.Add("peer", "test-token");
        await using var owner = new EmergencyManagerResearch(manager, input, auth, "host", ["peer", "other"]);
        var attempt = owner.TryReturnAsync("peer", "test-token");
        try
        {
            await input.SwitchEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(auth.Revoke("peer"));
            input.ReleaseSwitch.TrySetResult();
            Assert.Equal(ResearchReturn.AttemptedUnconfirmed, await attempt);
            Assert.Equal("host", input.Active); // The already-admitted synthetic command still ran.
            Assert.Equal(ResearchReturn.Unauthorized, await owner.TryReturnAsync("peer", "test-token"));
            Assert.True(owner.Unconfirmed);
            Assert.Equal(0, display.Calls);
        }
        finally { input.ReleaseSwitch.TrySetResult(); await manager.DisposeAsync(); }
    }

    [Fact]
    public async Task DisposalWaitsForAnInFlightSyntheticReturn()
    {
        var input = new ResearchKeyboard("peer") { HoldNextSwitch = true }; var display = new ResearchDisplay();
        var manager = new MultiPeerPreviewManager("host", ["peer"], input, display: display);
        var auth = new ResearchAuthorization(); auth.Add("peer", "test-token");
        var owner = new EmergencyManagerResearch(manager, input, auth, "host", ["peer", "other"]);
        var attempt = owner.TryReturnAsync("peer", "test-token");
        try
        {
            await input.SwitchEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var closing = owner.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted); // A non-settling backend can block closure in this model.
            input.ReleaseSwitch.TrySetResult();
            Assert.Equal(ResearchReturn.AttemptedUnconfirmed, await attempt);
            await closing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(owner.Unconfirmed);
            Assert.Equal(0, display.Calls);
        }
        finally { input.ReleaseSwitch.TrySetResult(); await manager.DisposeAsync(); }
    }

    [Fact]
    public async Task PriorSyntheticNativeCommandCanStillRunAfterEmergencyAttempt()
    {
        var input = new ResearchKeyboard("host"); var display = new ResearchDisplay();
        var manager = new MultiPeerPreviewManager("host", ["peer"], input, display: display);
        var auth = new ResearchAuthorization(); auth.Add("peer", "test-token");
        await using var owner = new EmergencyManagerResearch(manager, input, auth, "host", ["peer", "other"]);
        var before = await input.GetStateAsync(default);
        var prior = input.SubmitSyntheticPriorCommandAsync("peer", before.ActiveComputer!, before.Revision);
        try
        {
            await input.PriorQueued.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(ResearchReturn.AlreadyHostUnconfirmed, await owner.TryReturnAsync("peer", "test-token"));
            input.ReleasePrior.TrySetResult();
            Assert.True(await prior);
            await owner.OnFocusAsync(new FocusChangedEvent("peer", input.Revision));
            Assert.Equal("peer", input.Active); // The already submitted synthetic command still ran.
            Assert.Equal(0, display.Calls);
            Assert.True(owner.Unconfirmed);
        }
        finally { input.ReleasePrior.TrySetResult(); await manager.DisposeAsync(); }
    }
}
