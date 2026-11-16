using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingAbsentRecoveryTests
{
    [Theory]
    [InlineData("absent")]
    [InlineData("late_prepare")]
    [InlineData("partial_cleanup")]
    [InlineData("damaged_cleanup")]
    [InlineData("matching_active")]
    [InlineData("pending_without_ready")]
    [InlineData("changed_peer_identity")]
    [InlineData("damaged_tombstone")]
    [InlineData("full_history_no_reservation")]
    public async Task FreshConfirmationProvesAbsenceBeforeSafeTermination(string scenario)
    {
        using var folder = new TestFolder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), timeout.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), timeout.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var (oldOffer, oldIdentity) = Identity(hostCert, peerCert);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        using var peerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connecting = peerSocket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var hostSocket = await listener.AcceptSocketAsync(timeout.Token); await connecting;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(hostSocket, hostCert, oldIdentity, oldOffer, timeout.Token);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(peerSocket, peerCert, oldIdentity, oldOffer, timeout.Token);
        await Task.WhenAll(hostTask, peerTask);
        await using var host = await hostTask; await using var peer = await peerTask;
        await ConfirmBothAsync(host, peer, timeout.Token);
        using var hostDelivery = host.BeginDelivery(); using var peerDelivery = peer.BeginDelivery();
        var protector = new TestProtector();
        var vaultPath = Path.Combine(folder.Path, "vault"); var peerPath = Path.Combine(folder.Path, "peer");
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector);
        await using var store = await PeerInvitationStore.OpenAsync(peerPath, protector);
        var oldToken = await vault.AddConfirmedAsync("oldpeer", new string('A', 64), timeout.Token);
        await hostDelivery.StageHostGrantAsync(vault, timeout.Token);
        var frozen = await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);
        var originalSend = hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token);
        PairingPrepareReceipt? originalReceipt = null;
        byte[]? peerPendingBytes = null;
        if (scenario == "pending_without_ready")
        {
            // Reproduce the durable boundary after pending was written but before READY was recorded.
            await peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token);
            originalReceipt = await originalSend;
            peerPendingBytes = await File.ReadAllBytesAsync(Path.Combine(peerPath, "peer-pending-invitation.bin"), timeout.Token);
            File.Delete(Path.Combine(peerPath, "peer-ready-evidence.bin"));
            var reserved = await store.GetPairingRecoveryStatusAsync(timeout.Token);
            Assert.Equal(PairingRecoveryPhase.AwaitingPeer, reserved.Phase);
            Assert.False(await store.AbandonUnsentPairingAsync(reserved, timeout.Token));
        }
        var marker = Path.Combine(vaultPath, "pending-prepare.bin"); var pendingPath = Path.Combine(vaultPath, "pending-grant.bin");
        while (!File.Exists(marker)) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(10, timeout.Token); }
        var pendingBytes = await File.ReadAllBytesAsync(pendingPath, timeout.Token);
        if (scenario == "full_history_no_reservation") await FillAbortHistoryAsync(peerPath, timeout.Token);
        if (scenario == "matching_active")
        {
            using var json = JsonDocument.Parse(pendingBytes);
            var invitation = new MultiPeerInvitation { Host = frozen.Host, Port = frozen.ControlPort,
                KeyboardPort = frozen.KeyboardPort, DataPort = frozen.DataPort, HostName = oldIdentity.HostName,
                PeerName = oldIdentity.PeerName, CertificateSha256 = oldIdentity.HostFingerprint,
                Token = json.RootElement.GetProperty("token").GetString()! };
            await store.SaveConfirmedAsync(invitation, invitation.CertificateSha256, timeout.Token);
        }
        if (scenario == "damaged_tombstone")
            await File.WriteAllTextAsync(Path.Combine(peerPath, $"peer-aborted-{oldIdentity.TransactionId:N}.bin"), "damaged refusal", timeout.Token);
        Assert.Equal(PairingRecoveryPhase.AwaitingPeer, (await vault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);

        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "other-peer.pem"), timeout.Token);
        using var otherPeerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "other-peer.pem"));
        var freshPeerCert = scenario == "changed_peer_identity" ? otherPeerCert : peerCert;
        var (freshOffer, freshIdentity) = Identity(hostCert, freshPeerCert);
        using var freshListener = new TcpListener(IPAddress.Loopback, 0); freshListener.Start(1);
        using var freshPeerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var freshConnecting = freshPeerSocket.ConnectAsync((IPEndPoint)freshListener.LocalEndpoint, timeout.Token);
        using var freshHostSocket = await freshListener.AcceptSocketAsync(timeout.Token); await freshConnecting;
        var freshHostTask = PairingAuthenticatedChannel.StartHostAsync(freshHostSocket, hostCert, freshIdentity, freshOffer, timeout.Token);
        var freshPeerTask = PairingAuthenticatedChannel.StartPeerAsync(freshPeerSocket, freshPeerCert, freshIdentity, freshOffer, timeout.Token);
        await Task.WhenAll(freshHostTask, freshPeerTask);
        await using var freshHost = await freshHostTask; await using var freshPeer = await freshPeerTask;
        await ConfirmBothAsync(freshHost, freshPeer, timeout.Token);
        using var freshHostDelivery = freshHost.BeginDelivery(); using var freshPeerDelivery = freshPeer.BeginDelivery();
        var operation = freshPeerDelivery.ReceivePairingOperationAsync(timeout.Token);
        await freshHostDelivery.SendPairingOperationAsync(true, timeout.Token);
        Assert.True(await operation);
        var recoverHost = freshHostDelivery.ResumeHistoricalCommittedAsync(vault, timeout.Token);
        var recoverPeer = freshPeerDelivery.AnswerHistoricalCommittedAsync(store, timeout.Token);
        if (scenario is "matching_active" or "changed_peer_identity" or "damaged_tombstone")
        {
            await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(recoverHost, recoverPeer).WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.True(File.Exists(marker)); Assert.True(File.Exists(pendingPath));
        }
        else
        {
            var result = await Task.WhenAll(recoverHost, recoverPeer).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.All(result, value => Assert.False(value));
            Assert.False(File.Exists(marker)); Assert.False(File.Exists(pendingPath));
            if (scenario == "pending_without_ready")
            {
                Assert.False(File.Exists(Path.Combine(peerPath, "peer-pending-invitation.bin")));
                await File.WriteAllBytesAsync(Path.Combine(peerPath, "peer-pending-invitation.bin"), peerPendingBytes!, timeout.Token);
                await Assert.ThrowsAnyAsync<Exception>(() => store.RecordReadyEvidenceAsync(peerDelivery, originalReceipt!, timeout.Token));
                Assert.False(File.Exists(Path.Combine(peerPath, "peer-ready-evidence.bin")));
            }
            Assert.Equal(PairingRecoveryPhase.None, (await vault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
            Assert.Single(vault.GetTrustedDevices());
            Assert.True(vault.IsAuthorized("oldpeer", oldToken));
            if (scenario == "full_history_no_reservation")
            {
                await Assert.ThrowsAnyAsync<Exception>(() => peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token));
                Assert.False(File.Exists(Path.Combine(peerPath, "peer-pending-invitation.bin")));
                Assert.False(File.Exists(Path.Combine(peerPath, "peer-reservation.bin")));
                // The retired exact identities must free the single slot without
                // permitting this old reservation to be replayed.
                await using var subsequent = await OpenFreshPairAsync(hostCert, peerCert, timeout.Token);
                using var subsequentHost = subsequent.Host.BeginDelivery(); using var subsequentPeer = subsequent.Peer.BeginDelivery();
                await subsequentHost.StageHostGrantAsync(vault, timeout.Token);
                await subsequentHost.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);
                await Task.WhenAll(subsequentHost.SendPrepareAndAwaitReadyAsync(timeout.Token), subsequentPeer.ReceivePrepareAndStageAsync(store, timeout.Token));
                await subsequentHost.CommitVerifiedReadyAsync(timeout.Token);
                await Task.WhenAll(subsequentHost.SendCommittedAsync(timeout.Token), subsequentPeer.ReceiveCommittedAsync(store, timeout.Token));
                await Task.WhenAll(subsequentHost.ReceiveAppliedAsync(timeout.Token), subsequentPeer.PromoteAndSendAppliedAsync(store, timeout.Token));
                await Task.WhenAll(subsequentHost.SendFinalAndAwaitAckAsync(timeout.Token), subsequentPeer.ReceiveFinalAndFinalizeAsync(store, timeout.Token));
                await subsequentHost.CleanupFinishedDeliveryAsync(timeout.Token);
                Assert.Equal("peer", store.Get()!.PeerName);
            }
            if (scenario == "late_prepare")
                await Assert.ThrowsAnyAsync<Exception>(() => peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token));
            if (scenario is "partial_cleanup" or "damaged_cleanup")
            {
                // Simulate a restart after marker deletion but before deleting the exact pending record.
                await File.WriteAllBytesAsync(pendingPath, pendingBytes, timeout.Token);
                if (scenario == "damaged_cleanup") await File.WriteAllTextAsync(pendingPath, "damaged", timeout.Token);
                await vault.DisposeAsync();
                await using var reopened = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector, timeout.Token);
                var status = await reopened.GetPairingRecoveryStatusAsync(timeout.Token);
                if (scenario == "damaged_cleanup")
                {
                    Assert.Equal(PairingRecoveryPhase.EvidenceProblem, status.Phase);
                    Assert.False(await reopened.CleanupAbortedPairingAsync(status, timeout.Token));
                    Assert.True(File.Exists(pendingPath));
                }
                else
                {
                    Assert.Equal(PairingRecoveryPhase.AbortedCleanup, status.Phase);
                    Assert.True(await reopened.CleanupAbortedPairingAsync(status, timeout.Token));
                    Assert.False(File.Exists(pendingPath));
                    Assert.Equal(PairingRecoveryPhase.None, (await reopened.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
                    Assert.True(reopened.IsAuthorized("oldpeer", oldToken));
                }
            }
        }
        peerSocket.Dispose();
        if (scenario != "pending_without_ready")
            await Assert.ThrowsAnyAsync<Exception>(() => originalSend.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static (PeerIdentityOffer, PairingSessionIdentity) Identity(
        System.Security.Cryptography.X509Certificates.X509Certificate2 host,
        System.Security.Cryptography.X509Certificates.X509Certificate2 peer)
    {
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peer),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        return (offer, new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, DeskLinkFingerprint.OfCertificate(host), offer.KeyboardFingerprint, now.AddMinutes(1)));
    }

    private static async Task ConfirmBothAsync(PairingAuthenticatedChannel host, PairingAuthenticatedChannel peer, CancellationToken ct)
    {
        await Task.WhenAll(host.ConfirmLocalAsync(ct), peer.ReceiveRemoteConfirmationAsync(ct));
        await Task.WhenAll(peer.ConfirmLocalAsync(ct), host.ReceiveRemoteConfirmationAsync(ct));
    }

    private static async Task FillAbortHistoryAsync(string directory, CancellationToken ct)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        for (var i = 0; i < 64; i++)
        {
            var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", "peer",
                new string('B', 64), new string('C', 64), DateTimeOffset.UtcNow.AddMinutes(1));
            var binding = new PairingHistoricalBinding(identity, Guid.NewGuid(), 1, 24822, "127.0.0.1", 24821, 24800, 24822, new string('D', 64));
            await File.WriteAllBytesAsync(Path.Combine(directory, $"peer-aborted-{identity.TransactionId:N}.bin"),
                JsonSerializer.SerializeToUtf8Bytes(new { version = 1, binding }, options), ct);
        }
    }

    private static async Task<FreshPair> OpenFreshPairAsync(
        System.Security.Cryptography.X509Certificates.X509Certificate2 hostCertificate,
        System.Security.Cryptography.X509Certificates.X509Certificate2 peerCertificate, CancellationToken ct)
    {
        var (offer, identity) = Identity(hostCertificate, peerCertificate);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connecting = socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, ct);
        var accepted = await listener.AcceptSocketAsync(ct); await connecting;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, hostCertificate, identity, offer, ct);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(socket, peerCertificate, identity, offer, ct);
        await Task.WhenAll(hostTask, peerTask);
        var host = await hostTask; var peer = await peerTask;
        await ConfirmBothAsync(host, peer, ct);
        return new(host, peer);
    }

    private sealed record FreshPair(PairingAuthenticatedChannel Host, PairingAuthenticatedChannel Peer) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); await Peer.DisposeAsync(); }
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
