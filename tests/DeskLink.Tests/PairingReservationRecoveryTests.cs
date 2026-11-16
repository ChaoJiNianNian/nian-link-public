using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingReservationRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostReservationAcknowledgementIsSafelyTerminatedAfterRestart(bool full)
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
        if (full) await FillAbortHistoryAsync(peerPath, timeout.Token);
        await hostDelivery.StageHostGrantAsync(vault, timeout.Token);
        var frozen = await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);
        var originalSend = hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token);
        var marker = Path.Combine(vaultPath, "pending-prepare.bin");
        while (!File.Exists(marker)) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(10, timeout.Token); }
        var binding = new PairingHistoricalBinding(oldIdentity, frozen.StageId, frozen.Slot, frozen.DataPortBase,
            frozen.Host, frozen.ControlPort, frozen.KeyboardPort, frozen.DataPort, frozen.InvitationDigest);
        Assert.True(await store.ReserveCapacityAsync(peerDelivery, binding, timeout.Token));
        Assert.True(File.Exists(Path.Combine(peerPath, "peer-reservation.bin")));
        peerSocket.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => originalSend.WaitAsync(TimeSpan.FromSeconds(5)));
        await store.DisposeAsync(); await vault.DisposeAsync();
        await using var reopenedStore = await PeerInvitationStore.OpenAsync(peerPath, protector, timeout.Token);
        await using var reopenedVault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector, timeout.Token);
        Assert.Equal(PairingRecoveryPhase.AwaitingPeer, (await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);

        var (freshOffer, freshIdentity) = Identity(hostCert, peerCert);
        using var freshListener = new TcpListener(IPAddress.Loopback, 0); freshListener.Start(1);
        using var freshPeerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var freshConnecting = freshPeerSocket.ConnectAsync((IPEndPoint)freshListener.LocalEndpoint, timeout.Token);
        using var freshHostSocket = await freshListener.AcceptSocketAsync(timeout.Token); await freshConnecting;
        var freshHostTask = PairingAuthenticatedChannel.StartHostAsync(freshHostSocket, hostCert, freshIdentity, freshOffer, timeout.Token);
        var freshPeerTask = PairingAuthenticatedChannel.StartPeerAsync(freshPeerSocket, peerCert, freshIdentity, freshOffer, timeout.Token);
        await Task.WhenAll(freshHostTask, freshPeerTask);
        await using var freshHost = await freshHostTask; await using var freshPeer = await freshPeerTask;
        await ConfirmBothAsync(freshHost, freshPeer, timeout.Token);
        using var freshHostDelivery = freshHost.BeginDelivery(); using var freshPeerDelivery = freshPeer.BeginDelivery();
        var receiveOperation = freshPeerDelivery.ReceivePairingOperationAsync(timeout.Token);
        await freshHostDelivery.SendPairingOperationAsync(true, timeout.Token); Assert.True(await receiveOperation);
        var results = await Task.WhenAll(freshHostDelivery.ResumeHistoricalCommittedAsync(reopenedVault, timeout.Token),
            freshPeerDelivery.AnswerHistoricalCommittedAsync(reopenedStore, timeout.Token)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, value => Assert.False(value));
        Assert.Equal(PairingRecoveryPhase.None, (await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(vaultPath, "pending-grant.bin")));
        Assert.True(reopenedVault.IsAuthorized("oldpeer", oldToken));
        Assert.Single(reopenedVault.GetTrustedDevices());
        Assert.False(File.Exists(Path.Combine(peerPath, "peer-reservation.bin")));
        if (!full)
        {
            Assert.True(File.Exists(Path.Combine(peerPath, $"peer-aborted-{oldIdentity.TransactionId:N}.bin")));
        }
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
    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
