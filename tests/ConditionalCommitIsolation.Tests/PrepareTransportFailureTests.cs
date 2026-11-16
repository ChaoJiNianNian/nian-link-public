using System.Net;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Fact]
    public async Task ProxyDropsReadyCiphertextAfterPeerProtectedStage()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var beforeReady = new ControlledLivePublishBarrier(); var afterReady = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(throughProxy: true); await f.BeginAsync(); await f.ConfirmAsync();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = TestInvitation(f); await peer.SaveConfirmedAsync(old, f.Identity.HostFingerprint);
            var now = f.Clock(); var stage = (await vault.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await vault.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            f.Peer.BeforeLiveReady = beforeReady; f.Peer.AfterLiveReadySent = afterReady;
            var receiving = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability());
            var sending = vault.SendPrepareAndReceiveReadyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            try
            {
                await beforeReady.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(stage, (await peer.ReadLivePeerStageTestAsync())!.StageId);
                f.Proxy!.DropPeerToHost(); beforeReady.Release();
                await afterReady.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                await f.Proxy.Dropped.WaitAsync(TimeSpan.FromSeconds(5));
                f.Proxy.CutConnections();
                await Assert.ThrowsAnyAsync<IOException>(() => sending.WaitAsync(TimeSpan.FromSeconds(5)));
                afterReady.Release(); Assert.Equal(stage, (await receiving.WaitAsync(TimeSpan.FromSeconds(5))).StageId);
                Assert.True(old == peer.Get()); Assert.True(File.Exists(Path.Combine(hostRoot, "pending-prepare-unknown.bin")));
                Assert.NotNull(await vault.GetPendingGrantMetadataAsync(now));
                await Assert.ThrowsAsync<InvalidOperationException>(() => vault.AbortPendingGrantAsync(stage, f.Identity));
                Assert.Empty(vault.GetTrustedDevices());
            }
            finally { beforeReady.Release(); afterReady.Release(); }
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task TruncatedPrepareOverTlsDoesNotPublishPending()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            var old = TestInvitation(f); await store.SaveConfirmedAsync(old, f.Identity.HostFingerprint);
            var data = f.Host.MakePrepareFrameForTest(Guid.NewGuid(), TestInvitation(f), 1, 24802);
            try
            {
                Assert.True(data.Length > 100);
                var receive = f.Peer.ReceivePrepareAndStageTestAsync(store, f.Peer.IssueIsolatedTestCapability());
                await f.HostStream.WriteAsync(data.AsMemory(0, 100)); await f.HostStream.FlushAsync();
                await f.Host.DisposeAsync();
                await Assert.ThrowsAnyAsync<IOException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Null(await store.ReadLivePeerStageTestAsync()); Assert.True(old == store.Get());
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(data); }
        }
        finally { CleanVault(root); }
    }
}
