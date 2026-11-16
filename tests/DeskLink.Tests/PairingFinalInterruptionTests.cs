using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingFinalInterruptionTests
{
    [Theory]
    [InlineData("disconnect")]
    [InlineData("cancel")]
    [InlineData("revoke")]
    [InlineData("finished")]
    [InlineData("retired_ack_lost")]
    [InlineData("retired_ack_lost_replaced")]
    public async Task LostOrUnauthorizedFinalPreservesBothSidesEvidenceAndOldAuthorization(string scenario)
    {
        using var folder = new TestFolder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), timeout.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), timeout.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peerCert),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, DeskLinkFingerprint.OfCertificate(hostCert), offer.KeyboardFingerprint, now.AddMinutes(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connecting = socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var accepted = await listener.AcceptSocketAsync(timeout.Token); await connecting;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, hostCert, identity, offer, timeout.Token);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(socket, peerCert, identity, offer, timeout.Token);
        await Task.WhenAll(hostTask, peerTask);
        await using var host = await hostTask; await using var peer = await peerTask;
        await Task.WhenAll(host.ConfirmLocalAsync(timeout.Token), peer.ReceiveRemoteConfirmationAsync(timeout.Token));
        await Task.WhenAll(peer.ConfirmLocalAsync(timeout.Token), host.ReceiveRemoteConfirmationAsync(timeout.Token));
        using var hostDelivery = host.BeginDelivery(); using var peerDelivery = peer.BeginDelivery();
        var protector = new TestProtector();
        var vaultPath = Path.Combine(folder.Path, "vault"); var peerPath = Path.Combine(folder.Path, "peer");
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector);
        await using var store = await PeerInvitationStore.OpenAsync(peerPath, protector);
        var oldToken = await vault.AddConfirmedAsync("oldpeer", new string('A', 64), timeout.Token);
        await hostDelivery.StageHostGrantAsync(vault, timeout.Token);
        await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);
        await Task.WhenAll(hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token), peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token));
        await hostDelivery.CommitVerifiedReadyAsync(timeout.Token);
        await Task.WhenAll(hostDelivery.SendCommittedAsync(timeout.Token), peerDelivery.ReceiveCommittedAsync(store, timeout.Token));
        await Task.WhenAll(hostDelivery.ReceiveAppliedAsync(timeout.Token), peerDelivery.PromoteAndSendAppliedAsync(store, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hostDelivery.CleanupFinishedDeliveryAsync(timeout.Token));
        Assert.True(File.Exists(Path.Combine(vaultPath, "pending-prepare.bin")));
        Assert.True(File.Exists(Path.Combine(vaultPath, "pending-grant.bin")));

        if (scenario is "retired_ack_lost" or "retired_ack_lost_replaced")
        {
            // The peer has durably retired its receipt, but fails before writing ACK.
            store.RetirementCheckpointForTesting = point => { if (point == "source-deleted") throw new IOException("simulated lost retirement acknowledgement"); };
            await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(hostDelivery.SendFinalAndAwaitAckAsync(timeout.Token),
                peerDelivery.ReceiveFinalAndFinalizeAsync(store, timeout.Token)).WaitAsync(TimeSpan.FromSeconds(8)));
            store.RetirementCheckpointForTesting = null;
            Assert.Equal(PairingRecoveryPhase.CommittedAwaitingPeer, (await vault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
            Assert.True(File.Exists(Path.Combine(vaultPath, "pending-prepare.bin")));
            Assert.True(File.Exists(Path.Combine(vaultPath, "pending-grant.bin")));
            Assert.True(File.Exists(Path.Combine(peerPath, "peer-retired.bin")));
            await store.DisposeAsync(); await vault.DisposeAsync();
            await using var reopenedStore = await PeerInvitationStore.OpenAsync(peerPath, protector, timeout.Token);
            await using var reopenedVault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector, timeout.Token);
            MultiPeerInvitation? replacement = null;
            if (scenario == "retired_ack_lost_replaced")
            {
                replacement = reopenedStore.Get()! with { HostName = "newhost", CertificateSha256 = new string('D', 64),
                    Token = Convert.ToBase64String(Enumerable.Repeat((byte)8, 32).ToArray()) };
                await reopenedStore.SaveConfirmedAsync(replacement, replacement.CertificateSha256, timeout.Token);
            }
            var freshNow = DateTimeOffset.UtcNow;
            var freshOffer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peerCert),
                Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = freshNow.AddMinutes(2) };
            var freshIdentity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(freshOffer.Nonce, "N"), Guid.NewGuid(),
                "host", freshOffer.Name, DeskLinkFingerprint.OfCertificate(hostCert), freshOffer.KeyboardFingerprint, freshNow.AddMinutes(1));
            using var freshListener = new TcpListener(IPAddress.Loopback, 0); freshListener.Start(1);
            using var freshSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var connectingFresh = freshSocket.ConnectAsync((IPEndPoint)freshListener.LocalEndpoint, timeout.Token);
            using var acceptedFresh = await freshListener.AcceptSocketAsync(timeout.Token); await connectingFresh;
            var freshHostTask = PairingAuthenticatedChannel.StartHostAsync(acceptedFresh, hostCert, freshIdentity, freshOffer, timeout.Token);
            var freshPeerTask = PairingAuthenticatedChannel.StartPeerAsync(freshSocket, peerCert, freshIdentity, freshOffer, timeout.Token);
            await Task.WhenAll(freshHostTask, freshPeerTask);
            await using var freshHost = await freshHostTask; await using var freshPeer = await freshPeerTask;
            await Task.WhenAll(freshHost.ConfirmLocalAsync(timeout.Token), freshPeer.ReceiveRemoteConfirmationAsync(timeout.Token));
            await Task.WhenAll(freshPeer.ConfirmLocalAsync(timeout.Token), freshHost.ReceiveRemoteConfirmationAsync(timeout.Token));
            using var freshHostDelivery = freshHost.BeginDelivery(); using var freshPeerDelivery = freshPeer.BeginDelivery();
            var results = await Task.WhenAll(freshHostDelivery.ResumeHistoricalCommittedAsync(reopenedVault, timeout.Token),
                freshPeerDelivery.AnswerHistoricalCommittedAsync(reopenedStore, timeout.Token)).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.All(results, Assert.True);
            Assert.Equal(PairingRecoveryPhase.AppliedCleanup, (await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
            await freshHostDelivery.CleanupHistoricalFinishedDeliveryAsync(timeout.Token);
            Assert.Equal(PairingRecoveryPhase.None, (await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
            Assert.True(reopenedVault.IsAuthorized("oldpeer", oldToken));
            if (replacement is null) Assert.Equal("peer", reopenedStore.Get()!.PeerName);
            else Assert.Equal(replacement, reopenedStore.Get());
            return;
        }

        if (scenario == "finished")
        {
            await Task.WhenAll(hostDelivery.SendFinalAndAwaitAckAsync(timeout.Token),
                peerDelivery.ReceiveFinalAndFinalizeAsync(store, timeout.Token));
            await hostDelivery.CleanupFinishedDeliveryAsync(timeout.Token);
            Assert.Equal(PairingRecoveryPhase.None, (await vault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
            Assert.False(File.Exists(Path.Combine(vaultPath, "pending-prepare.bin")));
            Assert.False(File.Exists(Path.Combine(vaultPath, "pending-grant.bin")));
            Assert.True(vault.IsAuthorized("oldpeer", oldToken));
            await store.DisposeAsync();
            await using var finalPeer = await PeerInvitationStore.OpenAsync(peerPath, protector, timeout.Token);
            Assert.Equal("peer", finalPeer.Get()!.PeerName);
            return;
        }

        if (scenario == "cancel")
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hostDelivery.SendFinalAndAwaitAckAsync(cancelled.Token));
        }
        else if (scenario == "disconnect")
        {
            // Break the transport while its delivery lease is still held;
            // disposing the channel itself waits for that lease to finish.
            socket.Dispose();
            await Assert.ThrowsAnyAsync<Exception>(() => hostDelivery.SendFinalAndAwaitAckAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            Assert.True(await vault.RevokeAsync("peer", timeout.Token));
            await Assert.ThrowsAnyAsync<Exception>(() => hostDelivery.SendFinalAndAwaitAckAsync(timeout.Token));
        }
        var state = await vault.GetPairingRecoveryStatusAsync(timeout.Token);
        Assert.Equal(scenario == "revoke" ? PairingRecoveryPhase.EvidenceProblem : PairingRecoveryPhase.CommittedAwaitingPeer, state.Phase);
        Assert.False(await vault.CleanupAppliedPairingAsync(state, timeout.Token));
        Assert.True(File.Exists(Path.Combine(vaultPath, "pending-prepare.bin")));
        Assert.True(File.Exists(Path.Combine(vaultPath, "pending-grant.bin")));
        Assert.True(vault.IsAuthorized("oldpeer", oldToken));
        await store.DisposeAsync();
        await using var reopened = await PeerInvitationStore.OpenAsync(peerPath, protector, timeout.Token);
        Assert.Throws<PairingRecoveryRequiredException>(() => reopened.Get());
        Assert.NotEqual(PairingRecoveryPhase.None, (await reopened.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
