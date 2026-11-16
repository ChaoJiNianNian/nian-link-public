using DesktopToolbox.DeskLink;
using NianLink.MultiDevice;

namespace PeerControlOwnerTests;

public sealed class OwnerTests
{
    [Fact]
    public async Task RetiredConnectionCannotIssueBeginReadEndOrSwitchAfterNewConnectionBinds()
    {
        var owner = new PeerControlOwner();
        var first = new FakeTransport();
        owner.Bind(first);
        var old = Control(owner, first);
        owner.Retire(first);
        var second = new FakeTransport();
        owner.Bind(second);
        var current = Control(owner, second);
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.BeginPreviewAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.ReadPreviewAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.EndPreviewAsync("one"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.SwitchDetailedAsync("host"));
        Assert.Equal(0, first.BeginCalls + first.EndCalls + first.SwitchCalls + first.ReadCalls);
        Assert.Equal(DeskflowPreviewPhase.Active, (await current.BeginPreviewAsync()).Phase);
    }

    [Fact]
    public async Task ActivePreviewSurvivesWindowReplacementAndPreventsNewHandoffUntilEnd()
    {
        var owner = new PeerControlOwner();
        var transport = new FakeTransport(); owner.Bind(transport);
        var oldWindow = Control(owner, transport);
        await oldWindow.BeginPreviewAsync();
        var newWindow = Control(owner, transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => newWindow.BeginPreviewAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => newWindow.SwitchDetailedAsync("peer"));
        await oldWindow.EndPreviewAsync("one");
        Assert.Equal(DeskflowPreviewPhase.Active, (await newWindow.BeginPreviewAsync()).Phase);
    }

    [Fact]
    public async Task RetiringActiveConnectionLatchesUnknownAcrossStart()
    {
        var owner = new PeerControlOwner();
        var oldTransport = new FakeTransport(); owner.Bind(oldTransport);
        await Control(owner, oldTransport).BeginPreviewAsync();
        owner.Retire(oldTransport);
        var replacement = new FakeTransport(); owner.Bind(replacement);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Control(owner, replacement).BeginPreviewAsync());
    }

    [Fact]
    public async Task RetiringInFlightConnectionCannotBeClearedByItsLateReply()
    {
        var owner = new PeerControlOwner();
        var old = new FakeTransport { BeginPending = new(TaskCreationOptions.RunContinuationsAsynchronously) }; owner.Bind(old);
        var pending = Control(owner, old).BeginPreviewAsync();
        owner.Retire(old);
        var replacement = new FakeTransport(); owner.Bind(replacement);
        old.BeginPending.SetResult(Active());
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Control(owner, replacement).BeginPreviewAsync());
    }

    [Fact]
    public async Task StoppingBlocksNewWorkButAllowsKnownPreviewReturnBeforeRetirement()
    {
        var owner = new PeerControlOwner();
        var transport = new FakeTransport(); owner.Bind(transport);
        var control = Control(owner, transport);
        await control.BeginPreviewAsync();
        owner.BeginStopping(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.BeginPreviewAsync());
        await control.EndPreviewAsync("one");
        owner.Retire(transport);
        var replacement = new FakeTransport(); owner.Bind(replacement);
        Assert.Equal(DeskflowPreviewPhase.Active, (await Control(owner, replacement).BeginPreviewAsync()).Phase);
    }

    [Fact]
    public async Task ViewingRenewDoesNotPretendOldSessionIsSafelySettled()
    {
        var owner = new PeerControlOwner();
        var transport = new FakeTransport { RenewAsViewing = true }; owner.Bind(transport);
        var control = Control(owner, transport);
        await control.BeginPreviewAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.RenewPreviewAsync("one"));
        Assert.False(control.PreviewHandoffSupported);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Control(owner, transport).BeginPreviewAsync());
    }

    [Fact]
    public async Task RemoteInitiatedPreviewReturnIsAttemptedButNotFalselyConfirmed()
    {
        var owner = new PeerControlOwner();
        var transport = new FakeTransport { ReadAsActive = true }; owner.Bind(transport);
        var control = Control(owner, transport);
        Assert.Equal(DeskflowPreviewPhase.Active, (await control.ReadPreviewAsync()).Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.EndPreviewAsync("one"));
        Assert.Equal(1, transport.EndCalls);
        Assert.False(control.PreviewHandoffSupported);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.BeginPreviewAsync());
    }

    [Fact]
    public async Task FailedBeginSurvivesNewWindowAndNewConnectionAndBlocksPeerSwitch()
    {
        var owner = new PeerControlOwner();
        var first = new FakeTransport { FailBegin = true };
        owner.Bind(first);
        var old = Control(owner, first);
        await Assert.ThrowsAsync<IOException>(() => old.BeginPreviewAsync());
        owner.Retire(first);
        var fresh = new FakeTransport();
        owner.Bind(fresh);
        var reopened = Control(owner, fresh);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.BeginPreviewAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.RenewPreviewAsync("old"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SwitchDetailedAsync("peer"));
        Assert.Equal(0, fresh.BeginCalls);
        Assert.Equal(0, fresh.SwitchCalls);
    }

    [Fact]
    public async Task EmergencyReturnIsAttemptedButCannotClaimOrClearUnknown()
    {
        var owner = new PeerControlOwner();
        var fake = new FakeTransport { FailBegin = true };
        owner.Bind(fake);
        var control = Control(owner, fake);
        await Assert.ThrowsAsync<IOException>(() => control.BeginPreviewAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.SwitchDetailedAsync("host"));
        Assert.Equal(1, fake.SwitchCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.EndPreviewAsync("old"));
        Assert.Equal(1, fake.EndCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.BeginPreviewAsync());
    }

    [Fact]
    public async Task BeginIsReservedAcrossAllAdaptersAndLateReplyCannotClearEmergency()
    {
        var owner = new PeerControlOwner();
        var first = new FakeTransport { BeginPending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        owner.Bind(first);
        var old = Control(owner, first);
        var current = Control(owner, first);
        var starting = old.BeginPreviewAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => current.BeginPreviewAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => current.SwitchDetailedAsync("host"));
        first.BeginPending.SetResult(Active());
        await Assert.ThrowsAsync<InvalidOperationException>(() => starting);
        await Assert.ThrowsAsync<InvalidOperationException>(() => current.BeginPreviewAsync());
    }

    [Fact]
    public async Task FailedEndStaysUnknownEvenIfLaterReadSaysViewing()
    {
        var owner = new PeerControlOwner();
        var fake = new FakeTransport { FailEnd = true };
        owner.Bind(fake);
        var control = Control(owner, fake);
        await control.BeginPreviewAsync();
        await Assert.ThrowsAsync<IOException>(() => control.EndPreviewAsync("one"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.ReadPreviewAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.BeginPreviewAsync());
    }

    [Fact]
    public async Task EndReturningDifferentActiveSessionRemainsUnknownButAllowsEmergencyAttempts()
    {
        var owner = new PeerControlOwner();
        var fake = new FakeTransport { EndResult = new() { Phase = DeskflowPreviewPhase.Active, Id = "two" } };
        owner.Bind(fake);
        var control = Control(owner, fake);
        await control.BeginPreviewAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.EndPreviewAsync("one"));
        Assert.True(owner.IsUncertain);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.BeginPreviewAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.EndPreviewAsync("two"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.SwitchDetailedAsync("host"));
        Assert.Equal(1, fake.BeginCalls);
        Assert.Equal(2, fake.EndCalls);
        Assert.Equal(1, fake.SwitchCalls);
    }

    [Fact]
    public async Task DifferentActiveSessionDuringStopCannotUnlockReplacement()
    {
        var owner = new PeerControlOwner();
        var fake = new FakeTransport { EndResult = new() { Phase = DeskflowPreviewPhase.Active, Id = "two" } };
        owner.Bind(fake);
        var control = Control(owner, fake);
        await control.BeginPreviewAsync();
        owner.BeginStopping(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.EndPreviewAsync("one"));
        owner.Retire(fake);
        var replacement = new FakeTransport();
        owner.Bind(replacement);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Control(owner, replacement).BeginPreviewAsync());
        Assert.Equal(0, replacement.BeginCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Control(owner, replacement).SwitchDetailedAsync("host"));
        Assert.Equal(1, replacement.SwitchCalls);
    }

    [Fact]
    public async Task IgnoredCancellationCannotLetLateBeginUnlockNewConnection()
    {
        var owner = new PeerControlOwner();
        var fake = new FakeTransport { BeginPending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        owner.Bind(fake);
        var old = Control(owner, fake);
        using var cancel = new CancellationTokenSource();
        var starting = old.BeginPreviewAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(TimeSpan.FromMilliseconds(250)));
        fake.BeginPending.SetResult(Active());
        owner.Retire(fake);
        var fresh = new FakeTransport(); owner.Bind(fresh);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Control(owner, fresh).BeginPreviewAsync());
    }

    [Theory]
    [InlineData("host")]
    [InlineData("peer")]
    public async Task FailedNormalSwitchLatchesUnknownAcrossReplacement(string target)
    {
        var owner = new PeerControlOwner();
        var first = new FakeTransport { FailSwitch = true }; owner.Bind(first);
        var old = Control(owner, first);
        await Assert.ThrowsAsync<IOException>(() => old.SwitchDetailedAsync(target));
        owner.Retire(first);
        var fresh = new FakeTransport(); owner.Bind(fresh);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Control(owner, fresh).BeginPreviewAsync());
    }

    [Fact]
    public async Task CanceledNormalReturnStaysUnconfirmedWhenBackendCompletesLate()
    {
        var owner = new PeerControlOwner();
        var transport = new FakeTransport(); owner.Bind(transport);
        var pending = new TaskCompletionSource<DeskLinkSwitchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var control = new GuardedPeerControl(owner, transport, (_, _) => { calls++; return pending.Task; }, "host", "peer");
        using var cancel = new CancellationTokenSource();
        var returning = control.SwitchDetailedAsync("host", cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => returning.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(owner.IsUncertain);
        pending.SetResult(new DeskLinkSwitchResult { Ok = true, Target = "host", ActiveComputer = "host", InputTransferred = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.SwitchDetailedAsync("host"));
        Assert.True(owner.IsUncertain);
        Assert.Equal(2, calls); // Existing best-effort recovery is still attempted, not certified as safe.
        owner.Retire(transport);
        var replacement = new FakeTransport(); owner.Bind(replacement);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Control(owner, replacement).SwitchDetailedAsync("peer"));
    }

    [Fact]
    public async Task IgnoredCancellationOnEndCannotBeClearedByLateResponse()
    {
        var owner = new PeerControlOwner();
        var fake = new FakeTransport { EndPending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        owner.Bind(fake);
        var control = Control(owner, fake);
        await control.BeginPreviewAsync();
        using var cancel = new CancellationTokenSource();
        var ending = control.EndPreviewAsync("one", cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ending.WaitAsync(TimeSpan.FromSeconds(1)));
        fake.EndPending.SetResult(new DeskflowPreviewState());
        owner.Retire(fake);
        var fresh = new FakeTransport(); owner.Bind(fresh);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Control(owner, fresh).BeginPreviewAsync());
    }

    [Fact]
    public async Task NormalAcknowledgedOperationsRemainAvailable()
    {
        var owner = new PeerControlOwner();
        var fake = new FakeTransport();
        owner.Bind(fake);
        var control = Control(owner, fake);
        Assert.Equal(DeskflowPreviewPhase.Active, (await control.BeginPreviewAsync()).Phase);
        Assert.Equal(DeskflowPreviewPhase.Active, (await control.RenewPreviewAsync("one")).Phase);
        Assert.Equal(DeskflowPreviewPhase.Viewing, (await control.EndPreviewAsync("one")).Phase);
        Assert.True((await control.SwitchDetailedAsync("peer")).InputTransferred);
    }

    private static GuardedPeerControl Control(PeerControlOwner owner, FakeTransport fake) =>
        new(owner, fake, fake.SwitchAsync, "host", "peer");
    private static DeskflowPreviewState Active() => new() { Phase = DeskflowPreviewPhase.Active, Id = "one" };
    private sealed class FakeTransport : IDeskflowPreviewControl
    {
        public bool FailBegin, FailEnd, FailSwitch, RenewAsViewing, ReadAsActive;
        public int BeginCalls, EndCalls, SwitchCalls, ReadCalls;
        public TaskCompletionSource<DeskflowPreviewState>? BeginPending;
        public TaskCompletionSource<DeskflowPreviewState>? EndPending;
        public DeskflowPreviewState? EndResult;
        public bool PreviewHandoffSupported => true;
        public string PreviewReturnShortcut => "fixture";
        public string PreviewHandoffHint => "fixture";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default)
        { BeginCalls++; return BeginPending?.Task ?? (FailBegin ? Task.FromException<DeskflowPreviewState>(new IOException("lost")) : Task.FromResult(Active())); }
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => Task.FromResult(RenewAsViewing ? new DeskflowPreviewState() : Active());
        public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default)
        { EndCalls++; return EndPending?.Task ?? (FailEnd ? Task.FromException<DeskflowPreviewState>(new IOException("lost")) : Task.FromResult(EndResult ?? new DeskflowPreviewState())); }
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) { ReadCalls++; return Task.FromResult(ReadAsActive ? Active() : new DeskflowPreviewState()); }
        public Task<DeskLinkSwitchResult> SwitchAsync(string target, CancellationToken ct)
        { SwitchCalls++; return FailSwitch ? Task.FromException<DeskLinkSwitchResult>(new IOException("lost")) : Task.FromResult(new DeskLinkSwitchResult { Ok = true, Target = target, ActiveComputer = target, InputTransferred = true }); }
    }
}
