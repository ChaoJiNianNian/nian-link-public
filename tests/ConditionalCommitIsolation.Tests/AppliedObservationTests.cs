using System.Net;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Fact]
    public async Task RealTlsAppliedOnlyObservesPeerPromotionAndCurrentHostGrant()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var oldHost = await host.AddConfirmedAsync("old-device", new string('C', 64));
            var oldPeer = TestInvitation(f); await peer.SaveConfirmedAsync(oldPeer, f.Identity.HostFingerprint);
            var oldPath = Path.Combine(peerRoot, "peer-invitation.bin"); var oldBytes = await File.ReadAllBytesAsync(oldPath);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            var receive = f.Peer.ReceivePrepareReadyAndCommittedTestAsync(peer, f.Peer.IssueIsolatedTestCapability());
            var observe = host.SendPrepareReadyCommitNotifyAndObserveAppliedTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            var ticket = await receive.WaitAsync(TimeSpan.FromSeconds(5)); var pendingPath = Path.Combine(peerRoot, "peer-pending-invitation.bin");
            var pendingBytes = await File.ReadAllBytesAsync(pendingPath);
            await f.Peer.PromoteAndSendAppliedTestAsync(peer, ticket).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("applied-observed", await observe.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(host.IsAuthorized("old-device", oldHost)); Assert.True(host.IsAuthorized(f.Identity.PeerName, candidate.Token));
            Assert.True(candidate == peer.Get()); Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldPath)); Assert.Equal(pendingBytes, await File.ReadAllBytesAsync(pendingPath));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Peer.PromoteAndSendAppliedTestAsync(peer, ticket));
            await peer.DisposeAsync(); await using var reopened = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            Assert.Throws<InvalidOperationException>(() => reopened.Get());
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }
}
