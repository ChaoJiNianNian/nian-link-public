using System.Net;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Fact]
    public async Task PromotionPreservesOldFilesAndReopenFailsClosedUntilRecovery()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = TestInvitation(f); await peer.SaveConfirmedAsync(old, f.Identity.HostFingerprint);
            var oldPath = Path.Combine(peerRoot, "peer-invitation.bin"); var oldBytes = await File.ReadAllBytesAsync(oldPath);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            var receive = f.Peer.ReceivePrepareReadyAndCommittedTestAsync(peer, f.Peer.IssueIsolatedTestCapability());
            await host.SendPrepareReadyCommitAndNotifyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock).WaitAsync(TimeSpan.FromSeconds(5));
            var ticket = await receive.WaitAsync(TimeSpan.FromSeconds(5));
            var stagePath = Path.Combine(peerRoot, "peer-pending-invitation.bin"); var stagedBytes = await File.ReadAllBytesAsync(stagePath);
            Assert.Equal("promoted", await f.Peer.PromoteWithTicketTestAsync(peer, ticket));
            Assert.True(candidate == peer.Get()); Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldPath)); Assert.Equal(stagedBytes, await File.ReadAllBytesAsync(stagePath));
            Assert.True(File.Exists(Path.Combine(peerRoot, "peer-promotion.bin")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Peer.PromoteWithTicketTestAsync(peer, ticket));
            await Assert.ThrowsAsync<InvalidOperationException>(() => peer.SaveConfirmedAsync(old, f.Identity.HostFingerprint));
            await peer.DisposeAsync();
            await using var reopened = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            Assert.Throws<InvalidOperationException>(() => reopened.Get());
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SaveConfirmedAsync(old, f.Identity.HostFingerprint));
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldPath)); Assert.Equal(stagedBytes, await File.ReadAllBytesAsync(stagePath));
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task PromotionTemporaryConflictDoesNotOverwriteOrDeleteExistingFile()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = TestInvitation(f); await peer.SaveConfirmedAsync(old, f.Identity.HostFingerprint);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var receive = f.Peer.ReceivePrepareReadyAndCommittedTestAsync(peer, f.Peer.IssueIsolatedTestCapability());
            await host.SendPrepareReadyCommitAndNotifyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock).WaitAsync(TimeSpan.FromSeconds(5));
            var ticket = await receive.WaitAsync(TimeSpan.FromSeconds(5));
            var oldPath = Path.Combine(peerRoot, "peer-invitation.bin"); var pendingPath = Path.Combine(peerRoot, "peer-pending-invitation.bin");
            var oldBytes = await File.ReadAllBytesAsync(oldPath); var pendingBytes = await File.ReadAllBytesAsync(pendingPath);
            var temporary = Path.Combine(peerRoot, "peer-promotion.bin." + Guid.NewGuid().ToString("N") + ".tmp");
            var occupied = new byte[] { 11, 22, 33 }; await File.WriteAllBytesAsync(temporary, occupied); peer.PromotionTemporaryOverride = temporary;
            await Assert.ThrowsAnyAsync<IOException>(() => f.Peer.PromoteWithTicketTestAsync(peer, ticket));
            Assert.Equal(occupied, await File.ReadAllBytesAsync(temporary)); Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldPath));
            Assert.Equal(pendingBytes, await File.ReadAllBytesAsync(pendingPath)); Assert.True(old == peer.Get());
            Assert.False(File.Exists(Path.Combine(peerRoot, "peer-promotion.bin")));
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }
}
