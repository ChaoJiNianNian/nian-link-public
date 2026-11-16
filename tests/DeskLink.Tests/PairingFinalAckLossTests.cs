using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingFinalAckLossTests
{
    [Theory]
    [InlineData("resume")]
    [InlineData("revoked")]
    [InlineData("damaged")]
    [InlineData("changed_peer_identity")]
    [InlineData("replaced_invitation")]
    [InlineData("manual_replace_blocked")]
    public async Task LostFinalAckRequiresFreshAuthenticatedRecovery(string scenario)
    {
        using var folder = new TestFolder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), timeout.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), timeout.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peerCert),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, DeskLinkFingerprint.OfCertificate(hostCert), offer.KeyboardFingerprint, now.AddMinutes(1));
        using var hostListener = new TcpListener(IPAddress.Loopback, 0); hostListener.Start(1);
        using var proxyListener = new TcpListener(IPAddress.Loopback, 0); proxyListener.Start(1);
        using var peerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var peerConnecting = peerSocket.ConnectAsync((IPEndPoint)proxyListener.LocalEndpoint, timeout.Token);
        using var proxyPeer = await proxyListener.AcceptSocketAsync(timeout.Token); await peerConnecting;
        using var proxyHost = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var proxyConnecting = proxyHost.ConnectAsync((IPEndPoint)hostListener.LocalEndpoint, timeout.Token);
        using var hostSocket = await hostListener.AcceptSocketAsync(timeout.Token); await proxyConnecting;
        using var proxyPeerStream = new NetworkStream(proxyPeer);
        using var proxyHostStream = new NetworkStream(proxyHost);
        using var relayStop = new CancellationTokenSource();
        var dropPeerWrites = 0;
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var up = ForwardAsync(proxyPeerStream, proxyHostStream, () => Volatile.Read(ref dropPeerWrites) != 0, dropped, relayStop.Token);
        var down = ForwardAsync(proxyHostStream, proxyPeerStream, () => false, dropped, relayStop.Token);
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(hostSocket, hostCert, identity, offer, timeout.Token);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(peerSocket, peerCert, identity, offer, timeout.Token);
        await Task.WhenAll(hostTask, peerTask);
        await using var host = await hostTask; await using var peer = await peerTask;
        await Task.WhenAll(host.ConfirmLocalAsync(timeout.Token), peer.ReceiveRemoteConfirmationAsync(timeout.Token));
        await Task.WhenAll(peer.ConfirmLocalAsync(timeout.Token), host.ReceiveRemoteConfirmationAsync(timeout.Token));
        using var hostDelivery = host.BeginDelivery(); using var peerDelivery = peer.BeginDelivery();
        var protector = new TestProtector();
        var vaultPath = Path.Combine(folder.Path, "vault"); var peerPath = Path.Combine(folder.Path, "peer");
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector);
        await using var store = await PeerInvitationStore.OpenAsync(peerPath, protector);
        var oldToken = await vault.AddConfirmedAsync("oldpeer", new string('A', 64), timeout.Token);
        await hostDelivery.StageHostGrantAsync(vault, timeout.Token);
        await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);
        await Task.WhenAll(hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token), peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token));
        await hostDelivery.CommitVerifiedReadyAsync(timeout.Token);
        await Task.WhenAll(hostDelivery.SendCommittedAsync(timeout.Token), peerDelivery.ReceiveCommittedAsync(store, timeout.Token));
        await Task.WhenAll(hostDelivery.ReceiveAppliedAsync(timeout.Token), peerDelivery.PromoteAndSendAppliedAsync(store, timeout.Token));

        // The proxy drops peer-to-host TLS records only after APPLIED has arrived.
        // Thus FINAL reaches the peer, but its encrypted FINAL_ACK cannot reach host.
        Volatile.Write(ref dropPeerWrites, 1);
        var waitingAck = hostDelivery.SendFinalAndAwaitAckAsync(timeout.Token);
        var peerFinishing = peerDelivery.ReceiveFinalAndFinalizeAsync(store, timeout.Token);
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        proxyHost.Dispose(); proxyPeer.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => waitingAck.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<Exception>(() => peerFinishing.WaitAsync(TimeSpan.FromSeconds(5)));

        var recovery = await vault.GetPairingRecoveryStatusAsync(timeout.Token);
        Assert.Equal(PairingRecoveryPhase.CommittedAwaitingPeer, recovery.Phase);
        Assert.False(await vault.CleanupAppliedPairingAsync(recovery, timeout.Token));
        Assert.True(File.Exists(Path.Combine(vaultPath, "pending-prepare.bin")));
        Assert.True(File.Exists(Path.Combine(vaultPath, "pending-grant.bin")));
        Assert.True(vault.IsAuthorized("oldpeer", oldToken));
        await store.DisposeAsync();
        await using var reopened = await PeerInvitationStore.OpenAsync(peerPath, protector, timeout.Token);
        Assert.Equal("peer", reopened.Get()!.PeerName);
        var completedFile = Path.Combine(peerPath, $"peer-completed-{identity.TransactionId:N}.bin");
        Assert.True(File.Exists(completedFile));
        relayStop.Cancel(); proxyPeer.Dispose();
        await Task.WhenAll(up, down);

        await vault.DisposeAsync();
        await using var reopenedVault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector, timeout.Token);
        if (scenario == "revoked") Assert.True(await reopenedVault.RevokeAsync("peer", timeout.Token));
        if (scenario == "damaged") await File.WriteAllTextAsync(completedFile, "damaged completion evidence", timeout.Token);
        MultiPeerInvitation? replacement = null;
        if (scenario is "replaced_invitation" or "manual_replace_blocked")
        {
            replacement = reopened.Get()! with { Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            if (scenario == "manual_replace_blocked")
                await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SaveConfirmedAsync(replacement, replacement.CertificateSha256, timeout.Token));
            else
                await File.WriteAllTextAsync(Path.Combine(peerPath, "peer-invitation.bin"), replacement.ToJson(), timeout.Token);
        }
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "other-peer.pem"), timeout.Token);
        using var otherPeerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "other-peer.pem"));
        var freshPeerCert = scenario == "changed_peer_identity" ? otherPeerCert : peerCert;
        var freshNow = DateTimeOffset.UtcNow;
        var freshOffer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(freshPeerCert),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = freshNow.AddMinutes(2) };
        var freshIdentity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(freshOffer.Nonce, "N"), Guid.NewGuid(),
            "host", freshOffer.Name, DeskLinkFingerprint.OfCertificate(hostCert), freshOffer.KeyboardFingerprint, freshNow.AddMinutes(1));
        using var freshListener = new TcpListener(IPAddress.Loopback, 0); freshListener.Start(1);
        using var freshPeerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var freshConnecting = freshPeerSocket.ConnectAsync((IPEndPoint)freshListener.LocalEndpoint, timeout.Token);
        using var freshHostSocket = await freshListener.AcceptSocketAsync(timeout.Token); await freshConnecting;
        var freshHostTask = PairingAuthenticatedChannel.StartHostAsync(freshHostSocket, hostCert, freshIdentity, freshOffer, timeout.Token);
        var freshPeerTask = PairingAuthenticatedChannel.StartPeerAsync(freshPeerSocket, freshPeerCert, freshIdentity, freshOffer, timeout.Token);
        await Task.WhenAll(freshHostTask, freshPeerTask);
        await using var freshHost = await freshHostTask; await using var freshPeer = await freshPeerTask;
        await Task.WhenAll(freshHost.ConfirmLocalAsync(timeout.Token), freshPeer.ReceiveRemoteConfirmationAsync(timeout.Token));
        await Task.WhenAll(freshPeer.ConfirmLocalAsync(timeout.Token), freshHost.ReceiveRemoteConfirmationAsync(timeout.Token));
        using var freshHostDelivery = freshHost.BeginDelivery(); using var freshPeerDelivery = freshPeer.BeginDelivery();
        var operation = freshPeerDelivery.ReceivePairingOperationAsync(timeout.Token);
        await freshHostDelivery.SendPairingOperationAsync(true, timeout.Token);
        Assert.True(await operation);
        var resumeHost = freshHostDelivery.ResumeHistoricalCommittedAsync(reopenedVault, timeout.Token);
        var resumePeer = freshPeerDelivery.AnswerHistoricalCommittedAsync(reopened, timeout.Token);
        if (scenario is not ("resume" or "manual_replace_blocked"))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(resumeHost, resumePeer).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(File.Exists(Path.Combine(vaultPath, "pending-prepare.bin")));
            Assert.True(File.Exists(Path.Combine(vaultPath, "pending-grant.bin")));
            Assert.True(reopenedVault.IsAuthorized("oldpeer", oldToken));
            if (replacement is not null)
                Assert.Equal(replacement.ToJson(), await File.ReadAllTextAsync(Path.Combine(peerPath, "peer-invitation.bin"), timeout.Token));
            return;
        }
        await Task.WhenAll(resumeHost, resumePeer).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await resumeHost);
        Assert.True(await resumePeer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => freshHostDelivery.ResumeHistoricalCommittedAsync(reopenedVault, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => freshPeerDelivery.AnswerHistoricalCommittedAsync(reopened, timeout.Token));
        var finished = await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token);
        Assert.Equal(PairingRecoveryPhase.AppliedCleanup, finished.Phase);
        Assert.True(File.Exists(Path.Combine(vaultPath, "pending-prepare.bin")));
        Assert.True(File.Exists(Path.Combine(vaultPath, "pending-grant.bin")));
        await freshHostDelivery.CleanupHistoricalFinishedDeliveryAsync(timeout.Token);
        Assert.Equal(PairingRecoveryPhase.None, (await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        Assert.True(reopenedVault.IsAuthorized("oldpeer", oldToken));
        Assert.Equal("peer", reopened.Get()!.PeerName);
    }

    private static async Task ForwardAsync(NetworkStream from, NetworkStream to, Func<bool> shouldDrop,
        TaskCompletionSource dropped, CancellationToken ct)
    {
        var buffer = new byte[16384];
        try
        {
            while (true)
            {
                var count = await from.ReadAsync(buffer, ct);
                if (count == 0) break;
                if (shouldDrop()) { dropped.TrySetResult(); continue; }
                await to.WriteAsync(buffer.AsMemory(0, count), ct);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
