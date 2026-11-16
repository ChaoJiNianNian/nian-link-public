using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingCommittedRecoveryTests
{
    [Theory]
    [InlineData("peer_ready")]
    [InlineData("peer_promoted_host_committed")]
    [InlineData("peer_promoted_host_applied")]
    [InlineData("ready_damaged")]
    [InlineData("revoked")]
    [InlineData("uncommitted_ready")]
    [InlineData("uncommitted_ready_damaged")]
    [InlineData("uncommitted_conflict")]
    public async Task NewConfirmedSessionFinishesInterruptedCommittedTransaction(string scenario)
    {
        using var folder = new TestFolder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), timeout.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), timeout.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var (oldOffer, oldIdentity) = CreateIdentity(hostCert, peerCert);
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
        await hostDelivery.StageHostGrantAsync(vault, timeout.Token);
        await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, timeout.Token);
        await Task.WhenAll(hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token), peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token));
        if (!scenario.StartsWith("uncommitted", StringComparison.Ordinal))
            await hostDelivery.CommitVerifiedReadyAsync(timeout.Token);
        if (scenario is "peer_promoted_host_committed" or "peer_promoted_host_applied")
        {
            await Task.WhenAll(hostDelivery.SendCommittedAsync(timeout.Token), peerDelivery.ReceiveCommittedAsync(store, timeout.Token));
            if (scenario == "peer_promoted_host_applied")
                await Task.WhenAll(hostDelivery.ReceiveAppliedAsync(timeout.Token), peerDelivery.PromoteAndSendAppliedAsync(store, timeout.Token));
            else
                await peerDelivery.PromoteAndSendAppliedAsync(store, timeout.Token); // Host never reads APPLIED.
        }
        peerSocket.Dispose();
        await store.DisposeAsync(); await vault.DisposeAsync();
        await using var reopenedStore = await PeerInvitationStore.OpenAsync(peerPath, protector, timeout.Token);
        await using var reopenedVault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector, timeout.Token);
        Assert.Equal(scenario.StartsWith("uncommitted", StringComparison.Ordinal) ? PairingRecoveryPhase.AwaitingPeer :
            PairingRecoveryPhase.CommittedAwaitingPeer, (await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        Assert.True(reopenedVault.IsAuthorized("oldpeer", oldToken));
        if (scenario is "ready_damaged" or "uncommitted_ready_damaged")
            await File.WriteAllTextAsync(Path.Combine(peerPath, "peer-ready-evidence.bin"), "damaged readiness", timeout.Token);
        if (scenario == "revoked") Assert.True(await reopenedVault.RevokeAsync("peer", timeout.Token));
        string? conflictingToken = null;
        if (scenario == "uncommitted_conflict")
            conflictingToken = await reopenedVault.AddConfirmedAsync("peer", new string('B', 64), timeout.Token);

        var (freshOffer, freshIdentity) = CreateIdentity(hostCert, peerCert);
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
        var operation = freshPeerDelivery.ReceivePairingOperationAsync(timeout.Token);
        await freshHostDelivery.SendPairingOperationAsync(true, timeout.Token);
        Assert.True(await operation);
        var recovery = Task.WhenAll(freshHostDelivery.ResumeHistoricalCommittedAsync(reopenedVault, timeout.Token),
            freshPeerDelivery.AnswerHistoricalCommittedAsync(reopenedStore, timeout.Token));
        if (scenario is "ready_damaged" or "revoked" or "uncommitted_ready_damaged" or "uncommitted_conflict")
        {
            await Assert.ThrowsAnyAsync<Exception>(() => recovery.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.True(File.Exists(Path.Combine(vaultPath, "pending-prepare.bin")));
            Assert.True(File.Exists(Path.Combine(vaultPath, "pending-grant.bin")));
            Assert.True(reopenedVault.IsAuthorized("oldpeer", oldToken));
            if (conflictingToken is not null) Assert.True(reopenedVault.IsAuthorized("peer", conflictingToken));
            return;
        }
        Assert.All(await recovery.WaitAsync(TimeSpan.FromSeconds(10)), Assert.True);
        Assert.Equal(PairingRecoveryPhase.AppliedCleanup, (await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        await freshHostDelivery.CleanupHistoricalFinishedDeliveryAsync(timeout.Token);
        Assert.Equal(PairingRecoveryPhase.None, (await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        Assert.True(reopenedVault.IsAuthorized("oldpeer", oldToken));
        Assert.Equal("peer", reopenedStore.Get()!.PeerName);
        await reopenedStore.DisposeAsync();
        await using var finalStore = await PeerInvitationStore.OpenAsync(peerPath, protector, timeout.Token);
        Assert.Equal("peer", finalStore.Get()!.PeerName);
    }

    private static (PeerIdentityOffer, PairingSessionIdentity) CreateIdentity(
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
