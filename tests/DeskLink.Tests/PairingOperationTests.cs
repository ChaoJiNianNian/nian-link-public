using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingOperationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationIsNegotiatedOnceOnlyAfterBothConfirmations(bool recovery)
    {
        using var folder = new TestFolder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
        Assert.Throws<InvalidOperationException>(() => host.BeginDelivery());
        Assert.Throws<InvalidOperationException>(() => peer.BeginDelivery());
        await Task.WhenAll(host.ConfirmLocalAsync(timeout.Token), peer.ReceiveRemoteConfirmationAsync(timeout.Token));
        Assert.Throws<InvalidOperationException>(() => host.BeginDelivery());
        Assert.Throws<InvalidOperationException>(() => peer.BeginDelivery());
        await Task.WhenAll(peer.ConfirmLocalAsync(timeout.Token), host.ReceiveRemoteConfirmationAsync(timeout.Token));
        using var hostDelivery = host.BeginDelivery(); using var peerDelivery = peer.BeginDelivery();
        await Assert.ThrowsAsync<InvalidOperationException>(() => hostDelivery.ReceivePairingOperationAsync(timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peerDelivery.SendPairingOperationAsync(recovery, timeout.Token));
        var received = peerDelivery.ReceivePairingOperationAsync(timeout.Token);
        await hostDelivery.SendPairingOperationAsync(recovery, timeout.Token);
        Assert.Equal(recovery, await received.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hostDelivery.SendPairingOperationAsync(recovery, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peerDelivery.ReceivePairingOperationAsync(timeout.Token));
    }
}
