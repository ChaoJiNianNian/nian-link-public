using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingAuthenticatedChannelTests
{
    [Fact]
    public async Task RealMutualTlsRequiresBothExplicitConfirmations()
    {
        using var folder = new TestFolder();
        using var host = await CertificateAsync(Path.Combine(folder.Path, "host.pem"));
        using var peer = await CertificateAsync(Path.Combine(folder.Path, "peer.pem"));
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peer),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(1) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(), "host", offer.Name,
            DeskLinkFingerprint.OfCertificate(host), offer.KeyboardFingerprint, now.AddSeconds(30));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var hostLifetime = new CancellationTokenSource();
        var connecting = client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
        using var accepted = await listener.AcceptSocketAsync(timeout.Token);
        await connecting;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, host, identity, offer, hostLifetime.Token);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(client, peer, identity, offer, timeout.Token);
        await Task.WhenAll(hostTask, peerTask);
        await using var hostChannel = await hostTask;
        await using var peerChannel = await peerTask;
        Assert.Equal(identity.HostFingerprint, hostChannel.LocalFingerprint);
        Assert.Equal(identity.PeerFingerprint, hostChannel.RemoteFingerprint);
        Assert.Equal(PairingConsentPhase.AwaitingConfirmation, hostChannel.Phase);
        Assert.Throws<InvalidOperationException>(() => hostChannel.BeginDelivery());
        await Task.WhenAll(hostChannel.ConfirmLocalAsync(timeout.Token), peerChannel.ReceiveRemoteConfirmationAsync(timeout.Token));
        Assert.Equal(PairingConsentPhase.AwaitingConfirmation, hostChannel.Phase);
        Assert.Throws<InvalidOperationException>(() => hostChannel.BeginDelivery());
        await Task.WhenAll(peerChannel.ConfirmLocalAsync(timeout.Token), hostChannel.ReceiveRemoteConfirmationAsync(timeout.Token));
        Assert.Equal(PairingConsentPhase.ReadyToPrepare, hostChannel.Phase);
        Assert.Equal(PairingConsentPhase.ReadyToPrepare, peerChannel.Phase);
        using var hostDelivery = hostChannel.BeginDelivery();
        using var peerDelivery = peerChannel.BeginDelivery();
        Assert.Equal(identity, hostDelivery.Identity);
        Assert.Equal(offer, peerDelivery.Offer);
        Assert.Equal(IPAddress.Loopback, hostDelivery.LocalAddress);
        Assert.Equal(IPAddress.Loopback, peerDelivery.RemoteAddress);
        Assert.Throws<InvalidOperationException>(() => hostChannel.BeginDelivery());
        hostDelivery.EnsureCurrent();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(folder.Path, "trust"), new TestProtector());
        await using var otherVault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(folder.Path, "other-trust"), new TestProtector());
        await using var otherHost = new MultiPeerRuntime(otherVault, Path.Combine(folder.Path, "other-runtime"), "host",
            IPAddress.Loopback, 24821, 24822, _ => throw new InvalidOperationException("Keyboard is not needed in this test."));
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24821, 24822, 24800, identity.HostFingerprint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pairing.PrepareFilelessAsync(hostDelivery, otherHost));
        Assert.Null(await vault.GetPendingGrantMetadataAsync(DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peerDelivery.StageHostGrantAsync(vault));
        var pending = await hostDelivery.StageHostGrantAsync(vault);
        Assert.Equal(identity.TransactionId, pending.TransactionId);
        Assert.Empty(vault.GetTrustedDevices());
        await Assert.ThrowsAsync<InvalidOperationException>(() => hostDelivery.StageHostGrantAsync(vault));
        var syntheticAddress = IPAddress.Parse("192.168.20.10");
        var syntheticBinding = VerifiedLanBinding.FromSnapshots(syntheticAddress, true,
            [new LanInterfaceSnapshot("test", "test", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
                System.Net.NetworkInformation.OperationalStatus.Up, [syntheticAddress], "synthetic")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => hostDelivery.FreezeHostInvitationAsync(syntheticBinding, 24821, 24800, 24822));
        Assert.Empty(vault.GetTrustedDevices());
        var frozen = await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822);
        Assert.Equal("127.0.0.1", frozen.Host);
        Assert.Equal(pending.StageId, hostDelivery.GetFrozenHost().StageId);
        hostLifetime.Cancel();
        Assert.Equal(PairingConsentPhase.Aborted, hostChannel.Phase);
        Assert.Throws<InvalidOperationException>(() => hostDelivery.EnsureCurrent());
    }

    [Fact]
    public async Task CancellingDuringHandshakeClosesTheOwnedSocket()
    {
        using var folder = new TestFolder();
        using var host = await CertificateAsync(Path.Combine(folder.Path, "host.pem"));
        using var peer = await CertificateAsync(Path.Combine(folder.Path, "peer.pem"));
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peer),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(1) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(), "host", offer.Name,
            DeskLinkFingerprint.OfCertificate(host), offer.KeyboardFingerprint, now.AddSeconds(30));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var connecting = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var accepted = await listener.AcceptSocketAsync(timeout.Token); await connecting;
        using var lifetime = new CancellationTokenSource();
        var pending = PairingAuthenticatedChannel.StartHostAsync(accepted, host, identity, offer, lifetime.Token);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(accepted.SafeHandle.IsClosed);
    }

    [Fact]
    public async Task DifferentActualHostCertificateFailsBeforeConfirmation()
    {
        using var folder = new TestFolder();
        using var host = await CertificateAsync(Path.Combine(folder.Path, "host.pem"));
        using var peer = await CertificateAsync(Path.Combine(folder.Path, "peer.pem"));
        using var impostor = await CertificateAsync(Path.Combine(folder.Path, "impostor.pem"));
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peer),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(1) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(), "host", offer.Name,
            DeskLinkFingerprint.OfCertificate(host), offer.KeyboardFingerprint, now.AddSeconds(30));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var connecting = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var accepted = await listener.AcceptSocketAsync(timeout.Token); await connecting;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, host, identity, offer, timeout.Token);
        var wrongHost = identity with { HostFingerprint = DeskLinkFingerprint.OfCertificate(impostor) };
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(client, peer, wrongHost, offer, timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(hostTask, peerTask).WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.False(hostTask.IsCompletedSuccessfully && peerTask.IsCompletedSuccessfully);
    }

    private static async Task<X509Certificate2> CertificateAsync(string path)
    {
        await PrivateFiles.EnsureCertificateAsync(path, CancellationToken.None);
        return PrivateFiles.LoadTlsCertificate(path);
    }

    // Synthetic-only protector for isolated test data; production supplies platform protection.
    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }
}
