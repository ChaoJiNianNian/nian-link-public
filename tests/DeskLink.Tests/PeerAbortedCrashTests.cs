using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PeerAbortedCrashTests
{
    [Fact]
    public async Task TombstoneBeforePendingDeletionSurvivesRestartAndBlocksLateReady()
    {
        using var folder = new TestFolder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), timeout.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), timeout.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var vaultPath = Path.Combine(folder.Path, "vault"); var peerPath = Path.Combine(folder.Path, "peer");
        var protector = new TestProtector();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector);
        var store = await PeerInvitationStore.OpenAsync(peerPath, protector);
        var oldToken = await vault.AddConfirmedAsync("oldpeer", new string('A', 64), timeout.Token);
        await using var old = await ConnectAsync(hostCert, peerCert, timeout.Token);
        using var oldHost = old.Host.BeginDelivery(); using var oldPeer = old.Peer.BeginDelivery();
        await oldHost.StageHostGrantAsync(vault, timeout.Token);
        await oldHost.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);
        var send = oldHost.SendPrepareAndAwaitReadyAsync(timeout.Token);
        var receipt = await oldPeer.ReceivePrepareAndStageAsync(store, timeout.Token); await send;
        File.Delete(Path.Combine(peerPath, "peer-ready-evidence.bin"));
        store.AbortCheckpointForTesting = step => { if (step == "tombstone") throw new IOException("simulated crash"); };
        await using (var fresh = await ConnectAsync(hostCert, peerCert, timeout.Token))
        {
            using var host = fresh.Host.BeginDelivery(); using var peer = fresh.Peer.BeginDelivery();
            var hostWork = host.ResumeHistoricalCommittedAsync(vault, timeout.Token);
            var peerWork = peer.AnswerHistoricalCommittedAsync(store, timeout.Token);
            await Assert.ThrowsAnyAsync<Exception>(() => peerWork);
            await Assert.ThrowsAnyAsync<Exception>(() => hostWork);
        }
        Assert.True(File.Exists(Path.Combine(peerPath, "peer-pending-invitation.bin")));
        await Assert.ThrowsAnyAsync<Exception>(() => store.RecordReadyEvidenceAsync(oldPeer, receipt, timeout.Token));
        Assert.False(File.Exists(Path.Combine(peerPath, "peer-ready-evidence.bin")));
        await store.DisposeAsync();
        await using var reopened = await PeerInvitationStore.OpenAsync(peerPath, protector, timeout.Token);
        await using (var fresh = await ConnectAsync(hostCert, peerCert, timeout.Token))
        {
            using var host = fresh.Host.BeginDelivery(); using var peer = fresh.Peer.BeginDelivery();
            var result = await Task.WhenAll(host.ResumeHistoricalCommittedAsync(vault, timeout.Token), peer.AnswerHistoricalCommittedAsync(reopened, timeout.Token));
            Assert.All(result, value => Assert.False(value));
        }
        Assert.False(File.Exists(Path.Combine(peerPath, "peer-pending-invitation.bin")));
        Assert.Equal(PairingRecoveryPhase.None, (await vault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        Assert.True(vault.IsAuthorized("oldpeer", oldToken));
    }

    private static async Task<Connection> ConnectAsync(X509Certificate2 hostCert, X509Certificate2 peerCert, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peerCert),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, DeskLinkFingerprint.OfCertificate(hostCert), offer.KeyboardFingerprint, now.AddMinutes(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, ct);
        var accepted = await listener.AcceptSocketAsync(ct); await connect;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, hostCert, identity, offer, ct);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(client, peerCert, identity, offer, ct);
        await Task.WhenAll(hostTask, peerTask);
        var host = await hostTask; var peer = await peerTask;
        await Task.WhenAll(host.ConfirmLocalAsync(ct), peer.ReceiveRemoteConfirmationAsync(ct));
        await Task.WhenAll(peer.ConfirmLocalAsync(ct), host.ReceiveRemoteConfirmationAsync(ct));
        return new(host, peer, accepted, client);
    }

    private sealed record Connection(PairingAuthenticatedChannel Host, PairingAuthenticatedChannel Peer, Socket HostSocket, Socket PeerSocket) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); await Peer.DisposeAsync(); HostSocket.Dispose(); PeerSocket.Dispose(); }
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
