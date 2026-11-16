using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PeerFinalizationCrashTests
{
    [Theory]
    [InlineData("journal", false)]
    [InlineData("active", false)]
    [InlineData("receipt", false)]
    [InlineData("promotion", false)]
    [InlineData("ready", false)]
    [InlineData("reservation", false)]
    [InlineData("pending", false)]
    [InlineData("journal-deleted", false)]
    [InlineData("journal", true)]
    [InlineData("active", true)]
    [InlineData("receipt", true)]
    [InlineData("pending", true)]
    [InlineData("reservation", true)]
    [InlineData("active-prepublish", true)]
    [InlineData("promotion-predelete", true)]
    public async Task FinalizationResumesEachDurableBoundaryAndRejectsChangedEvidence(string checkpoint, bool damage)
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
        var store = await PeerInvitationStore.OpenAsync(peerPath, protector);
        var previous = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, HostName = "oldhost", PeerName = "peer",
            CertificateSha256 = new string('A', 64), Token = Convert.ToBase64String(new byte[32]) };
        await store.SaveConfirmedAsync(previous, previous.CertificateSha256, timeout.Token);
        var oldToken = await vault.AddConfirmedAsync("oldpeer", new string('B', 64), timeout.Token);
        await hostDelivery.StageHostGrantAsync(vault, timeout.Token);
        await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);
        await Task.WhenAll(hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token), peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token));
        await hostDelivery.CommitVerifiedReadyAsync(timeout.Token);
        await Task.WhenAll(hostDelivery.SendCommittedAsync(timeout.Token), peerDelivery.ReceiveCommittedAsync(store, timeout.Token));
        await Task.WhenAll(hostDelivery.ReceiveAppliedAsync(timeout.Token), peerDelivery.PromoteAndSendAppliedAsync(store, timeout.Token));
        store.FinalizationCheckpointForTesting = stage =>
        {
            if (stage != checkpoint) return;
            if (stage == "active-prepublish") File.WriteAllText(Path.Combine(peerPath, "peer-invitation.bin"), (previous with { Port = previous.Port + 1 }).ToJson());
            else if (stage == "promotion-predelete") File.WriteAllText(Path.Combine(peerPath, "peer-promotion.bin"), "foreign evidence");
            else throw new IOException("synthetic interruption");
        };
        var send = hostDelivery.SendFinalAndAwaitAckAsync(timeout.Token);
        var receive = peerDelivery.ReceiveFinalAndFinalizeAsync(store, timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => receive);
        await Assert.ThrowsAnyAsync<Exception>(() => send);
        Assert.Equal(PairingRecoveryPhase.CommittedAwaitingPeer, (await vault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        Assert.True(vault.IsAuthorized("oldpeer", oldToken));
        await store.DisposeAsync();
        var journal = Path.Combine(peerPath, "peer-finalizing.bin");
        if (damage && checkpoint is not ("active-prepublish" or "promotion-predelete"))
        {
            var target = checkpoint switch
            {
                "journal" => Path.Combine(peerPath, "peer-pending-invitation.bin"),
                "active" => Path.Combine(peerPath, "peer-invitation.bin"),
                _ => Path.Combine(peerPath, "peer-completed-" + identity.TransactionId.ToString("N") + ".bin")
            };
            await File.WriteAllTextAsync(target, checkpoint == "active" ? (previous with { Port = previous.Port + 1 }).ToJson() : "synthetic damage", timeout.Token);
        }
        await using var reopened = await PeerInvitationStore.OpenAsync(peerPath, protector, timeout.Token);
        if (damage)
        {
            Assert.Throws<PairingRecoveryRequiredException>(() => reopened.Get());
            Assert.Equal(PairingRecoveryPhase.EvidenceProblem, (await reopened.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
            Assert.True(File.Exists(journal));
            if (checkpoint == "promotion-predelete") Assert.Equal("foreign evidence", await File.ReadAllTextAsync(Path.Combine(peerPath, "peer-promotion.bin"), timeout.Token));
            return;
        }
        Assert.Equal("host", reopened.Get()!.HostName);
        Assert.Equal(PairingRecoveryPhase.CompletedAwaitingHost, (await reopened.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        Assert.False(File.Exists(journal));
        Assert.False(File.Exists(Path.Combine(peerPath, "peer-promotion.bin")));
        Assert.False(File.Exists(Path.Combine(peerPath, "peer-ready-evidence.bin")));
        Assert.False(File.Exists(Path.Combine(peerPath, "peer-pending-invitation.bin")));
        Assert.True(File.Exists(Path.Combine(peerPath, "peer-completed-" + identity.TransactionId.ToString("N") + ".bin")));
        Assert.True(vault.IsAuthorized("oldpeer", oldToken));
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
