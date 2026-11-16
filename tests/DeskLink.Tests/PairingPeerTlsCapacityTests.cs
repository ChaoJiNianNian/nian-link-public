using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingPeerTlsCapacityTests
{
    [Theory]
    [InlineData("completion")]
    [InlineData("abort")]
    public async Task FullPeerHistoryIsHandledBeforeActualPrepareWithoutChangingOldInvitation(string kind)
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
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector, timeout.Token);
        await using var store = await PeerInvitationStore.OpenAsync(peerPath, protector, timeout.Token);
        var old = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, HostName = "oldhost", PeerName = "peer",
            CertificateSha256 = new string('A', 64), Token = Convert.ToBase64String(new byte[32]) };
        await store.SaveConfirmedAsync(old, old.CertificateSha256, timeout.Token);
        for (var i = 0; i < 64; i++)
        {
            var historical = new PairingSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", "peer",
                new string('B', 64), new string('C', 64), now.AddMinutes(1));
            var binding = new PairingHistoricalBinding(historical, Guid.NewGuid(), 1, 24822, "127.0.0.1", 24821, 24800, 24822, new string('D', 64));
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var bytes = kind == "completion"
                ? JsonSerializer.SerializeToUtf8Bytes(new { version = 1, identity = historical, stageId = binding.StageId, slot = 1,
                    dataPortBase = 24822, host = "127.0.0.1", controlPort = 24821, keyboardPort = 24800, dataPort = 24822,
                    invitationDigest = new string('D', 64), pendingHash = new string('E', 64), readyHash = new string('F', 64),
                    promotionHash = new string('1', 64), baseHash = (string?)null, reservationHash = (string?)null }, options)
                : JsonSerializer.SerializeToUtf8Bytes(new { version = 1, binding }, options);
            await File.WriteAllBytesAsync(Path.Combine(peerPath, $"peer-{(kind == "completion" ? "completed" : "aborted")}-{historical.TransactionId:N}.bin"), bytes, timeout.Token);
        }
        var oldToken = await vault.AddConfirmedAsync("oldpeer", new string('D', 64), timeout.Token);
        await hostDelivery.StageHostGrantAsync(vault, timeout.Token);
        await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);
        var sent = hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token);
        var received = peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token);
        if (kind == "completion")
            await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(sent, received).WaitAsync(TimeSpan.FromSeconds(8)));
        else
            await Task.WhenAll(sent, received).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(kind == "abort", File.Exists(Path.Combine(peerPath, "peer-pending-invitation.bin")));
        Assert.Equal(old, store.Get());
        Assert.True(vault.IsAuthorized("oldpeer", oldToken));
        Assert.Single(vault.GetTrustedDevices());
        Assert.Equal(PairingRecoveryPhase.AwaitingPeer, (await vault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
