using System.Net;
using System.Security.Cryptography;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Fact]
    public async Task CommittedOverOriginalTlsIssuesOnlySingleBoundPeerTicket()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var otherRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var a = await Fixture.OpenAsync(); await using var b = await Fixture.OpenAsync(sameIdentity: a);
            await a.BeginAsync(); await a.ConfirmAsync(); await b.BeginAsync(); await b.ConfirmAsync(); var now = a.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            await using var other = await PeerInvitationStore.OpenAsync(otherRoot, protector);
            var old = await host.AddConfirmedAsync("old-device", new string('C', 64));
            var oldPeer = TestInvitation(a); await peer.SaveConfirmedAsync(oldPeer, a.Identity.HostFingerprint);
            var stage = (await host.StagePendingGrantAsync(a.Identity, a.Offer, now, clock: a.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, a.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: a.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            var activeBefore = await File.ReadAllBytesAsync(Path.Combine(peerRoot, "peer-invitation.bin"));
            var receive = a.Peer.ReceivePrepareReadyAndCommittedTestAsync(peer, a.Peer.IssueIsolatedTestCapability());
            var send = host.SendPrepareReadyCommitAndNotifyTestAsync(a.Host, a.Host.IssueIsolatedTestCapability(), stage, a.Identity, a.Offer, now, a.Clock);
            Assert.Equal("committed-notified", await send.WaitAsync(TimeSpan.FromSeconds(5)));
            var ticket = await receive.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(host.IsAuthorized("old-device", old)); Assert.True(host.IsAuthorized(a.Identity.PeerName, candidate.Token));
            Assert.True(oldPeer == peer.Get()); Assert.Equal(activeBefore, await File.ReadAllBytesAsync(Path.Combine(peerRoot, "peer-invitation.bin")));
            Assert.DoesNotContain(candidate.Token, ticket.ToString()!);
            Assert.Throws<NotSupportedException>(() => System.Text.Json.JsonSerializer.Serialize(ticket, ticket.GetType()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Peer.ConsumePromotionTicketTestAsync(peer, ticket));
            await Assert.ThrowsAsync<InvalidOperationException>(() => a.Peer.ConsumePromotionTicketTestAsync(other, ticket));
            Assert.True(await a.Peer.ConsumePromotionTicketTestAsync(peer, ticket));
            await Assert.ThrowsAsync<InvalidOperationException>(() => a.Peer.ConsumePromotionTicketTestAsync(peer, ticket));
            Assert.True(oldPeer == peer.Get()); Assert.Equal(activeBefore, await File.ReadAllBytesAsync(Path.Combine(peerRoot, "peer-invitation.bin")));
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); CleanVault(otherRoot); }
    }

    [Fact]
    public async Task DirectCommittedWithoutVaultCommitCannotIssueTicket()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var beforeReady = new ControlledLivePublishBarrier(); using var cancel = new CancellationTokenSource();
        Task? receive = null, send = null;
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            var frozen = await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector); var capability = f.Host.IssueIsolatedTestCapability();
            f.Peer.BeforeLiveReady = beforeReady;
            receive = f.Peer.ReceivePrepareReadyAndCommittedTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), cancel.Token);
            send = host.SendPrepareAndReceiveReadyTestAsync(f.Host, capability, stage, f.Identity, f.Offer, now, f.Clock, cancel.Token);
            try
            {
                await beforeReady.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                var payload = new LivePreparePayload(stage, frozen.Slot, frozen.DataPortBase, frozen.InvitationDigest, candidate);
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.SendCommittedFrameTestAsync(capability, host, new object(), payload, cancel.Token));
                Assert.False(host.IsAuthorized(f.Identity.PeerName, candidate.Token));
            }
            finally
            {
                cancel.Cancel(); beforeReady.Release();
                if (send is not null) { try { await send.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
                if (receive is not null) { try { await receive.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
            }
        }
        finally { beforeReady.Release(); CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("active")]
    [InlineData("protector")]
    [InlineData("cancel")]
    [InlineData("expiry")]
    [InlineData("close")]
    public async Task TicketCannotBeConsumedAfterPeerStateOrSessionChanges(string change)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var windows = new WindowsCredentialProtector(); var protector = new RecoveryFaultProtector();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, windows);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var oldPeer = TestInvitation(f); await peer.SaveConfirmedAsync(oldPeer, f.Identity.HostFingerprint);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var receive = f.Peer.ReceivePrepareReadyAndCommittedTestAsync(peer, f.Peer.IssueIsolatedTestCapability());
            await host.SendPrepareReadyCommitAndNotifyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock).WaitAsync(TimeSpan.FromSeconds(5));
            var ticket = await receive.WaitAsync(TimeSpan.FromSeconds(5));
            if (change == "pending")
            {
                var path = Path.Combine(peerRoot, "peer-pending-invitation.bin"); var plain = windows.Unprotect(await File.ReadAllBytesAsync(path));
                try { await File.WriteAllBytesAsync(path, windows.Protect(plain)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            if (change == "active") await peer.SaveConfirmedAsync(oldPeer with { Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }, f.Identity.HostFingerprint);
            if (change == "protector") protector.Unavailable = true;
            if (change == "cancel") cancellation.Cancel();
            if (change == "expiry") Interlocked.Exchange(ref f.UtcTicks, f.Identity.ExpiresAtUtc.UtcTicks);
            if (change == "close") await f.Peer.DisposeAsync();
            var error = await Record.ExceptionAsync(() => f.Peer.ConsumePromotionTicketTestAsync(peer, ticket, cancellation.Token));
            Assert.True(error is InvalidOperationException or OperationCanceledException, $"Unexpected ticket outcome: {error?.GetType().Name ?? "success"}");
            if (change != "active") Assert.True(oldPeer == peer.Get());
            Assert.True(File.Exists(Path.Combine(peerRoot, "peer-pending-invitation.bin")));
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Theory]
    [InlineData("before-verify")]
    [InlineData("before-send")]
    public async Task RevocationBeforeCommittedTransmissionPreventsNotificationAndTicket(string moment)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var beforeVerify = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = await host.AddConfirmedAsync("old-device", new string('C', 64)); var oldPeer = TestInvitation(f);
            await peer.SaveConfirmedAsync(oldPeer, f.Identity.HostFingerprint);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            if (moment == "before-verify") host.BeforeCommittedVerify = beforeVerify; else host.BeforeCommittedSend = beforeVerify;
            var receive = f.Peer.ReceivePrepareReadyAndCommittedTestAsync(peer, f.Peer.IssueIsolatedTestCapability());
            var send = host.SendPrepareReadyCommitAndNotifyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            try
            {
                await beforeVerify.Entered.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(host.IsAuthorized(f.Identity.PeerName, candidate.Token));
                Assert.True(await host.RevokeAsync(f.Identity.PeerName)); beforeVerify.Release();
                await Assert.ThrowsAsync<InvalidOperationException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
                var error = await Record.ExceptionAsync(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error is IOException or InvalidOperationException, $"Unexpected peer outcome: {error?.GetType().Name ?? "success"}");
                Assert.True(host.IsAuthorized("old-device", old)); Assert.False(host.IsAuthorized(f.Identity.PeerName, candidate.Token)); Assert.True(oldPeer == peer.Get());
            }
            finally { beforeVerify.Release(); }
        }
        finally { beforeVerify.Release(); CleanVault(hostRoot); CleanVault(peerRoot); }
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
    public async Task CorruptedCommittedFrameCannotIssuePeerTicket(ReadyWireFault fault)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var oldPeer = TestInvitation(f); await peer.SaveConfirmedAsync(oldPeer, f.Identity.HostFingerprint);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            f.Host.ReadyWireControls = new ReadyWireControls { Fault = fault };
            var receive = f.Peer.ReceivePrepareReadyAndCommittedTestAsync(peer, f.Peer.IssueIsolatedTestCapability());
            var send = host.SendPrepareReadyCommitAndNotifyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            Assert.Equal("committed-notified", await send.WaitAsync(TimeSpan.FromSeconds(5)));
            var error = await Record.ExceptionAsync(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
            if (fault == ReadyWireFault.BadType) Assert.IsType<InvalidOperationException>(error); else Assert.IsType<InvalidDataException>(error);
            Assert.True(f.Host.ReadyWireControls.FrameWritten); Assert.True(oldPeer == peer.Get());
            Assert.True(host.IsAuthorized(f.Identity.PeerName, candidate.Token));
            Assert.Equal("v3-history", (await peer.InspectPeerV3IsolatedTestAsync(now)).State);
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task ReprotectedPeerV3BetweenReadyAndCommittedCannotIssueTicket()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var beforeSend = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = TestInvitation(f); await peer.SaveConfirmedAsync(old, f.Identity.HostFingerprint);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            host.BeforeCommittedSend = beforeSend;
            var receive = f.Peer.ReceivePrepareReadyAndCommittedTestAsync(peer, f.Peer.IssueIsolatedTestCapability());
            var send = host.SendPrepareReadyCommitAndNotifyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            try
            {
                await beforeSend.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                var path = Path.Combine(peerRoot, "peer-pending-invitation.bin"); var plain = protector.Unprotect(await File.ReadAllBytesAsync(path));
                try { await File.WriteAllBytesAsync(path, protector.Protect(plain)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                beforeSend.Release();
                await send.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<InvalidOperationException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(old == peer.Get());
            }
            finally { beforeSend.Release(); }
        }
        finally { beforeSend.Release(); CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task CommittedFrameReplayedAcrossRealTlsSessionsCannotIssueTicket()
    {
        var hostA = VaultRoot(); var hostB = VaultRoot(); var peerA = VaultRoot(); var peerB = VaultRoot(); var protector = new WindowsCredentialProtector();
        byte[]? first = null, second = null;
        try
        {
            await using var a = await Fixture.OpenAsync(); await using var b = await Fixture.OpenAsync(sameIdentity: a);
            Assert.NotEqual(a.Port, b.Port); await a.BeginAsync(); await a.ConfirmAsync(); await b.BeginAsync(); await b.ConfirmAsync(); var now = a.Clock();
            await using var ha = await MultiDeviceCredentialVault.OpenAsync(hostA, protector);
            await using var pa = await PeerInvitationStore.OpenAsync(peerA, protector);
            var stage = (await ha.StagePendingGrantAsync(a.Identity, a.Offer, now, clock: a.Clock)).StageId;
            await ha.FreezePendingInvitationForLoopbackTestAsync(stage, a.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: a.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostA, protector);
            Directory.CreateDirectory(hostB);
            await File.WriteAllBytesAsync(Path.Combine(hostB, "pending-grant.bin"), await File.ReadAllBytesAsync(Path.Combine(hostA, "pending-grant.bin")));
            await using var hb = await MultiDeviceCredentialVault.OpenAsync(hostB, protector);
            await using var pb = await PeerInvitationStore.OpenAsync(peerB, protector);
            var oldPeer = TestInvitation(b); await pb.SaveConfirmedAsync(oldPeer, b.Identity.HostFingerprint);
            var oldBytes = await File.ReadAllBytesAsync(Path.Combine(peerB, "peer-invitation.bin"));
            a.Host.ReadyWireControls = new ReadyWireControls { Capture = true };
            var receiveA = a.Peer.ReceivePrepareReadyAndCommittedTestAsync(pa, a.Peer.IssueIsolatedTestCapability());
            await ha.SendPrepareReadyCommitAndNotifyTestAsync(a.Host, a.Host.IssueIsolatedTestCapability(), stage, a.Identity, a.Offer, now, a.Clock).WaitAsync(TimeSpan.FromSeconds(5));
            await receiveA.WaitAsync(TimeSpan.FromSeconds(5)); first = a.Host.ReadyWireControls.Captured ?? throw new InvalidOperationException();
            b.Host.ReadyWireControls = new ReadyWireControls { Capture = true, Replay = first };
            var receiveB = b.Peer.ReceivePrepareReadyAndCommittedTestAsync(pb, b.Peer.IssueIsolatedTestCapability());
            await hb.SendPrepareReadyCommitAndNotifyTestAsync(b.Host, b.Host.IssueIsolatedTestCapability(), stage, b.Identity, b.Offer, now, b.Clock).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidDataException>(() => receiveB.WaitAsync(TimeSpan.FromSeconds(5)));
            second = b.Host.ReadyWireControls.Captured ?? throw new InvalidOperationException();
            using var docA = System.Text.Json.JsonDocument.Parse(first); using var docB = System.Text.Json.JsonDocument.Parse(second);
            foreach (var name in new[] { "version", "type", "role", "sessionId", "transactionId", "offerNonce", "stageId", "slot", "dataPortBase", "digest" })
                Assert.True(docA.RootElement.GetProperty(name).GetRawText() == docB.RootElement.GetProperty(name).GetRawText());
            Assert.True(docA.RootElement.GetProperty("senderChallenge").GetString() != docB.RootElement.GetProperty("senderChallenge").GetString());
            Assert.True(docA.RootElement.GetProperty("receiverChallenge").GetString() != docB.RootElement.GetProperty("receiverChallenge").GetString());
            Assert.True(b.Host.ReadyWireControls.FrameWritten); Assert.True(hb.IsAuthorized(b.Identity.PeerName, candidate.Token));
            Assert.Equal("v3-history", (await pb.InspectPeerV3IsolatedTestAsync(now)).State);
            Assert.True(oldPeer == pb.Get()); Assert.Equal(oldBytes, await File.ReadAllBytesAsync(Path.Combine(peerB, "peer-invitation.bin")));
        }
        finally
        {
            if (first is not null) CryptographicOperations.ZeroMemory(first); if (second is not null) CryptographicOperations.ZeroMemory(second);
            CleanVault(hostA); CleanVault(hostB); CleanVault(peerA); CleanVault(peerB);
        }
    }

    [Fact]
    public async Task PeerProtectionUnavailableBeforeCommittedDoesNotIssueTicket()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var windows = new WindowsCredentialProtector(); var protector = new RecoveryFaultProtector();
        var beforeSend = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, windows);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = TestInvitation(f); await peer.SaveConfirmedAsync(old, f.Identity.HostFingerprint);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            host.BeforeCommittedSend = beforeSend;
            var receive = f.Peer.ReceivePrepareReadyAndCommittedTestAsync(peer, f.Peer.IssueIsolatedTestCapability());
            var send = host.SendPrepareReadyCommitAndNotifyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            try
            {
                await beforeSend.Entered.WaitAsync(TimeSpan.FromSeconds(5)); protector.Unavailable = true; beforeSend.Release();
                await send.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<InvalidOperationException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(old == peer.Get());
            }
            finally { beforeSend.Release(); }
        }
        finally { beforeSend.Release(); CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task PeerDisconnectBeforeCommittedKeepsPeerOldInvitation()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var beforeSend = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = TestInvitation(f); await peer.SaveConfirmedAsync(old, f.Identity.HostFingerprint);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector); host.BeforeCommittedSend = beforeSend;
            var receive = f.Peer.ReceivePrepareReadyAndCommittedTestAsync(peer, f.Peer.IssueIsolatedTestCapability());
            var send = host.SendPrepareReadyCommitAndNotifyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            try
            {
                await beforeSend.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                await f.Peer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); beforeSend.Release();
                var peerError = await Record.ExceptionAsync(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(peerError is IOException or OperationCanceledException or InvalidOperationException);
                var hostError = await Record.ExceptionAsync(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(hostError is null or IOException or InvalidOperationException);
                Assert.True(host.IsAuthorized(f.Identity.PeerName, candidate.Token)); Assert.True(old == peer.Get());
                Assert.Equal("v3-history", (await peer.InspectPeerV3IsolatedTestAsync(now)).State);
            }
            finally { beforeSend.Release(); }
        }
        finally { beforeSend.Release(); CleanVault(hostRoot); CleanVault(peerRoot); }
    }
}
