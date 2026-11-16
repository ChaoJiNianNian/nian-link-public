using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingAppliedCleanupTests
{
    [Theory]
    [InlineData("normal")]
    [InlineData("marker_already_removed")]
    [InlineData("tampered_marker")]
    [InlineData("revoked_after_review")]
    [InlineData("applied_without_final_ack")]
    public async Task AppliedEvidenceIsCleanedOnlyForTheExactCurrentCompletedTransaction(string scenario)
    {
        using var folder = new TestFolder();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), ct.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), ct.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peerCert),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, DeskLinkFingerprint.OfCertificate(hostCert), offer.KeyboardFingerprint, now.AddMinutes(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connecting = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, ct.Token);
        using var accepted = await listener.AcceptSocketAsync(ct.Token); await connecting;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, hostCert, identity, offer, ct.Token);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(client, peerCert, identity, offer, ct.Token);
        await Task.WhenAll(hostTask, peerTask);
        await using var host = await hostTask; await using var peer = await peerTask;
        await Task.WhenAll(host.ConfirmLocalAsync(ct.Token), peer.ReceiveRemoteConfirmationAsync(ct.Token));
        await Task.WhenAll(peer.ConfirmLocalAsync(ct.Token), host.ReceiveRemoteConfirmationAsync(ct.Token));
        using var hostDelivery = host.BeginDelivery(); using var peerDelivery = peer.BeginDelivery();
        var protector = new TestProtector(); var vaultPath = Path.Combine(folder.Path, "vault");
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector);
        await using var store = await PeerInvitationStore.OpenAsync(Path.Combine(folder.Path, "peer"), protector);
        var oldToken = await vault.AddConfirmedAsync("oldpeer", new string('A', 64), ct.Token);
        await hostDelivery.StageHostGrantAsync(vault, ct.Token);
        await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, ct.Token);
        await Task.WhenAll(hostDelivery.SendPrepareAndAwaitReadyAsync(ct.Token), peerDelivery.ReceivePrepareAndStageAsync(store, ct.Token));
        await hostDelivery.CommitVerifiedReadyAsync(ct.Token);
        await Task.WhenAll(hostDelivery.SendCommittedAsync(ct.Token), peerDelivery.ReceiveCommittedAsync(store, ct.Token));
        await Task.WhenAll(hostDelivery.ReceiveAppliedAsync(ct.Token), peerDelivery.PromoteAndSendAppliedAsync(store, ct.Token));
        Assert.Equal(2, vault.GetTrustedDevices().Count);
        Assert.True(vault.IsAuthorized("oldpeer", oldToken));
        var marker = Path.Combine(vaultPath, "pending-prepare.bin");
        var pending = Path.Combine(vaultPath, "pending-grant.bin");
        Assert.True(File.Exists(marker)); Assert.True(File.Exists(pending));
        if (scenario == "applied_without_final_ack")
        {
            var notFinished = await vault.GetPairingRecoveryStatusAsync(ct.Token);
            Assert.Equal(PairingRecoveryPhase.CommittedAwaitingPeer, notFinished.Phase);
            Assert.False(await vault.CleanupAppliedPairingAsync(notFinished, ct.Token));
            Assert.True(File.Exists(marker)); Assert.True(File.Exists(pending));
            return;
        }
        await Task.WhenAll(hostDelivery.SendFinalAndAwaitAckAsync(ct.Token),
            peerDelivery.ReceiveFinalAndFinalizeAsync(store, ct.Token));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(folder.Path, "peer"), "peer-completed-*.bin"));
        Assert.True(File.Exists(Path.Combine(folder.Path, "peer", "peer-retired.bin")));
        var reviewed = await vault.GetPairingRecoveryStatusAsync(ct.Token);
        Assert.Equal(PairingRecoveryPhase.AppliedCleanup, reviewed.Phase);
        if (scenario == "tampered_marker")
        {
            await File.WriteAllTextAsync(marker, "damaged protected test marker", ct.Token);
            Assert.False(await vault.CleanupAppliedPairingAsync(reviewed, ct.Token));
            Assert.True(File.Exists(marker)); Assert.True(File.Exists(pending));
            return;
        }
        if (scenario == "revoked_after_review")
        {
            Assert.True(await vault.RevokeAsync("peer", ct.Token));
            Assert.False(await vault.CleanupAppliedPairingAsync(reviewed, ct.Token));
            Assert.True(File.Exists(marker)); Assert.True(File.Exists(pending));
            Assert.True(vault.IsAuthorized("oldpeer", oldToken));
            return;
        }
        if (scenario == "marker_already_removed")
        {
            // Simulate process interruption after the first of the two ordered deletes.
            File.Delete(marker);
            await vault.DisposeAsync();
            await using var reopened = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector);
            var resumed = await reopened.GetPairingRecoveryStatusAsync(ct.Token);
            Assert.Equal(PairingRecoveryPhase.AppliedCleanup, resumed.Phase);
            Assert.True(await reopened.CleanupAppliedPairingAsync(resumed, ct.Token));
            Assert.False(File.Exists(marker)); Assert.False(File.Exists(pending));
            Assert.Equal(PairingRecoveryPhase.None, (await reopened.GetPairingRecoveryStatusAsync(ct.Token)).Phase);
            Assert.True(reopened.IsAuthorized("oldpeer", oldToken));
            Assert.Equal(2, reopened.GetTrustedDevices().Count);
            return;
        }
        Assert.True(await vault.CleanupAppliedPairingAsync(reviewed, ct.Token));
        Assert.False(await vault.CleanupAppliedPairingAsync(reviewed, ct.Token));
        Assert.False(File.Exists(marker)); Assert.False(File.Exists(pending));
        Assert.Equal(PairingRecoveryPhase.None, (await vault.GetPairingRecoveryStatusAsync(ct.Token)).Phase);
        Assert.True(vault.IsAuthorized("oldpeer", oldToken));
        Assert.Equal(2, vault.GetTrustedDevices().Count);
        // Successful cleanup must not leave the original marker blocking another device.
        var nextTime = DateTimeOffset.UtcNow;
        var nextOffer = new PeerIdentityOffer { Name = "nextpeer", Platform = "windows", KeyboardFingerprint = new string('D', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = nextTime.AddMinutes(2) };
        var nextIdentity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(nextOffer.Nonce, "N"), Guid.NewGuid(),
            "host", nextOffer.Name, identity.HostFingerprint, nextOffer.KeyboardFingerprint, nextTime.AddMinutes(1));
        var nextPending = await vault.StagePendingGrantAsync(nextIdentity, nextOffer, nextTime, ct.Token);
        Assert.Equal(nextOffer.Name, nextPending.PeerName);
        Assert.Equal(2, vault.GetTrustedDevices().Count);
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
