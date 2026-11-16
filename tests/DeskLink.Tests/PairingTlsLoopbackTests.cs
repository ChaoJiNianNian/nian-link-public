using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingTlsLoopbackTests
{
    private static async Task<X509Certificate2> SyntheticCertificateAsync(string path)
    {
        await PrivateFiles.EnsureCertificateAsync(path, CancellationToken.None);
        return PrivateFiles.LoadTlsCertificate(path);
    }

    [Fact]
    public async Task MutualTlsProvesBothCertificatesButNeverSuppliesUserConsent()
    {
        using var folder = new TestFolder();
        using var host = await SyntheticCertificateAsync(Path.Combine(folder.Path, "host.pem"));
        using var peer = await SyntheticCertificateAsync(Path.Combine(folder.Path, "peer.pem"));
        var hostHash = DeskLinkFingerprint.OfCertificate(host); var peerHash = DeskLinkFingerprint.OfCertificate(peer);
        var evidence = await PairingTlsLoopbackProbe.ConnectAsync(host, peer, hostHash, peerHash);
        Assert.Equal(hostHash, evidence.HostFingerprint); Assert.Equal(peerHash, evidence.PeerFingerprint);
        var anotherConnection = await PairingTlsLoopbackProbe.ConnectAsync(host, peer, hostHash, peerHash);
        var now = DateTimeOffset.UtcNow;
        var session = new PairingSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", "peer", evidence.HostFingerprint, evidence.PeerFingerprint, now.AddMinutes(1));
        var otherSession = session with { SessionId = Guid.NewGuid(), OfferNonce = Guid.NewGuid(), HostFingerprint = anotherConnection.HostFingerprint, PeerFingerprint = anotherConnection.PeerFingerprint };
        var consent = new PairingConsentState(session, () => now);
        Assert.Throws<InvalidOperationException>(() => consent.BeginPrepare(session));
        Assert.Throws<InvalidOperationException>(() => consent.ConfirmRemote(otherSession));
        Assert.Throws<InvalidOperationException>(() => consent.ConfirmRemote(session with { OfferNonce = Guid.NewGuid() }));
        consent.ConfirmLocal(session);
        Assert.Throws<InvalidOperationException>(() => consent.BeginPrepare(session));
        consent.ConfirmRemote(session); consent.BeginPrepare(session);
        Assert.Equal(PairingConsentPhase.Preparing, consent.Phase);
    }

    [Fact]
    public async Task UnexpectedHostOrPeerCertificateIsRejected()
    {
        using var folder = new TestFolder();
        using var host = await SyntheticCertificateAsync(Path.Combine(folder.Path, "host.pem"));
        using var peer = await SyntheticCertificateAsync(Path.Combine(folder.Path, "peer.pem"));
        using var impostor = await SyntheticCertificateAsync(Path.Combine(folder.Path, "impostor.pem"));
        var hostHash = DeskLinkFingerprint.OfCertificate(host); var peerHash = DeskLinkFingerprint.OfCertificate(peer);
        var impostorHash = DeskLinkFingerprint.OfCertificate(impostor);
        var wrongHost = await Assert.ThrowsAsync<PairingTlsLoopbackProbe.Failure>(() => PairingTlsLoopbackProbe.ConnectAsync(host, peer, impostorHash, peerHash));
        Assert.True(wrongHost.PeerRejected);
        var wrongPeer = await Assert.ThrowsAsync<PairingTlsLoopbackProbe.Failure>(() => PairingTlsLoopbackProbe.ConnectAsync(host, peer, hostHash, impostorHash));
        Assert.True(wrongPeer.HostRejected);
    }

    [Fact]
    public async Task CertificateWithoutPrivateKeyCannotProvePeerIdentity()
    {
        using var folder = new TestFolder();
        using var host = await SyntheticCertificateAsync(Path.Combine(folder.Path, "host.pem"));
        using var peer = await SyntheticCertificateAsync(Path.Combine(folder.Path, "peer.pem"));
        using var publicOnly = X509CertificateLoader.LoadCertificate(peer.RawData);
        Assert.False(publicOnly.HasPrivateKey);
        var failure = await Assert.ThrowsAsync<PairingTlsLoopbackProbe.Failure>(() => PairingTlsLoopbackProbe.ConnectAsync(host, publicOnly,
            DeskLinkFingerprint.OfCertificate(host), DeskLinkFingerprint.OfCertificate(peer)));
        Assert.Contains(failure.Causes, e => e is AuthenticationException or System.Security.Cryptography.CryptographicException);
    }
}
