using System.Net;
using System.Security.Cryptography;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    private sealed class AppliedCase : IAsyncDisposable
    {
        internal string HostRoot = VaultRoot(), PeerRoot = VaultRoot();
        internal readonly WindowsCredentialProtector Protector = new();
        internal readonly GateProtector HostProtector = new(), PeerProtector = new();
        internal readonly CancellationTokenSource Cancel = new();
        internal Fixture Fixture = null!;
        internal MultiDeviceCredentialVault Host = null!;
        internal PeerInvitationStore Peer = null!;
        internal MultiPeerInvitation Old = null!, Candidate = null!;
        internal object Ticket = null!;
        internal Task<string> Observation = null!;
        internal string PromotionPath => Path.Combine(PeerRoot, "peer-promotion.bin");
        internal string StagePath => Path.Combine(PeerRoot, "peer-pending-invitation.bin");
        internal string ActivePath => Path.Combine(PeerRoot, "peer-invitation.bin");

        internal static async Task<AppliedCase> OpenAsync(bool proxy = false, AppliedCase? sameIdentity = null, byte[]? clonedPending = null)
        {
            var c = new AppliedCase();
            try
            {
                c.Fixture = await Fixture.OpenAsync(sameIdentity: sameIdentity?.Fixture, throughProxy: proxy);
                await c.Fixture.BeginAsync(); await c.Fixture.ConfirmAsync(); var now = c.Fixture.Clock();
                if (clonedPending is not null) { Directory.CreateDirectory(c.HostRoot); await File.WriteAllBytesAsync(Path.Combine(c.HostRoot, "pending-grant.bin"), clonedPending); }
                c.Host = await MultiDeviceCredentialVault.OpenAsync(c.HostRoot, c.HostProtector);
                c.Peer = await PeerInvitationStore.OpenAsync(c.PeerRoot, c.PeerProtector);
                c.Old = TestInvitation(c.Fixture); await c.Peer.SaveConfirmedAsync(c.Old, c.Fixture.Identity.HostFingerprint);
                Guid stage;
                if (clonedPending is null)
                {
                    stage = (await c.Host.StagePendingGrantAsync(c.Fixture.Identity, c.Fixture.Offer, now, clock: c.Fixture.Clock)).StageId;
                    await c.Host.FreezePendingInvitationForLoopbackTestAsync(stage, c.Fixture.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: c.Fixture.Clock);
                }
                else stage = (await c.Host.GetPendingGrantMetadataAsync(now) ?? throw new InvalidOperationException()).StageId;
                c.Candidate = await ReadOfflineHostInvitationAsync(c.HostRoot, c.Protector);
                var receive = c.Fixture.Peer.ReceivePrepareReadyAndCommittedTestAsync(c.Peer, c.Fixture.Peer.IssueIsolatedTestCapability(), c.Cancel.Token);
                c.Observation = c.Host.SendPrepareReadyCommitNotifyAndObserveAppliedTestAsync(c.Fixture.Host, c.Fixture.Host.IssueIsolatedTestCapability(), stage, c.Fixture.Identity, c.Fixture.Offer, now, c.Fixture.Clock, c.Cancel.Token);
                c.Ticket = await receive.WaitAsync(TimeSpan.FromSeconds(5)); return c;
            }
            catch { await c.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Cancel.Cancel();
            if (Observation is not null) { try { await Observation.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
            if (Peer is not null) await Peer.DisposeAsync(); if (Host is not null) await Host.DisposeAsync();
            if (Fixture is not null) await Fixture.DisposeAsync(); Cancel.Dispose(); CleanVault(HostRoot); CleanVault(PeerRoot);
        }
    }

    [Fact]
    public async Task RevocationBeforeFinalAppliedCheckCannotReportObservation()
    {
        await using var c = await AppliedCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier(); c.Host.BeforeAppliedFinalCheck = barrier;
        try
        {
            var send = c.Fixture.Peer.PromoteAndSendAppliedTestAsync(c.Peer, c.Ticket);
            await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5)); await send.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await c.Host.RevokeAsync(c.Fixture.Identity.PeerName)); barrier.Release();
            await Assert.ThrowsAsync<InvalidOperationException>(() => c.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(c.Host.IsAuthorized(c.Fixture.Identity.PeerName, c.Candidate.Token)); Assert.True(c.Candidate == c.Peer.Get());
        }
        finally { barrier.Release(); }
    }

    [Fact]
    public async Task FabricatedAppliedPermitCannotSendBeforeActualPromotion()
    {
        await using var c = await AppliedCase.OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Fixture.Peer.SendAppliedFrameTestAsync(c.Peer, new object(), new AppliedBinding(null!), CancellationToken.None));
        Assert.True(c.Old == c.Peer.Get()); Assert.False(File.Exists(c.PromotionPath)); Assert.False(c.Observation.IsCompleted);
        await c.Fixture.Peer.PromoteAndSendAppliedTestAsync(c.Peer, c.Ticket).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("applied-observed", await c.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("promotion")]
    [InlineData("stage")]
    [InlineData("active")]
    public async Task ChangedProtectedPeerEvidenceBeforeAppliedCannotBeReported(string changed)
    {
        await using var c = await AppliedCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier(); c.Peer.BeforeAppliedSend = barrier;
        var send = c.Fixture.Peer.PromoteAndSendAppliedTestAsync(c.Peer, c.Ticket);
        try
        {
            await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var path = changed == "promotion" ? c.PromotionPath : changed == "stage" ? c.StagePath : c.ActivePath;
            var plain = c.Protector.Unprotect(await File.ReadAllBytesAsync(path));
            try { await File.WriteAllBytesAsync(path, c.Protector.Protect(plain)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            barrier.Release(); await Assert.ThrowsAsync<InvalidOperationException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
            var hostError = await Record.ExceptionAsync(() => c.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(hostError is IOException or InvalidOperationException or OperationCanceledException);
            Assert.Throws<InvalidOperationException>(() => c.Peer.Get());
        }
        finally { barrier.Release(); }
    }

    [Theory]
    [InlineData(ReadyWireFault.Role)]
    [InlineData(ReadyWireFault.Type)]
    [InlineData(ReadyWireFault.Version)]
    [InlineData(ReadyWireFault.Session)]
    [InlineData(ReadyWireFault.Transaction)]
    [InlineData(ReadyWireFault.OfferNonce)]
    [InlineData(ReadyWireFault.Stage)]
    [InlineData(ReadyWireFault.SenderChallenge)]
    [InlineData(ReadyWireFault.ReceiverChallenge)]
    [InlineData(ReadyWireFault.Slot)]
    [InlineData(ReadyWireFault.DataPortBase)]
    [InlineData(ReadyWireFault.Digest)]
    [InlineData(ReadyWireFault.Missing)]
    [InlineData(ReadyWireFault.Extra)]
    [InlineData(ReadyWireFault.Duplicate)]
    [InlineData(ReadyWireFault.BadType)]
    [InlineData(ReadyWireFault.InvalidUtf8)]
    [InlineData(ReadyWireFault.CaseCollision)]
    [InlineData(ReadyWireFault.Oversize)]
    [InlineData(ReadyWireFault.Truncated)]
    public async Task CorruptedAppliedFrameCannotProduceHostObservation(ReadyWireFault fault)
    {
        await using var c = await AppliedCase.OpenAsync();
        c.Fixture.Peer.ReadyWireControls = new ReadyWireControls { Fault = fault };
        var send = c.Fixture.Peer.PromoteAndSendAppliedTestAsync(c.Peer, c.Ticket);
        if (fault == ReadyWireFault.Truncated) await Assert.ThrowsAsync<IOException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
        else await send.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Record.ExceptionAsync(() => c.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
        if (fault == ReadyWireFault.Truncated) Assert.IsAssignableFrom<IOException>(error);
        else if (fault == ReadyWireFault.BadType) Assert.IsType<InvalidOperationException>(error); else Assert.IsType<InvalidDataException>(error);
        if (fault == ReadyWireFault.Truncated) Assert.True(c.Fixture.Peer.ReadyWireControls.TruncatedWritten); else Assert.True(c.Fixture.Peer.ReadyWireControls.FrameWritten);
        Assert.True(c.Candidate == c.Peer.Get());
        Assert.True(c.Host.IsAuthorized(c.Fixture.Identity.PeerName, c.Candidate.Token));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expiry")]
    [InlineData("pending")]
    [InlineData("marker")]
    public async Task HostStateChangeBeforeAppliedFinalCheckCannotReportObservation(string change)
    {
        await using var c = await AppliedCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier(); c.Host.BeforeAppliedFinalCheck = barrier;
        try
        {
            var send = c.Fixture.Peer.PromoteAndSendAppliedTestAsync(c.Peer, c.Ticket);
            await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5)); await send.WaitAsync(TimeSpan.FromSeconds(5));
            if (change == "cancel") c.Cancel.Cancel();
            if (change == "expiry") Interlocked.Exchange(ref c.Fixture.UtcTicks, c.Fixture.Identity.ExpiresAtUtc.UtcTicks);
            if (change is "pending" or "marker")
            {
                var path = Path.Combine(c.HostRoot, change == "pending" ? "pending-grant.bin" : "pending-prepare-unknown.bin");
                var plain = c.Protector.Unprotect(await File.ReadAllBytesAsync(path));
                try { await File.WriteAllBytesAsync(path, c.Protector.Protect(plain)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            barrier.Release(); var error = await Record.ExceptionAsync(() => c.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(error is InvalidOperationException or OperationCanceledException, $"Unexpected host result: {error?.GetType().Name ?? "success"}");
            Assert.True(c.Candidate == c.Peer.Get());
        }
        finally { barrier.Release(); }
    }

    [Fact]
    public async Task PeerToHostCiphertextLostAfterAppliedWriteLeavesHostObservationUnknown()
    {
        await using var c = await AppliedCase.OpenAsync(proxy: true); var barrier = new ControlledLivePublishBarrier(); c.Peer.BeforeAppliedSend = barrier;
        var send = c.Fixture.Peer.PromoteAndSendAppliedTestAsync(c.Peer, c.Ticket);
        try
        {
            await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5)); c.Fixture.Proxy!.DropPeerToHost(); barrier.Release();
            await send.WaitAsync(TimeSpan.FromSeconds(5)); await c.Fixture.Proxy.Dropped.WaitAsync(TimeSpan.FromSeconds(5)); c.Fixture.Proxy.CutConnections();
            await Assert.ThrowsAnyAsync<IOException>(() => c.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(c.Candidate == c.Peer.Get()); Assert.True(c.Host.IsAuthorized(c.Fixture.Identity.PeerName, c.Candidate.Token));
            Assert.True(File.Exists(c.PromotionPath));
        }
        finally { barrier.Release(); }
    }

    [Fact]
    public async Task AppliedReplayedAcrossRealTlsSessionsCannotProduceObservation()
    {
        await using var a = await AppliedCase.OpenAsync();
        var clonedPending = await File.ReadAllBytesAsync(Path.Combine(a.HostRoot, "pending-grant.bin"));
        await using var b = await AppliedCase.OpenAsync(sameIdentity: a, clonedPending: clonedPending);
        Assert.NotEqual(a.Fixture.Port, b.Fixture.Port); byte[]? first = null, second = null;
        try
        {
            a.Fixture.Peer.ReadyWireControls = new ReadyWireControls { Capture = true };
            await a.Fixture.Peer.PromoteAndSendAppliedTestAsync(a.Peer, a.Ticket).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("applied-observed", await a.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
            first = a.Fixture.Peer.ReadyWireControls.Captured ?? throw new InvalidOperationException();
            b.Fixture.Peer.ReadyWireControls = new ReadyWireControls { Capture = true, Replay = first };
            await b.Fixture.Peer.PromoteAndSendAppliedTestAsync(b.Peer, b.Ticket).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidDataException>(() => b.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
            second = b.Fixture.Peer.ReadyWireControls.Captured ?? throw new InvalidOperationException();
            using var firstJson = System.Text.Json.JsonDocument.Parse(first); using var secondJson = System.Text.Json.JsonDocument.Parse(second);
            foreach (var name in new[] { "version", "type", "role", "sessionId", "transactionId", "offerNonce", "stageId", "slot", "dataPortBase", "digest" })
                Assert.True(firstJson.RootElement.GetProperty(name).GetRawText() == secondJson.RootElement.GetProperty(name).GetRawText());
            Assert.True(firstJson.RootElement.GetProperty("senderChallenge").GetString() != secondJson.RootElement.GetProperty("senderChallenge").GetString());
            Assert.True(firstJson.RootElement.GetProperty("receiverChallenge").GetString() != secondJson.RootElement.GetProperty("receiverChallenge").GetString());
            Assert.True(b.Candidate == b.Peer.Get()); Assert.True(b.Host.IsAuthorized(b.Fixture.Identity.PeerName, b.Candidate.Token));
        }
        finally { if (first is not null) CryptographicOperations.ZeroMemory(first); if (second is not null) CryptographicOperations.ZeroMemory(second); }
    }

    [Fact]
    public async Task CancelledAppliedClaimCannotReuseSamePermit()
    {
        await using var c = await AppliedCase.OpenAsync();
        await c.Fixture.Peer.PromoteWithTicketTestAsync(c.Peer, c.Ticket);
        var (permit, binding) = await c.Peer.RegisterAppliedTestAsync(c.Fixture.Peer, CancellationToken.None);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.Peer.ClaimAppliedTestAsync(c.Fixture.Peer, permit, binding, cancelled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Peer.ClaimAppliedTestAsync(c.Fixture.Peer, permit, binding, CancellationToken.None));
        Assert.False(c.Observation.IsCompleted);
    }

    [Fact]
    public async Task RevokedThenRepairedSameNameCannotValidateOldApplied()
    {
        await using var c = await AppliedCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier(); c.Host.BeforeAppliedFinalCheck = barrier;
        try
        {
            var send = c.Fixture.Peer.PromoteAndSendAppliedTestAsync(c.Peer, c.Ticket);
            await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5)); await send.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await c.Host.RevokeAsync(c.Fixture.Identity.PeerName));
            var replacement = await c.Host.AddConfirmedWithSlotAsync(c.Fixture.Identity.PeerName, c.Fixture.Identity.PeerFingerprint, c.Fixture.Offer.Platform);
            barrier.Release(); await Assert.ThrowsAsync<InvalidOperationException>(() => c.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(c.Host.IsAuthorized(c.Fixture.Identity.PeerName, replacement.Token)); Assert.False(c.Host.IsAuthorized(c.Fixture.Identity.PeerName, c.Candidate.Token));
        }
        finally { barrier.Release(); }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expiry")]
    [InlineData("close")]
    public async Task PeerInterruptionBeforeAppliedDoesNotYieldObservation(string change)
    {
        await using var c = await AppliedCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier(); using var cancel = new CancellationTokenSource();
        c.Peer.BeforeAppliedSend = barrier; Task? closing = null;
        var send = c.Fixture.Peer.PromoteAndSendAppliedTestAsync(c.Peer, c.Ticket, cancel.Token);
        try
        {
            await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            if (change == "cancel") cancel.Cancel();
            if (change == "expiry") Interlocked.Exchange(ref c.Fixture.UtcTicks, c.Fixture.Identity.ExpiresAtUtc.UtcTicks);
            if (change == "close")
            {
                closing = Task.Run(async () => await c.Fixture.Peer.DisposeAsync());
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (c.Fixture.Peer.Phase != PairingConsentPhase.Aborted) { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
            }
            barrier.Release(); var sendError = await Record.ExceptionAsync(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(sendError is InvalidOperationException or OperationCanceledException);
            if (closing is not null) await closing.WaitAsync(TimeSpan.FromSeconds(5));
            var hostError = await Record.ExceptionAsync(() => c.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(hostError is IOException or InvalidOperationException or OperationCanceledException);
            Assert.True(File.Exists(c.PromotionPath));
        }
        finally { barrier.Release(); }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("close")]
    public async Task PeerWaitingForStoreGateCancelsAndDrainsWithoutPromotion(string change)
    {
        await using var c = await AppliedCase.OpenAsync(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource();
        c.PeerProtector.BeforeProtect = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test gate was not released."); };
        var blocker = Task.Factory.StartNew(() => c.Peer.SaveConfirmedAsync(c.Old, c.Fixture.Identity.HostFingerprint),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap(); Task? send = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            send = c.Fixture.Peer.PromoteAndSendAppliedTestAsync(c.Peer, c.Ticket, cancel.Token); Assert.False(send.IsCompleted);
            if (change == "cancel") cancel.Cancel(); else await c.Fixture.Peer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Record.ExceptionAsync(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(error is OperationCanceledException or InvalidOperationException); Assert.False(File.Exists(c.PromotionPath));
        }
        finally { release.Set(); await blocker.WaitAsync(TimeSpan.FromSeconds(5)); }
        var hostError = await Record.ExceptionAsync(() => c.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(hostError is IOException or InvalidOperationException or OperationCanceledException);
    }

    [Fact]
    public async Task HostWaitingForVaultGateCancelsAndDrainsWithoutObservation()
    {
        await using var c = await AppliedCase.OpenAsync(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        c.HostProtector.BeforeProtect = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test gate was not released."); };
        var blocker = Task.Factory.StartNew(() => c.Host.AddConfirmedAsync("other-device", new string('D', 64)),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            c.Host.BeforeAppliedFinalGateWait = waiting;
            var releaseWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); c.Host.BeforePrepareReleaseGateWait = releaseWaiting;
            await c.Fixture.Peer.PromoteAndSendAppliedTestAsync(c.Peer, c.Ticket).WaitAsync(TimeSpan.FromSeconds(5));
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(c.Observation.IsCompleted); c.Cancel.Cancel();
            await releaseWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(c.Observation.IsCompleted);
            release.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { release.Set(); await blocker.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
