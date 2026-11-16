using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingTlsPrepareTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConfirmedRealTlsStagesPendingOrFailsWithoutChangingActiveState(bool rejectPeerProtection, bool markerConflict)
    {
        using var folder = new TestFolder();
        using var hostCertificate = await CertificateAsync(Path.Combine(folder.Path, "host.pem"));
        using var peerCertificate = await CertificateAsync(Path.Combine(folder.Path, "peer.pem"));
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows",
            KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peerCertificate),
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
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(folder.Path, "vault"), new TestProtector());
        var peerProtector = new TestProtector();
        await using var store = await PeerInvitationStore.OpenAsync(Path.Combine(folder.Path, "peer"), peerProtector);
        var old = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24921, KeyboardPort = 24900, DataPort = 24922,
            HostName = "oldhost", PeerName = "peer", CertificateSha256 = new string('A', 64),
            Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        await store.SaveConfirmedAsync(old, old.CertificateSha256);
        peerProtector.RejectWrites = rejectPeerProtection;

        var grant = await hostDelivery.StageHostGrantAsync(vault, timeout.Token);
        var frozen = await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);
        Assert.Throws<InvalidOperationException>(() => hostDelivery.ClaimVerifiedReady(timeout.Token));
        Assert.Throws<InvalidOperationException>(() => peerDelivery.ClaimVerifiedReady(timeout.Token));
        var markerPath = Path.Combine(folder.Path, "vault", "pending-prepare.bin");
        if (markerConflict) await File.WriteAllTextAsync(markerPath, "existing synthetic evidence", timeout.Token);
        var receive = peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token);
        var send = hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token);
        if (markerConflict)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => send.WaitAsync(TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAnyAsync<Exception>(() => receive.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("existing synthetic evidence", await File.ReadAllTextAsync(markerPath, timeout.Token));
            Assert.Equal(old, store.Get());
            Assert.Empty(vault.GetTrustedDevices());
            Assert.Null(await store.GetPendingMetadataAsync(timeout.Token));
            return;
        }
        if (rejectPeerProtection)
        {
            await Assert.ThrowsAsync<IOException>(() => receive.WaitAsync(TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAnyAsync<Exception>(() => send.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(old, store.Get());
            Assert.Empty(vault.GetTrustedDevices());
            Assert.Null(await store.GetPendingMetadataAsync(timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.AbortPendingGrantAsync(grant.StageId, identity, timeout.Token));
            return;
        }
        var results = await Task.WhenAll(send, receive).WaitAsync(TimeSpan.FromSeconds(10));
        var sent = results[0]; var received = results[1];

        Assert.Equal(grant.StageId, sent.StageId);
        Assert.Equal(frozen.InvitationDigest, sent.InvitationDigest);
        Assert.Equal(sent, received);
        Assert.Equal(old, store.Get());
        Assert.Empty(vault.GetTrustedDevices());
        var pending = await store.GetPendingMetadataAsync(timeout.Token);
        Assert.NotNull(pending);
        Assert.Equal(identity.TransactionId, pending.TransactionId);
        Assert.Equal(frozen.InvitationDigest, pending.InvitationDigest);
        // A READY may be in flight. Neither manual replacement nor ordinary cancellation may
        // invalidate the reviewed active-invitation baseline or erase its protected evidence.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveConfirmedAsync(old with { Port = old.Port + 1 }, old.CertificateSha256, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AbortPendingAsync(identity, frozen.InvitationDigest, timeout.Token));
        Assert.Equal(old, store.Get());
        Assert.Equal(sent, hostDelivery.ClaimVerifiedReady(timeout.Token));
        Assert.Throws<InvalidOperationException>(() => hostDelivery.ClaimVerifiedReady(timeout.Token));
        // A completed PREPARE may have reached the peer; never discard host evidence as if unsent.
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.AbortPendingGrantAsync(grant.StageId, identity, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token));
        await store.DisposeAsync();
        await using var reopenedPeer = await PeerInvitationStore.OpenAsync(Path.Combine(folder.Path, "peer"), peerProtector);
        Assert.Equal(old, reopenedPeer.Get());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopenedPeer.AbortPendingAsync(identity, frozen.InvitationDigest, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopenedPeer.SaveConfirmedAsync(old with { Port = old.Port + 1 }, old.CertificateSha256, timeout.Token));
        await vault.DisposeAsync();
        await using var reopened = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(folder.Path, "vault"), new TestProtector());
        Assert.Empty(reopened.GetTrustedDevices());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AbortPendingGrantAsync(grant.StageId, identity, timeout.Token));
    }

    [Fact]
    public async Task BeforeReadyMatchingAbortReleasesPendingAndPreservesManualPairing()
    {
        using var folder = new TestFolder();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var now = DateTimeOffset.UtcNow;
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", "peer",
            new string('A', 64), new string('B', 64), now.AddMinutes(1));
        var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, KeyboardPort = 24800,
            DataPort = 24822, HostName = "host", PeerName = "peer", CertificateSha256 = identity.HostFingerprint,
            Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        await using var store = await PeerInvitationStore.OpenAsync(Path.Combine(folder.Path, "peer"), new TestProtector());
        var digest = PeerInvitationStore.ComputePendingDigest(invitation);
        await store.StagePendingAsync(identity, invitation, identity.PeerName, identity.HostFingerprint, digest, now, cancellation.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveConfirmedAsync(invitation, identity.HostFingerprint, cancellation.Token));
        Assert.False(await store.AbortPendingAsync(identity with { TransactionId = Guid.NewGuid() }, digest, cancellation.Token));
        Assert.True(await store.AbortPendingAsync(identity, digest, cancellation.Token));
        await store.SaveConfirmedAsync(invitation, identity.HostFingerprint, cancellation.Token);
        Assert.Equal(invitation, store.Get());
    }

    [Theory]
    [InlineData("peer-ready-evidence.bin")]
    [InlineData("peer-promotion.bin")]
    public async Task OrphanEvidenceBlocksNewStageAndPreservesExistingInvitation(string evidenceFile)
    {
        using var folder = new TestFolder();
        var directory = Path.Combine(folder.Path, "peer");
        var now = DateTimeOffset.UtcNow;
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", "peer",
            new string('A', 64), new string('B', 64), now.AddMinutes(1));
        var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, KeyboardPort = 24800,
            DataPort = 24822, HostName = "host", PeerName = "peer", CertificateSha256 = identity.HostFingerprint,
            Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var protector = new TestProtector();
        await using (var store = await PeerInvitationStore.OpenAsync(directory, protector))
            await store.SaveConfirmedAsync(invitation, identity.HostFingerprint);
        var evidence = Path.Combine(directory, evidenceFile);
        await File.WriteAllTextAsync(evidence, "protected test evidence");
        await using var reopened = await PeerInvitationStore.OpenAsync(directory, protector);
        var digest = PeerInvitationStore.ComputePendingDigest(invitation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.StagePendingAsync(identity, invitation,
            identity.PeerName, identity.HostFingerprint, digest, now));
        Assert.Equal("protected test evidence", await File.ReadAllTextAsync(evidence));
        if (evidenceFile == "peer-promotion.bin") Assert.Throws<PairingRecoveryRequiredException>(() => reopened.Get());
        else Assert.Equal(invitation, reopened.Get());
        Assert.Null(await reopened.GetPendingMetadataAsync());
    }

    private static async Task<X509Certificate2> CertificateAsync(string path)
    {
        await PrivateFiles.EnsureCertificateAsync(path, CancellationToken.None);
        return PrivateFiles.LoadTlsCertificate(path);
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public bool RejectWrites { get; set; }
        public byte[] Protect(byte[] plaintext) => RejectWrites ? throw new IOException("Synthetic protection failure.") : plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }
}
