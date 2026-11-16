using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PeerInvitationReadRaceTests
{
    [Fact]
    public async Task ReadCannotReturnOldInvitationAfterCompletePromotionAndFinalization()
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
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var accepted = await listener.AcceptSocketAsync(timeout.Token); await connect;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, hostCert, identity, offer, timeout.Token);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(client, peerCert, identity, offer, timeout.Token);
        await Task.WhenAll(hostTask, peerTask);
        await using var host = await hostTask; await using var peer = await peerTask;
        await Task.WhenAll(host.ConfirmLocalAsync(timeout.Token), peer.ReceiveRemoteConfirmationAsync(timeout.Token));
        await Task.WhenAll(peer.ConfirmLocalAsync(timeout.Token), host.ReceiveRemoteConfirmationAsync(timeout.Token));
        using var hostDelivery = host.BeginDelivery(); using var peerDelivery = peer.BeginDelivery();
        var protector = new TestProtector();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(folder.Path, "vault"), protector);
        await using var store = await PeerInvitationStore.OpenAsync(Path.Combine(folder.Path, "peer"), protector);
        var old = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, HostName = "oldhost", PeerName = "peer",
            CertificateSha256 = new string('A', 64), Token = Convert.ToBase64String(new byte[32]) };
        await store.SaveConfirmedAsync(old, old.CertificateSha256, timeout.Token);
        Assert.Equal("oldhost", store.Get()!.HostName);
        await hostDelivery.StageHostGrantAsync(vault, timeout.Token);
        await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);
        await Task.WhenAll(hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token), peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token));
        await hostDelivery.CommitVerifiedReadyAsync(timeout.Token);
        await Task.WhenAll(hostDelivery.SendCommittedAsync(timeout.Token), peerDelivery.ReceiveCommittedAsync(store, timeout.Token));
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var paused = 0;
        store.InvitationReadCheckpointForTesting = () =>
        {
            if (Interlocked.Exchange(ref paused, 1) != 0) return;
            captured.TrySetResult(); release.Wait(timeout.Token);
        };
        var staleRead = Task.Run(store.Get, timeout.Token);
        try
        {
            await captured.Task.WaitAsync(timeout.Token);
            await Task.WhenAll(hostDelivery.ReceiveAppliedAsync(timeout.Token), peerDelivery.PromoteAndSendAppliedAsync(store, timeout.Token));
            await Task.WhenAll(hostDelivery.SendFinalAndAwaitAckAsync(timeout.Token), peerDelivery.ReceiveFinalAndFinalizeAsync(store, timeout.Token));
        }
        finally { store.InvitationReadCheckpointForTesting = null; release.Set(); }
        var returned = await staleRead.WaitAsync(timeout.Token);
        Assert.Equal("host", returned!.HostName);
        Assert.Equal("host", store.Get()!.HostName);
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
