#if NIAN_FENCE_RESEARCH
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace EmergencyReturn.Research;

// These exercise the actual manager, not the separate research wrapper.
// The requester is assumed to be authenticated by a future server: it is not
// safe to expose the experimental method as an RPC without that boundary.
public sealed class EmergencyManagerCandidateTests
{
    [Fact]
    public async Task AttemptFreezesOldPreviewReadDisposeAndDisplayEvents()
    {
        var clock = new ResearchClock();
        var keyboard = new ResearchKeyboard("peer");
        var display = new ResearchDisplay();
        var manager = new MultiPeerPreviewManager("host", ["peer", "other"], keyboard, clock, display);
        Assert.True((await manager.BeginAsync("peer", "peer")).IsActive);
        Assert.Equal("host", keyboard.Active);
        Assert.Equal(EmergencyReturnOutcome.AlreadyHostUnconfirmed, await manager.TryEmergencyReturnAsync("peer"));
        clock.Advance(TimeSpan.FromSeconds(9));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReadForDeviceAsync("peer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.BeginAsync("peer", "peer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SwitchNormalAsync("peer", "peer"));
        await manager.OnFocusChangedAsync(new FocusChangedEvent("host", keyboard.Revision));
        Assert.Equal(EmergencyReturnOutcome.AlreadyHostUnconfirmed, await manager.TryEmergencyReturnAsync("peer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.Equal("host", keyboard.Active);
        Assert.Equal(0, display.Calls);
    }

    [Fact]
    public async Task OnlyApprovedConnectedParticipantCanAttempt()
    {
        var keyboard = new ResearchKeyboard("peer");
        var manager = new MultiPeerPreviewManager("host", ["peer", "other"], keyboard);
        Assert.True((await manager.BeginAsync("peer", "peer")).IsActive);
        var handoffs = keyboard.SwitchCalls;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.TryEmergencyReturnAsync("outside"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.TryEmergencyReturnAsync("host"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.TryEmergencyReturnAsync("other"));
        Assert.Equal(handoffs, keyboard.SwitchCalls);
        Assert.Equal(EmergencyReturnOutcome.AlreadyHostUnconfirmed, await manager.TryEmergencyReturnAsync("peer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task UnconnectedOrUnrelatedFocusedPeerIsRejectedBeforeAnyHandoff()
    {
        var keyboard = new ResearchKeyboard("other") { Connected = ["other"] };
        var manager = new MultiPeerPreviewManager("host", ["peer", "other"], keyboard);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.TryEmergencyReturnAsync("peer"));
        Assert.Equal(0, keyboard.SwitchCalls);
        Assert.Equal("other", keyboard.Active);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task UnknownLocksEveryPreviewEntryAndAnotherPeerCannotTakeOwnership()
    {
        var keyboard = new ResearchKeyboard("peer");
        var manager = new MultiPeerPreviewManager("host", ["peer", "other"], keyboard);
        var preview = await manager.BeginAsync("peer", "peer");
        Assert.Equal(EmergencyReturnOutcome.AlreadyHostUnconfirmed, await manager.TryEmergencyReturnAsync("peer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RenewAsync("peer", preview.Id!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EndAsync("peer", preview.Id!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.PrepareNormalSwitchAsync("peer"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.TryEmergencyReturnAsync("other"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisposeAsync().AsTask());
        Assert.Equal("host", keyboard.Active);
    }

    [Fact]
    public async Task IgnoredCancellationDoesNotHangManagerAndLateNativeIsUnconfirmed()
    {
        var keyboard = new ResearchKeyboard("peer") { HoldNextSwitch = true };
        var display = new ResearchDisplay();
        var manager = new MultiPeerPreviewManager("host", ["peer"], keyboard, display: display);
        var attempt = manager.TryEmergencyReturnAsync("peer");
        await keyboard.SwitchEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(EmergencyReturnOutcome.AttemptedUnconfirmed, await attempt.WaitAsync(TimeSpan.FromSeconds(6)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)));
        keyboard.ReleaseSwitch.TrySetResult();
        await keyboard.SwitchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("host", keyboard.Active);
        Assert.Equal(0, display.Calls);
        // The pending native request can still execute. Success is never returned.
    }

    [Fact]
    public async Task AlreadyQueuedNativeCommandCanStillMoveFocusAfterAttempt()
    {
        var keyboard = new ResearchKeyboard("host");
        var manager = new MultiPeerPreviewManager("host", ["peer"], keyboard);
        var pending = keyboard.SubmitSyntheticPriorCommandAsync("peer", "host", keyboard.Revision);
        await keyboard.PriorQueued.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(EmergencyReturnOutcome.AlreadyHostUnconfirmed, await manager.TryEmergencyReturnAsync("peer"));
        keyboard.ReleasePrior.TrySetResult();
        Assert.True(await pending);
        Assert.Equal("peer", keyboard.Active);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task BusyManagerTimesOutWithoutClaimingCleanupAndCanRetryAfterRelease()
    {
        var keyboard = new ResearchKeyboard("peer") { HoldNextSwitch = true };
        var manager = new MultiPeerPreviewManager("host", ["peer"], keyboard);
        var begin = manager.BeginAsync("peer", "peer");
        await keyboard.SwitchEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.TryEmergencyReturnAsync("peer"));
        await Assert.ThrowsAsync<TimeoutException>(() => manager.DisposeAsync().AsTask());
        keyboard.ReleaseSwitch.TrySetResult();
        await begin.WaitAsync(TimeSpan.FromSeconds(3));
        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
    }
}
#endif
