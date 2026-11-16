using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingReadyCommitTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task ActualReadyCommitsOnceAndPreservesPreviouslyTrustedDevice(bool damageMarker, bool revokeAfterCommit, bool damageTrust, bool damageTrustAfterCommit)
    {
        using var folder = new TestFolder();
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), CancellationToken.None);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), CancellationToken.None);
        using var hostCertificate = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCertificate = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peerCertificate),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, DeskLinkFingerprint.OfCertificate(hostCertificate), offer.KeyboardFingerprint, now.AddMinutes(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var accepted = await listener.AcceptSocketAsync(timeout.Token);
        await connect;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, hostCertificate, identity, offer, timeout.Token);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(client, peerCertificate, identity, offer, timeout.Token);
        await Task.WhenAll(hostTask, peerTask);
        await using var host = await hostTask;
        await using var peer = await peerTask;
        await Task.WhenAll(host.ConfirmLocalAsync(timeout.Token), peer.ReceiveRemoteConfirmationAsync(timeout.Token));
        await Task.WhenAll(peer.ConfirmLocalAsync(timeout.Token), host.ReceiveRemoteConfirmationAsync(timeout.Token));
        using var hostDelivery = host.BeginDelivery();
        using var peerDelivery = peer.BeginDelivery();
        var operation = peerDelivery.ReceivePairingOperationAsync(timeout.Token);
        await hostDelivery.SendPairingOperationAsync(false, timeout.Token);
        Assert.False(await operation);
        var protector = new TestProtector();
        var vaultPath = Path.Combine(folder.Path, "vault");
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector);
        await using var store = await PeerInvitationStore.OpenAsync(Path.Combine(folder.Path, "peer"), protector);
        var oldToken = await vault.AddConfirmedAsync("oldpeer", new string('A', 64), timeout.Token);
        await hostDelivery.StageHostGrantAsync(vault, timeout.Token);
        await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);

        Assert.Throws<InvalidOperationException>(() => hostDelivery.ClaimVerifiedReady(timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hostDelivery.SendCommittedAsync(timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peerDelivery.PromoteAndSendAppliedAsync(store, timeout.Token));
        Assert.Single(vault.GetTrustedDevices());
        var receive = peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token);
        var send = hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token);
        await Task.WhenAll(send, receive).WaitAsync(TimeSpan.FromSeconds(10));
        var reservationFile = Path.Combine(folder.Path, "peer", "peer-reservation.bin");
        Assert.True(File.Exists(reservationFile));
        var reservedBytes = await File.ReadAllBytesAsync(reservationFile, timeout.Token);
        File.Delete(reservationFile);
        Assert.Equal(PairingRecoveryPhase.EvidenceProblem, (await store.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        await File.WriteAllBytesAsync(reservationFile, reservedBytes, timeout.Token);
        if (damageMarker)
        {
            await File.WriteAllTextAsync(Path.Combine(vaultPath, "pending-prepare.bin"), "{}", timeout.Token);
            await Assert.ThrowsAnyAsync<Exception>(() => hostDelivery.CommitVerifiedReadyAsync(timeout.Token));
            Assert.Single(vault.GetTrustedDevices());
            Assert.True(vault.IsAuthorized("oldpeer", oldToken));
            return;
        }
        if (damageTrust)
        {
            var trustFile = Path.Combine(vaultPath, "multi-device-trust.bin");
            const string corrupted = "corrupted protected trust";
            await File.WriteAllTextAsync(trustFile, corrupted, timeout.Token);
            await Assert.ThrowsAnyAsync<Exception>(() => hostDelivery.CommitVerifiedReadyAsync(timeout.Token));
            Assert.Equal(corrupted, await File.ReadAllTextAsync(trustFile, timeout.Token));
            Assert.False(vault.IsAuthorized("oldpeer", oldToken));
            Assert.Equal(PairingRecoveryPhase.EvidenceProblem, (await vault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
            return;
        }
        await hostDelivery.CommitVerifiedReadyAsync(timeout.Token);
        Assert.Equal(2, vault.GetTrustedDevices().Count);
        Assert.True(vault.IsAuthorized("oldpeer", oldToken));
        Assert.Null(store.Get());
        if (damageTrustAfterCommit)
        {
            var trustFile = Path.Combine(vaultPath, "multi-device-trust.bin");
            const string corrupted = "corrupted protected trust after commit";
            await File.WriteAllTextAsync(trustFile, corrupted, timeout.Token);
            await Assert.ThrowsAnyAsync<Exception>(() => hostDelivery.SendCommittedAsync(timeout.Token));
            Assert.Equal(corrupted, await File.ReadAllTextAsync(trustFile, timeout.Token));
            Assert.False(vault.IsAuthorized("oldpeer", oldToken));
            Assert.Equal(PairingRecoveryPhase.EvidenceProblem, (await vault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
            return;
        }
        if (revokeAfterCommit)
        {
            Assert.True(await vault.RevokeAsync("peer", timeout.Token));
            await Assert.ThrowsAnyAsync<Exception>(() => hostDelivery.SendCommittedAsync(timeout.Token));
            Assert.True(vault.IsAuthorized("oldpeer", oldToken));
            Assert.Null(store.Get());
            return;
        }
        var committedReceived = peerDelivery.ReceiveCommittedAsync(store, timeout.Token);
        var committedSent = hostDelivery.SendCommittedAsync(timeout.Token);
        await Task.WhenAll(committedReceived, committedSent).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(await send, await committedReceived);
        Assert.Null(store.Get()); // Receipt alone must not activate a pairing.
        var appliedSent = peerDelivery.PromoteAndSendAppliedAsync(store, timeout.Token);
        var appliedReceived = hostDelivery.ReceiveAppliedAsync(timeout.Token);
        await Task.WhenAll(appliedSent, appliedReceived).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<PairingRecoveryRequiredException>(() => store.Get());
        var promotionPath = Path.Combine(folder.Path, "peer", "peer-promotion.bin");
        var promotionBytes = await File.ReadAllBytesAsync(promotionPath, timeout.Token);
        File.Delete(promotionPath);
        Assert.Throws<PairingRecoveryRequiredException>(() => store.Get());
        var pendingPath = Path.Combine(folder.Path, "peer", "peer-pending-invitation.bin");
        var readyPath = Path.Combine(folder.Path, "peer", "peer-ready-evidence.bin");
        var pendingBytes = await File.ReadAllBytesAsync(pendingPath, timeout.Token);
        var readyBytes = await File.ReadAllBytesAsync(readyPath, timeout.Token);
        File.Delete(pendingPath); File.Delete(readyPath);
        var unrelated = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, HostName = "otherhost", PeerName = "peer",
            CertificateSha256 = new string('B', 64), Token = Convert.ToBase64String(new byte[32]) };
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveConfirmedAsync(unrelated, unrelated.CertificateSha256, timeout.Token));
        await File.WriteAllBytesAsync(pendingPath, pendingBytes, timeout.Token);
        await File.WriteAllBytesAsync(readyPath, readyBytes, timeout.Token);
        await File.WriteAllBytesAsync(promotionPath, promotionBytes, timeout.Token);
        var savedVault = await File.ReadAllTextAsync(Path.Combine(vaultPath, "multi-device-trust.bin"), timeout.Token);
        Assert.Contains("\"state\":\"applied\"", savedVault);
        await Assert.ThrowsAsync<InvalidOperationException>(() => peerDelivery.PromoteAndSendAppliedAsync(store, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hostDelivery.ReceiveAppliedAsync(timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hostDelivery.SendCommittedAsync(timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hostDelivery.CommitVerifiedReadyAsync(timeout.Token));

        await vault.DisposeAsync();
        await using var reopened = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector);
        Assert.Equal(2, reopened.GetTrustedDevices().Count);
        Assert.True(reopened.IsAuthorized("oldpeer", oldToken));
        await store.DisposeAsync();
        var peerDirectory = Path.Combine(folder.Path, "peer");
        await using var reopenedPeer = await PeerInvitationStore.OpenAsync(peerDirectory, protector);
        Assert.Throws<PairingRecoveryRequiredException>(() => reopenedPeer.Get());
        await reopenedPeer.DisposeAsync();
        await File.WriteAllTextAsync(Path.Combine(peerDirectory, "peer-ready-evidence.bin"), "{}", timeout.Token);
        await using var damagedPeer = await PeerInvitationStore.OpenAsync(peerDirectory, protector, timeout.Token);
        Assert.Throws<PairingRecoveryRequiredException>(() => damagedPeer.Get());
        Assert.Equal(PairingRecoveryPhase.EvidenceProblem, (await damagedPeer.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        Assert.True(await reopened.RevokeAsync("peer", timeout.Token));
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }
}
