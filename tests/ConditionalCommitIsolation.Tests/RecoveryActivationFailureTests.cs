using System.Security.Cryptography;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    private static async Task<string?> RunActivationAsync(PromotedRecoveryCase c, RecoveryActivationControls controls, DateTimeOffset? now = null,
        Func<DateTimeOffset>? clock = null, CancellationToken ct = default)
    {
        var current = now ?? c.Now;
        return await RecoveryActivationExperiment.RunAsync(c.Case.Host, c.Case.Peer, c.Case.Fixture.HostCertificate, c.Case.Fixture.PeerCertificate, current, controls, clock ?? (() => current), ct);
    }
    private static async Task ConfirmRecoveryAsync(RecoveryActivationControls controls, Task? running = null)
    {
        var ready = Task.WhenAll(controls.HostAwaitingConfirmation.Task, controls.PeerAwaitingConfirmation.Task);
        if (running is not null && await Task.WhenAny(ready, running).WaitAsync(TimeSpan.FromSeconds(5)) == running && !ready.IsCompleted)
        { await running; throw new InvalidOperationException("Recovery ended before both confirmations were requested."); }
        await ready.WaitAsync(TimeSpan.FromSeconds(5)); controls.ConfirmHost(); controls.ConfirmPeer();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EitherFreshConfirmationRejectionKeepsRecoveryClosed(bool hostRejects)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var controls = new RecoveryActivationControls();
        Assert.Throws<InvalidOperationException>(() => controls.ConfirmHost());
        var run = RunActivationAsync(c, controls);
        await Task.WhenAll(controls.HostAwaitingConfirmation.Task, controls.PeerAwaitingConfirmation.Task).WaitAsync(TimeSpan.FromSeconds(5));
        if (hostRejects) { controls.RejectHost(); controls.ConfirmPeer(); } else { controls.ConfirmHost(); controls.RejectPeer(); }
        Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        Assert.False(File.Exists(Path.Combine(c.Case.PeerRoot, "peer-recovery.bin")));
    }

    [Fact]
    public async Task HistoricalAndFreshSessionExpiryCannotReactivate()
    {
        await using var historical = await PromotedRecoveryCase.OpenAsync();
        var expired = historical.Case.Fixture.Identity.ExpiresAtUtc.AddSeconds(1);
        Assert.Null(await RunActivationAsync(historical, new RecoveryActivationControls(), expired));
        Assert.Throws<InvalidOperationException>(() => historical.Case.Peer.Get());
        await using var fresh = await PromotedRecoveryCase.OpenAsync(); long ticks = fresh.Now.UtcTicks; var controls = new RecoveryActivationControls();
        var run = RunActivationAsync(fresh, controls, clock: () => new DateTimeOffset(Interlocked.Read(ref ticks), TimeSpan.Zero));
        await Task.WhenAll(controls.HostAwaitingConfirmation.Task, controls.PeerAwaitingConfirmation.Task).WaitAsync(TimeSpan.FromSeconds(5));
        controls.ConfirmHost(); await controls.HostConfirmationSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Interlocked.Exchange(ref ticks, fresh.Now.AddSeconds(31).UtcTicks); controls.ConfirmPeer();
        Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Throws<InvalidOperationException>(() => fresh.Case.Peer.Get());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedOrReplacedGrantBeforeRecoveryAuthorizationStaysClosed(bool replace)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier();
        var controls = new RecoveryActivationControls { BeforeHostAuthorization = barrier }; var run = RunActivationAsync(c, controls);
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await c.Case.Host.RevokeAsync(c.Case.Fixture.Identity.PeerName));
            if (replace) await c.Case.Host.AddConfirmedWithSlotAsync(c.Case.Fixture.Identity.PeerName, c.Case.Fixture.Identity.PeerFingerprint, c.Case.Fixture.Offer.Platform);
            barrier.Release(); Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get()); Assert.False(File.Exists(Path.Combine(c.Case.PeerRoot, "peer-recovery.bin")));
        }
        finally { barrier.Release(); }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public async Task ChangedProtectedEvidenceBeforeAuthorizationOrActivationKeepsRecoveryClosed(int fileIndex, bool hostSide)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier();
        var controls = hostSide ? new RecoveryActivationControls { BeforeHostAuthorization = barrier } : new RecoveryActivationControls { BeforePeerActivation = barrier };
        var run = RunActivationAsync(c, controls);
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var path = c.Files[fileIndex]; var plain = c.Case.Protector.Unprotect(await File.ReadAllBytesAsync(path));
            try { await File.WriteAllBytesAsync(path, c.Case.Protector.Protect(plain)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var changed = await File.ReadAllBytesAsync(path); barrier.Release(); Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(changed, await File.ReadAllBytesAsync(path)); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
            Assert.False(File.Exists(Path.Combine(c.Case.PeerRoot, "peer-recovery.bin")));
        }
        finally { barrier.Release(); }
    }

    [Fact]
    public async Task RecoveryTemporaryConflictDoesNotOverwriteOrDeleteFile()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var control = new RecoveryActivationControls();
        var path = Path.Combine(c.Case.PeerRoot, "peer-recovery.bin." + Guid.NewGuid().ToString("N") + ".tmp"); byte[] occupied = [7, 8, 9];
        await File.WriteAllBytesAsync(path, occupied); c.Case.Peer.RecoveryTemporaryOverride = path;
        var run = RunActivationAsync(c, control); await ConfirmRecoveryAsync(control, run); Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(occupied, await File.ReadAllBytesAsync(path)); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Fact]
    public async Task PublishedRecoveryThenMemoryFailureKeepsClosedEvenIfPromotionFileIsMissing()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var control = new RecoveryActivationControls();
        c.Case.Peer.AfterRecoveryMove = () => throw new IOException("Synthetic post-publication failure.");
        var run = RunActivationAsync(c, control); await ConfirmRecoveryAsync(control, run); Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(File.Exists(Path.Combine(c.Case.PeerRoot, "peer-recovery.bin"))); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        var promotion = await File.ReadAllBytesAsync(c.Case.PromotionPath); File.Delete(c.Case.PromotionPath);
        try
        {
            await c.Case.Peer.DisposeAsync(); c.Case.Peer = await PeerInvitationStore.OpenAsync(c.Case.PeerRoot, c.Case.Protector);
            Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        }
        finally { if (!File.Exists(c.Case.PromotionPath)) await File.WriteAllBytesAsync(c.Case.PromotionPath, promotion); }
    }

    [Fact]
    public async Task RevocationAfterHostFinalCheckCanLeaveStaleLocalCandidateButHostRejectsToken()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier();
        var controls = new RecoveryActivationControls { AfterHostFinalCheck = barrier }; var run = RunActivationAsync(c, controls);
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await c.Case.Host.RevokeAsync(c.Case.Fixture.Identity.PeerName)); barrier.Release();
            Assert.Equal("test-process-recovered", await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(c.Case.Candidate == c.Case.Peer.Get()); Assert.False(c.Case.Host.IsAuthorized(c.Case.Fixture.Identity.PeerName, c.Case.Candidate.Token));
        }
        finally { barrier.Release(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryRequiresBothOriginalCertificatePins(bool wrongHost)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); await using var wrong = await Fixture.OpenAsync();
        using var host = PrivateFiles.LoadTlsCertificate(wrongHost ? wrong.HostCertificatePath : c.Case.Fixture.HostCertificatePath);
        using var peer = PrivateFiles.LoadTlsCertificate(wrongHost ? c.Case.Fixture.PeerCertificatePath : wrong.PeerCertificatePath);
        var controls = new RecoveryActivationControls();
        Assert.Null(await RecoveryActivationExperiment.RunAsync(c.Case.Host, c.Case.Peer, host, peer, c.Now, controls, () => c.Now));
        Assert.False(controls.HostAwaitingConfirmation.Task.IsCompleted); Assert.False(controls.PeerAwaitingConfirmation.Task.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Fact]
    public async Task RecoveryRecordContainsNoBearerAndBlocksAnotherRecoveryAfterReopen()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var control = new RecoveryActivationControls(); var run = RunActivationAsync(c, control);
        await ConfirmRecoveryAsync(control, run); Assert.Equal("test-process-recovered", await run.WaitAsync(TimeSpan.FromSeconds(5)));
        var path = Path.Combine(c.Case.PeerRoot, "peer-recovery.bin"); var plain = c.Case.Protector.Unprotect(await File.ReadAllBytesAsync(path));
        try { Assert.DoesNotContain(c.Case.Candidate.Token, System.Text.Encoding.UTF8.GetString(plain), StringComparison.Ordinal); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        await c.Case.Peer.DisposeAsync(); c.Case.Peer = await PeerInvitationStore.OpenAsync(c.Case.PeerRoot, c.Case.Protector);
        var before = await File.ReadAllBytesAsync(path); Assert.Null(await RunActivationAsync(c, new RecoveryActivationControls()));
        Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Fact]
    public async Task RecoveryMoveConflictPreservesExistingRecordAndRemainsUnknown()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier();
        c.Case.Peer.AfterRecoveryFinalCheck = barrier; var controls = new RecoveryActivationControls(); var run = RunActivationAsync(c, controls);
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var target = Path.Combine(c.Case.PeerRoot, "peer-recovery.bin"); byte[] occupied = [4, 5, 6]; await File.WriteAllBytesAsync(target, occupied);
            barrier.Release(); Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(occupied, await File.ReadAllBytesAsync(target)); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
            await c.Case.Peer.DisposeAsync(); c.Case.Peer = await PeerInvitationStore.OpenAsync(c.Case.PeerRoot, c.Case.Protector);
            Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        }
        finally { barrier.Release(); }
    }

    [Fact]
    public async Task RecoveryCancelledBeforePublicationKeepsOriginalFilesAndFailsClosed()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier(); using var cancellation = new CancellationTokenSource();
        c.Case.Peer.BeforeRecoveryMove = barrier; var controls = new RecoveryActivationControls(); var before = await Task.WhenAll(c.Files.Select(path => File.ReadAllBytesAsync(path)));
        var run = RunActivationAsync(c, controls, ct: cancellation.Token);
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); barrier.Release();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < c.Files.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(c.Files[i]));
            Assert.False(File.Exists(Path.Combine(c.Case.PeerRoot, "peer-recovery.bin"))); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        }
        finally { barrier.Release(); }
    }

    [Fact]
    public async Task RecoveryCancellationWhileWaitingForFreshConfirmationsNeverActivates()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); using var cancel = new CancellationTokenSource(); var controls = new RecoveryActivationControls();
        var run = RunActivationAsync(c, controls, ct: cancel.Token);
        await Task.WhenAll(controls.HostAwaitingConfirmation.Task, controls.PeerAwaitingConfirmation.Task).WaitAsync(TimeSpan.FromSeconds(5));
        controls.ConfirmHost(); await controls.HostConfirmationSent.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get()); Assert.False(File.Exists(Path.Combine(c.Case.PeerRoot, "peer-recovery.bin")));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expiry")]
    public async Task RecoveryInterruptionAfterFinalCheckMayStillPublish(string interruption)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier(); using var cancel = new CancellationTokenSource();
        long ticks = c.Now.UtcTicks; c.Case.Peer.AfterRecoveryFinalCheck = barrier; var controls = new RecoveryActivationControls();
        var run = RunActivationAsync(c, controls, clock: () => new DateTimeOffset(Interlocked.Read(ref ticks), TimeSpan.Zero), ct: cancel.Token);
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            if (interruption == "cancel") cancel.Cancel(); else Interlocked.Exchange(ref ticks, c.Now.AddSeconds(31).UtcTicks);
            barrier.Release(); Assert.Equal("test-process-recovered", await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(c.Case.Candidate == c.Case.Peer.Get()); Assert.True(File.Exists(Path.Combine(c.Case.PeerRoot, "peer-recovery.bin")));
        }
        finally { barrier.Release(); }
    }

    private sealed class BlockingRecoveryProtector : IMultiDeviceSecretProtector
    {
        private readonly WindowsCredentialProtector inner = new();
        internal Action? BeforeUnprotect { get; set; }
        public byte[] Protect(byte[] plaintext) => inner.Protect(plaintext);
        public byte[] Unprotect(byte[] ciphertext) { BeforeUnprotect?.Invoke(); return inner.Unprotect(ciphertext); }
    }

    [Fact]
    public async Task RecoveryCancelledWhileWaitingForPeerGateDrainsWithoutActivation()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var protector = new BlockingRecoveryProtector();
        await c.Case.Peer.DisposeAsync(); c.Case.Peer = await PeerInvitationStore.OpenAsync(c.Case.PeerRoot, protector);
        var barrier = new ControlledLivePublishBarrier(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource();
        var controls = new RecoveryActivationControls { BeforePeerActivation = barrier }; var run = RunActivationAsync(c, controls, ct: cancel.Token); Task? blocker = null;
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            protector.BeforeUnprotect = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test gate was not released."); };
            blocker = Task.Factory.StartNew(() => c.Case.Peer.CapturePromotedRecoveryTestAsync(c.Now), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            c.Case.Peer.BeforeActivationGateWait = waiting; c.Case.Peer.BeforePeerRecoveryEndGateWait = releaseWaiting;
            barrier.Release(); await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            await releaseWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(run.IsCompleted);
            release.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get()); Assert.False(File.Exists(Path.Combine(c.Case.PeerRoot, "peer-recovery.bin")));
        }
        finally { barrier.Release(); release.Set(); if (blocker is not null) await blocker.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task RecoveryCancelledWhileWaitingForHostGateDrainsAfterLeaseRelease()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var protector = new GateProtector();
        await c.Case.Host.DisposeAsync(); c.Case.Host = await MultiDeviceCredentialVault.OpenAsync(c.Case.HostRoot, protector);
        var barrier = new ControlledLivePublishBarrier(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource();
        var controls = new RecoveryActivationControls { BeforeHostAuthorization = barrier }; var run = RunActivationAsync(c, controls, ct: cancel.Token); Task? blocker = null;
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            protector.BeforeProtect = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test gate was not released."); };
            blocker = Task.Factory.StartNew(() => c.Case.Host.AddConfirmedAsync("other-device", new string('D', 64)), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registrationWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            c.Case.Host.BeforeActivationReleaseGateWait = waiting; c.Case.Host.HostActivationGateWaiting = registrationWaiting;
            barrier.Release(); await registrationWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted); release.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        }
        finally { barrier.Release(); release.Set(); if (blocker is not null) await blocker.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task CancelledHostRecoveryPermitCannotBeReused()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier();
        var controls = new RecoveryActivationControls { BeforeHostAuthorization = barrier }; var run = RunActivationAsync(c, controls);
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var session = controls.HostSessionForTest ?? throw new InvalidOperationException(); var snapshot = controls.HostSnapshotForTest ?? throw new InvalidOperationException();
            var permit = await c.Case.Host.RegisterActivationSendTestAsync(session, snapshot, CancellationToken.None);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.Case.Host.ClaimActivationSendTestAsync(session, snapshot, permit, cancelled.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => c.Case.Host.ClaimActivationSendTestAsync(session, snapshot, permit, CancellationToken.None));
            barrier.Release(); Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        }
        finally { barrier.Release(); }
    }

    [Fact]
    public async Task PeerRecoveryPermitCannotCrossStoreOrBeForged()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier(); var otherRoot = VaultRoot();
        var controls = new RecoveryActivationControls { BeforePeerActivation = barrier }; var run = RunActivationAsync(c, controls);
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var session = controls.PeerSessionForTest ?? throw new InvalidOperationException(); var snapshot = controls.PeerSnapshotForTest ?? throw new InvalidOperationException();
            var permit = controls.PeerPermitForTest ?? throw new InvalidOperationException();
            await using var other = await PeerInvitationStore.OpenAsync(otherRoot, c.Case.Protector);
            Assert.Throws<InvalidOperationException>(() => session.ClaimPeerActivation(other, snapshot, permit, CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => session.ClaimPeerActivation(c.Case.Peer, snapshot, new object(), CancellationToken.None));
            barrier.Release(); Assert.Equal("test-process-recovered", await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(c.Case.Candidate == c.Case.Peer.Get());
        }
        finally { barrier.Release(); CleanVault(otherRoot); }
    }

    [Fact]
    public async Task RecoveryClockRollbackAfterNewHandshakeCannotActivate()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); long ticks = c.Now.UtcTicks; var controls = new RecoveryActivationControls();
        var run = RunActivationAsync(c, controls, clock: () => new DateTimeOffset(Interlocked.Read(ref ticks), TimeSpan.Zero));
        await Task.WhenAll(controls.HostAwaitingConfirmation.Task, controls.PeerAwaitingConfirmation.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Interlocked.Exchange(ref ticks, c.Now.AddSeconds(-1).UtcTicks); controls.ConfirmHost(); controls.ConfirmPeer();
        Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Fact]
    public async Task ClockRollbackBetweenSnapshotAndSessionCannotActivate()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var now = c.Case.Fixture.Identity.ExpiresAtUtc.AddSeconds(-10);
        var controls = new RecoveryActivationControls(); var run = RunActivationAsync(c, controls, now, () => now.AddSeconds(-1));
        var ready = Task.WhenAll(controls.HostAwaitingConfirmation.Task, controls.PeerAwaitingConfirmation.Task);
        var first = await Task.WhenAny(ready, run).WaitAsync(TimeSpan.FromSeconds(5));
        if (first == ready) { await ready; controls.ConfirmHost(); controls.ConfirmPeer(); }
        Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Fact]
    public async Task RecoverySnapshotHashCopiesCannotMutateOriginalBinding()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var controls = new RecoveryActivationControls(); var run = RunActivationAsync(c, controls);
        await Task.WhenAll(controls.HostAwaitingConfirmation.Task, controls.PeerAwaitingConfirmation.Task).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var snapshot = controls.PeerSnapshotForTest ?? throw new InvalidOperationException(); var supplied = snapshot.FirstHash; var before = supplied.ToArray();
            supplied[0] ^= 0xff; Assert.Equal(before, snapshot.FirstHash);
            controls.ConfirmHost(); controls.ConfirmPeer(); Assert.Equal("test-process-recovered", await run.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (!run.IsCompleted)
            {
                try { controls.ConfirmHost(); } catch (InvalidOperationException) { }
                try { controls.ConfirmPeer(); } catch (InvalidOperationException) { }
                try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
        }
    }

    [Fact]
    public async Task HostStoreCloseBeforeRecoveryAuthorizationStopsActivation()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier();
        var controls = new RecoveryActivationControls { AfterHostFinalCheck = barrier }; var run = RunActivationAsync(c, controls); Task? closing = null;
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            closing = Task.Run(async () => await c.Case.Host.DisposeAsync());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try { c.Case.Host.IsAuthorized(c.Case.Fixture.Identity.PeerName, c.Case.Candidate.Token); }
                catch (ObjectDisposedException) { break; }
                await Task.Yield();
            }
            barrier.Release(); Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5))); await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get()); Assert.False(File.Exists(Path.Combine(c.Case.PeerRoot, "peer-recovery.bin")));
        }
        finally { barrier.Release(); if (closing is not null) { try { await closing.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } } }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosingEitherStoreWhileAwaitingConfirmationCancelsAndDrainsRecovery(bool hostSide)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var controls = new RecoveryActivationControls(); var run = RunActivationAsync(c, controls);
        await Task.WhenAll(controls.HostAwaitingConfirmation.Task, controls.PeerAwaitingConfirmation.Task).WaitAsync(TimeSpan.FromSeconds(5));
        if (hostSide) await c.Case.Host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        else await c.Case.Peer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        if (hostSide) Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        else { c.Case.Peer = await PeerInvitationStore.OpenAsync(c.Case.PeerRoot, c.Case.Protector); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get()); }
        Assert.False(File.Exists(Path.Combine(c.Case.PeerRoot, "peer-recovery.bin")));
    }

    [Fact]
    public async Task PeerStoreCloseBeforeRecoveryPublicationCancelsAndDrains()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier();
        var controls = new RecoveryActivationControls { BeforePeerActivation = barrier }; var run = RunActivationAsync(c, controls); Task? closing = null;
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            closing = Task.Run(async () => await c.Case.Peer.DisposeAsync());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try { c.Case.Peer.Get(); } catch (ObjectDisposedException) { break; } catch (InvalidOperationException) { }
                await Task.Yield();
            }
            Assert.False(closing.IsCompleted); barrier.Release(); Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5)));
            await closing.WaitAsync(TimeSpan.FromSeconds(5)); c.Case.Peer = await PeerInvitationStore.OpenAsync(c.Case.PeerRoot, c.Case.Protector);
            Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get()); Assert.False(File.Exists(Path.Combine(c.Case.PeerRoot, "peer-recovery.bin")));
        }
        finally { barrier.Release(); if (closing is not null) { try { await closing.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } } }
    }

    [Theory]
    [InlineData("callback")]
    [InlineData("host-stream")]
    [InlineData("peer-stream")]
    public async Task TeardownFailureStillClosesBothRecoveryLeases(string fault)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var controls = new RecoveryActivationControls
        {
            ThrowOnCancellationTest = fault == "callback", ThrowAfterHostStreamCloseTest = fault == "host-stream", ThrowAfterPeerStreamCloseTest = fault == "peer-stream"
        };
        var run = RunActivationAsync(c, controls); Task? hostClose = null, peerClose = null;
        try
        {
            await ConfirmRecoveryAsync(controls, run); var error = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotNull(error); Assert.False(error is TimeoutException);
            hostClose = c.Case.Host.DisposeAsync().AsTask(); peerClose = c.Case.Peer.DisposeAsync().AsTask();
            await Task.WhenAll(hostClose, peerClose).WaitAsync(TimeSpan.FromSeconds(3));
            c.Case.Host = await MultiDeviceCredentialVault.OpenAsync(c.Case.HostRoot, c.Case.Protector);
            c.Case.Peer = await PeerInvitationStore.OpenAsync(c.Case.PeerRoot, c.Case.Protector);
            Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        }
        finally
        {
            if (controls.HostSessionForTest is not null)
            {
                try { await controls.HostSessionForTest.CloseTransportTestAsync(); } catch { }
                try { await c.Case.Host.EndHostRecoveryTestAsync(controls.HostSessionForTest); } catch { }
            }
            if (controls.PeerSessionForTest is not null)
            {
                try { await controls.PeerSessionForTest.CloseTransportTestAsync(); } catch { }
                try { await c.Case.Peer.EndPeerRecoveryTestAsync(controls.PeerSessionForTest); } catch { }
            }
            if (hostClose is not null) { try { await hostClose.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
            if (peerClose is not null) { try { await peerClose.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StoreCloseWithThrowingCancellationStillDrainsAndReleasesLease(bool hostSide)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var controls = new RecoveryActivationControls();
        var run = RunActivationAsync(c, controls);
        await Task.WhenAll(controls.HostAwaitingConfirmation.Task, controls.PeerAwaitingConfirmation.Task).WaitAsync(TimeSpan.FromSeconds(5));
        var session = hostSide ? controls.HostSessionForTest : controls.PeerSessionForTest;
        (session ?? throw new InvalidOperationException()).InstallThrowingStopCallbackTest();
        Task close = hostSide ? c.Case.Host.DisposeAsync().AsTask() : c.Case.Peer.DisposeAsync().AsTask();
        var error = await Record.ExceptionAsync(() => close.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(error); Assert.False(error is TimeoutException); Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        if (hostSide) c.Case.Host = await MultiDeviceCredentialVault.OpenAsync(c.Case.HostRoot, c.Case.Protector);
        else c.Case.Peer = await PeerInvitationStore.OpenAsync(c.Case.PeerRoot, c.Case.Protector);
        Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnverifiedSocketClosureDoesNotReleaseCorrespondingStoreLease(bool hostSide)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync();
        var skipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controls = new RecoveryActivationControls
        {
            ForceHostClosureUnverifiedTest = hostSide, ForcePeerClosureUnverifiedTest = !hostSide,
            HostEndSkipped = hostSide ? skipped : null, PeerEndSkipped = hostSide ? null : skipped
        };
        var run = RunActivationAsync(c, controls); Task? closing = null;
        try
        {
            await ConfirmRecoveryAsync(controls, run); await skipped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            closing = hostSide ? c.Case.Host.DisposeAsync().AsTask() : c.Case.Peer.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
        }
        finally
        {
            if (hostSide && controls.HostSessionForTest is not null)
            {
                try { await controls.HostSessionForTest.CloseTransportTestAsync(); } catch { }
                try { await c.Case.Host.EndHostRecoveryTestAsync(controls.HostSessionForTest); } catch { }
            }
            if (!hostSide && controls.PeerSessionForTest is not null)
            {
                try { await controls.PeerSessionForTest.CloseTransportTestAsync(); } catch { }
                try { await c.Case.Peer.EndPeerRecoveryTestAsync(controls.PeerSessionForTest); } catch { }
            }
            if (closing is not null) await closing.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
