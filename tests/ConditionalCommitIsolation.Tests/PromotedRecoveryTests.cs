using System.Net;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Fact]
    public async Task PromotedTransactionCanBeReauthenticatedReadOnlyAfterStoresReopen()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            var files = new[] { Path.Combine(hostRoot, "multi-device-trust.bin"), Path.Combine(hostRoot, "pending-grant.bin"), Path.Combine(hostRoot, "pending-prepare-unknown.bin"),
                Path.Combine(peerRoot, "peer-invitation.bin"), Path.Combine(peerRoot, "peer-pending-invitation.bin"), Path.Combine(peerRoot, "peer-promotion.bin") };
            byte[][] before; string oldHost, token; MultiPeerInvitation oldPeer;
            await using (var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector))
            await using (var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector))
            {
                oldHost = await host.AddConfirmedAsync("old-device", new string('C', 64)); oldPeer = TestInvitation(f);
                await peer.SaveConfirmedAsync(oldPeer, f.Identity.HostFingerprint);
                var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
                await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
                token = (await ReadOfflineHostInvitationAsync(hostRoot, protector)).Token;
                var receive = f.Peer.ReceivePrepareReadyAndCommittedTestAsync(peer, f.Peer.IssueIsolatedTestCapability());
                var observe = host.SendPrepareReadyCommitNotifyAndObserveAppliedTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
                var ticket = await receive.WaitAsync(TimeSpan.FromSeconds(5)); await f.Peer.PromoteAndSendAppliedTestAsync(peer, ticket).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("applied-observed", await observe.WaitAsync(TimeSpan.FromSeconds(5)));
                await f.Host.DisposeAsync(); await f.Peer.DisposeAsync(); before = await Task.WhenAll(files.Select(path => File.ReadAllBytesAsync(path)));
            }
            using var hostCert = PrivateFiles.LoadTlsCertificate(f.HostCertificatePath); using var peerCert = PrivateFiles.LoadTlsCertificate(f.PeerCertificatePath);
            await using var reopenedHost = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var reopenedPeer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            Assert.Throws<InvalidOperationException>(() => reopenedPeer.Get());
            var result = await IsolatedRecoveryQuery.RunPromotedAsync(reopenedHost, reopenedPeer, hostCert, peerCert, now, clock: () => now);
            Assert.NotNull(result); Assert.True(result.Matched); Assert.Equal("promotion-current-grant", result.Scope); Assert.False(result.HistoryExpired);
            for (var i = 0; i < files.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(files[i]));
            Assert.True(reopenedHost.IsAuthorized("old-device", oldHost)); Assert.True(reopenedHost.IsAuthorized(f.Identity.PeerName, token));
            Assert.Throws<InvalidOperationException>(() => reopenedPeer.Get());
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }
}
