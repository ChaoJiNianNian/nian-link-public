using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingRefusalRestartTests
{
    [Fact]
    public async Task RefusalWrittenBeforePendingDeletionCanResumeAfterBothStoresReopen()
    {
        using var folder = new TestFolder(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), timeout.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), timeout.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var vaultPath = Path.Combine(folder.Path, "vault"); var peerPath = Path.Combine(folder.Path, "peer");
        var protector = new TestProtector();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector, timeout.Token);
        await using var store = await PeerInvitationStore.OpenAsync(peerPath, protector, timeout.Token);
        var oldToken = await vault.AddConfirmedAsync("oldpeer", new string('A', 64), timeout.Token);
        await using (var original = await OpenPairAsync(hostCert, peerCert, timeout.Token))
        {
            using var hostDelivery = original.Host.BeginDelivery(); using var peerDelivery = original.Peer.BeginDelivery();
            await hostDelivery.StageHostGrantAsync(vault, timeout.Token);
            await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);
            await Task.WhenAll(hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token), peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token));
            // Model the durable boundary just before peer records READY.
            File.Delete(Path.Combine(peerPath, "peer-ready-evidence.bin"));
        }
        await FillAbortHistoryAsync(peerPath, timeout.Token);
        store.AbortCheckpointForTesting = point => { if (point == "reservation-refused") throw new IOException("simulated interruption"); };
        await using (var firstRecovery = await OpenPairAsync(hostCert, peerCert, timeout.Token))
        {
            using var hostDelivery = firstRecovery.Host.BeginDelivery(); using var peerDelivery = firstRecovery.Peer.BeginDelivery();
            var host = hostDelivery.ResumeHistoricalCommittedAsync(vault, timeout.Token);
            var peer = peerDelivery.AnswerHistoricalCommittedAsync(store, timeout.Token);
            await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(host, peer).WaitAsync(TimeSpan.FromSeconds(8)));
        }
        store.AbortCheckpointForTesting = null;
        var pendingPath = Path.Combine(peerPath, "peer-pending-invitation.bin"); var reservationPath = Path.Combine(peerPath, "peer-reservation.bin");
        Assert.True(File.Exists(pendingPath)); Assert.True(File.Exists(reservationPath));
        using (var record = JsonDocument.Parse(await File.ReadAllBytesAsync(reservationPath, timeout.Token)))
            Assert.Equal("refused", record.RootElement.GetProperty("decision").GetString());
        await store.DisposeAsync(); await vault.DisposeAsync();
        await using var reopenedStore = await PeerInvitationStore.OpenAsync(peerPath, protector, timeout.Token);
        await using var reopenedVault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector, timeout.Token);
        Assert.Equal(PairingRecoveryPhase.AwaitingPeer, (await reopenedStore.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        Assert.Equal(PairingRecoveryPhase.AwaitingPeer, (await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        await using (var retry = await OpenPairAsync(hostCert, peerCert, timeout.Token))
        {
            using var hostDelivery = retry.Host.BeginDelivery(); using var peerDelivery = retry.Peer.BeginDelivery();
            var results = await Task.WhenAll(hostDelivery.ResumeHistoricalCommittedAsync(reopenedVault, timeout.Token),
                peerDelivery.AnswerHistoricalCommittedAsync(reopenedStore, timeout.Token)).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.All(results, value => Assert.False(value));
        }
        Assert.False(File.Exists(pendingPath)); Assert.False(File.Exists(reservationPath));
        Assert.Equal(PairingRecoveryPhase.None, (await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        Assert.True(reopenedVault.IsAuthorized("oldpeer", oldToken));
    }

    private static async Task<Pair> OpenPairAsync(X509Certificate2 hostCertificate, X509Certificate2 peerCertificate, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peerCertificate),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, DeskLinkFingerprint.OfCertificate(hostCertificate), offer.KeyboardFingerprint, now.AddMinutes(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            var connecting = socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, ct);
            var accepted = await listener.AcceptSocketAsync(ct); await connecting;
            var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, hostCertificate, identity, offer, ct);
            var peerTask = PairingAuthenticatedChannel.StartPeerAsync(socket, peerCertificate, identity, offer, ct);
            await Task.WhenAll(hostTask, peerTask);
            var host = await hostTask; var peer = await peerTask;
            await Task.WhenAll(host.ConfirmLocalAsync(ct), peer.ReceiveRemoteConfirmationAsync(ct));
            await Task.WhenAll(peer.ConfirmLocalAsync(ct), host.ReceiveRemoteConfirmationAsync(ct));
            return new(host, peer);
        }
        catch { socket.Dispose(); throw; }
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
    private sealed record Pair(PairingAuthenticatedChannel Host, PairingAuthenticatedChannel Peer) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); await Peer.DisposeAsync(); }
    }
    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
