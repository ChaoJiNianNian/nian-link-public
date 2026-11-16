using System.Net;
using System.Security.Cryptography;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    private sealed class PromotionCase : IAsyncDisposable
    {
        internal string HostRoot = VaultRoot(), PeerRoot = VaultRoot();
        internal readonly WindowsCredentialProtector Windows = new();
        internal readonly GateProtector PeerProtector = new();
        internal Fixture Fixture = null!;
        internal MultiDeviceCredentialVault Host = null!;
        internal PeerInvitationStore Peer = null!;
        internal MultiPeerInvitation Old = null!, Candidate = null!;
        internal object Ticket = null!;
        internal string ActivePath => Path.Combine(PeerRoot, "peer-invitation.bin");
        internal string StagePath => Path.Combine(PeerRoot, "peer-pending-invitation.bin");
        internal string PromotionPath => Path.Combine(PeerRoot, "peer-promotion.bin");

        internal static async Task<PromotionCase> OpenAsync()
        {
            var c = new PromotionCase();
            try
            {
                c.Fixture = await Fixture.OpenAsync(); await c.Fixture.BeginAsync(); await c.Fixture.ConfirmAsync(); var now = c.Fixture.Clock();
                c.Host = await MultiDeviceCredentialVault.OpenAsync(c.HostRoot, c.Windows);
                c.Peer = await PeerInvitationStore.OpenAsync(c.PeerRoot, c.PeerProtector);
                c.Old = TestInvitation(c.Fixture); await c.Peer.SaveConfirmedAsync(c.Old, c.Fixture.Identity.HostFingerprint);
                var stage = (await c.Host.StagePendingGrantAsync(c.Fixture.Identity, c.Fixture.Offer, now, clock: c.Fixture.Clock)).StageId;
                await c.Host.FreezePendingInvitationForLoopbackTestAsync(stage, c.Fixture.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: c.Fixture.Clock);
                c.Candidate = await ReadOfflineHostInvitationAsync(c.HostRoot, c.Windows);
                var receive = c.Fixture.Peer.ReceivePrepareReadyAndCommittedTestAsync(c.Peer, c.Fixture.Peer.IssueIsolatedTestCapability());
                await c.Host.SendPrepareReadyCommitAndNotifyTestAsync(c.Fixture.Host, c.Fixture.Host.IssueIsolatedTestCapability(), stage, c.Fixture.Identity, c.Fixture.Offer, now, c.Fixture.Clock).WaitAsync(TimeSpan.FromSeconds(5));
                c.Ticket = await receive.WaitAsync(TimeSpan.FromSeconds(5)); return c;
            }
            catch { await c.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (Peer is not null) await Peer.DisposeAsync();
            if (Host is not null) await Host.DisposeAsync();
            if (Fixture is not null) await Fixture.DisposeAsync();
            CleanVault(HostRoot); CleanVault(PeerRoot);
        }
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("active")]
    [InlineData("cancel")]
    [InlineData("expiry")]
    [InlineData("close")]
    [InlineData("protect")]
    public async Task PromotionFailureBeforeMovePreservesOldInvitation(string cause)
    {
        await using var c = await PromotionCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier(); using var cancel = new CancellationTokenSource();
        Task? promotion = null, closing = null;
        try
        {
            if (cause == "protect") c.PeerProtector.BeforeProtect = () => throw new CryptographicException("Synthetic protection failure.");
            else c.Peer.BeforePromotionMove = barrier;
            promotion = c.Fixture.Peer.PromoteWithTicketTestAsync(c.Peer, c.Ticket, cancel.Token);
            if (cause != "protect")
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                if (cause is "stage" or "active")
                {
                    var path = cause == "stage" ? c.StagePath : c.ActivePath; var plain = c.Windows.Unprotect(await File.ReadAllBytesAsync(path));
                    try { await File.WriteAllBytesAsync(path, c.Windows.Protect(plain)); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                if (cause == "cancel") cancel.Cancel();
                if (cause == "expiry") Interlocked.Exchange(ref c.Fixture.UtcTicks, c.Fixture.Identity.ExpiresAtUtc.UtcTicks);
                if (cause == "close")
                {
                    closing = Task.Run(async () => await c.Fixture.Peer.DisposeAsync());
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while (c.Fixture.Peer.Phase != PairingConsentPhase.Aborted) { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
                }
                barrier.Release();
            }
            var error = await Record.ExceptionAsync(() => promotion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(error is InvalidOperationException or OperationCanceledException or CryptographicException, $"Unexpected promotion result: {error?.GetType().Name ?? "success"}");
            if (closing is not null) await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(c.Old == c.Peer.Get()); Assert.False(File.Exists(c.PromotionPath));
            Assert.True(File.Exists(c.ActivePath)); Assert.True(File.Exists(c.StagePath));
        }
        finally { barrier.Release(); cancel.Cancel(); if (promotion is not null) { try { await promotion.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } } }
    }

    [Fact]
    public async Task PromotionPublishedButMemoryUpdateFailsKeepsUnknownAcrossReopen()
    {
        await using var c = await PromotionCase.OpenAsync();
        var old = await File.ReadAllBytesAsync(c.ActivePath); var staged = await File.ReadAllBytesAsync(c.StagePath);
        c.Peer.AfterPromotionMove = () => throw new IOException("Synthetic failure after publish.");
        await Assert.ThrowsAsync<IOException>(() => c.Fixture.Peer.PromoteWithTicketTestAsync(c.Peer, c.Ticket));
        Assert.True(File.Exists(c.PromotionPath)); Assert.Throws<InvalidOperationException>(() => c.Peer.Get());
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Peer.SaveConfirmedAsync(c.Old, c.Fixture.Identity.HostFingerprint));
        Assert.Equal(old, await File.ReadAllBytesAsync(c.ActivePath)); Assert.Equal(staged, await File.ReadAllBytesAsync(c.StagePath));
        await c.Peer.DisposeAsync(); await using var reopened = await PeerInvitationStore.OpenAsync(c.PeerRoot, c.Windows);
        Assert.Throws<InvalidOperationException>(() => reopened.Get());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SaveConfirmedAsync(c.Old, c.Fixture.Identity.HostFingerprint));
    }

    [Fact]
    public async Task PromotionMoveConflictFailsClosedAndDoesNotOverwriteDestination()
    {
        await using var c = await PromotionCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier();
        var old = await File.ReadAllBytesAsync(c.ActivePath); var staged = await File.ReadAllBytesAsync(c.StagePath);
        c.Fixture.Peer.AfterPromotionFinalCheck = barrier;
        var operation = Task.Run(() => c.Fixture.Peer.PromoteWithTicketTestAsync(c.Peer, c.Ticket));
        try
        {
            await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Throws<InvalidOperationException>(() => c.Peer.Get());
            var occupied = new byte[] { 4, 5, 6 }; await File.WriteAllBytesAsync(c.PromotionPath, occupied); barrier.Release();
            await Assert.ThrowsAnyAsync<IOException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidOperationException>(() => c.Peer.Get()); Assert.Equal(occupied, await File.ReadAllBytesAsync(c.PromotionPath));
            Assert.Equal(old, await File.ReadAllBytesAsync(c.ActivePath)); Assert.Equal(staged, await File.ReadAllBytesAsync(c.StagePath));
            await c.Peer.DisposeAsync(); await using var reopened = await PeerInvitationStore.OpenAsync(c.PeerRoot, c.Windows);
            Assert.Throws<InvalidOperationException>(() => reopened.Get());
        }
        finally { barrier.Release(); }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expiry")]
    public async Task PromotionAfterFinalCheckMayCompleteDespiteLaterInterruption(string interruption)
    {
        await using var c = await PromotionCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier(); using var cancel = new CancellationTokenSource();
        c.Fixture.Peer.AfterPromotionFinalCheck = barrier;
        var operation = Task.Run(() => c.Fixture.Peer.PromoteWithTicketTestAsync(c.Peer, c.Ticket, cancel.Token));
        try
        {
            await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            if (interruption == "cancel") cancel.Cancel(); else Interlocked.Exchange(ref c.Fixture.UtcTicks, c.Fixture.Identity.ExpiresAtUtc.UtcTicks);
            barrier.Release(); Assert.Equal("promoted", await operation.WaitAsync(TimeSpan.FromSeconds(5))); Assert.True(c.Candidate == c.Peer.Get());
        }
        finally { barrier.Release(); }
    }

    [Fact]
    public async Task PromotionRecordBlocksOtherPendingMutationsEvenIfStageFileIsMissing()
    {
        await using var c = await PromotionCase.OpenAsync();
        await c.Fixture.Peer.PromoteWithTicketTestAsync(c.Peer, c.Ticket);
        var original = await File.ReadAllBytesAsync(c.StagePath); File.Delete(c.StagePath);
        try
        {
            var digest = PeerInvitationStore.ComputePendingDigest(c.Candidate);
            await Assert.ThrowsAsync<InvalidOperationException>(() => c.Peer.StagePendingAsync(c.Fixture.Identity, c.Candidate, c.Fixture.Identity.PeerName,
                c.Fixture.Identity.HostFingerprint, digest, c.Fixture.Clock()));
            Assert.False(File.Exists(c.StagePath));
        }
        finally { if (!File.Exists(c.StagePath)) await File.WriteAllBytesAsync(c.StagePath, original); }
    }

    [Fact]
    public async Task PromotionTicketCannotCrossSessionOrStoreAndCannotBeReused()
    {
        await using var c = await PromotionCase.OpenAsync(); var otherRoot = VaultRoot();
        try
        {
            await using var otherSession = await Fixture.OpenAsync(sameIdentity: c.Fixture);
            await otherSession.BeginAsync(); await otherSession.ConfirmAsync();
            await using var otherStore = await PeerInvitationStore.OpenAsync(otherRoot, c.Windows);
            await Assert.ThrowsAsync<InvalidOperationException>(() => otherSession.Peer.PromoteWithTicketTestAsync(c.Peer, c.Ticket));
            await Assert.ThrowsAsync<InvalidOperationException>(() => c.Fixture.Peer.PromoteWithTicketTestAsync(otherStore, c.Ticket));
            await Assert.ThrowsAsync<InvalidOperationException>(() => c.Fixture.Peer.PromoteWithTicketTestAsync(c.Peer, new object()));
            Assert.True(c.Old == c.Peer.Get()); Assert.False(File.Exists(c.PromotionPath));
            Assert.Equal("promoted", await c.Fixture.Peer.PromoteWithTicketTestAsync(c.Peer, c.Ticket));
            await Assert.ThrowsAsync<InvalidOperationException>(() => c.Fixture.Peer.PromoteWithTicketTestAsync(c.Peer, c.Ticket));
            Assert.True(c.Candidate == c.Peer.Get());
        }
        finally { CleanVault(otherRoot); }
    }

    [Fact]
    public async Task PromotionRecordBlocksRealTlsStageIfEarlierStageIsMissing()
    {
        await using var c = await PromotionCase.OpenAsync(); var otherHostRoot = VaultRoot(); using var cancel = new CancellationTokenSource();
        var staged = await File.ReadAllBytesAsync(c.StagePath); Task? send = null, receive = null;
        try
        {
            await c.Fixture.Peer.PromoteWithTicketTestAsync(c.Peer, c.Ticket);
            var marker = await File.ReadAllBytesAsync(c.PromotionPath); File.Delete(c.StagePath);
            await using var other = await Fixture.OpenAsync(sameIdentity: c.Fixture); await other.BeginAsync(); await other.ConfirmAsync();
            Directory.CreateDirectory(otherHostRoot);
            await File.WriteAllBytesAsync(Path.Combine(otherHostRoot, "pending-grant.bin"), await File.ReadAllBytesAsync(Path.Combine(c.HostRoot, "pending-grant.bin")));
            await using var otherHost = await MultiDeviceCredentialVault.OpenAsync(otherHostRoot, c.Windows);
            var now = other.Clock(); var metadata = await otherHost.GetPendingGrantMetadataAsync(now) ?? throw new InvalidOperationException();
            try
            {
                receive = other.Peer.ReceivePrepareAndStageTestAsync(c.Peer, other.Peer.IssueIsolatedTestCapability(), cancel.Token, writeV3: true);
                send = otherHost.SendPrepareAndReceiveReadyTestAsync(other.Host, other.Host.IssueIsolatedTestCapability(), metadata.StageId, other.Identity, other.Offer, now, other.Clock, cancel.Token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.False(File.Exists(c.StagePath)); Assert.Equal(marker, await File.ReadAllBytesAsync(c.PromotionPath));
                Assert.True(c.Candidate == c.Peer.Get());
            }
            finally
            {
                cancel.Cancel(); if (send is not null) { try { await send.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
                if (receive is not null) { try { await receive.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
                if (!File.Exists(c.StagePath)) await File.WriteAllBytesAsync(c.StagePath, staged);
            }
        }
        finally { CleanVault(otherHostRoot); }
    }
}
