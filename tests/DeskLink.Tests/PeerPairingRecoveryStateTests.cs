using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PeerPairingRecoveryStateTests
{
    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }

    private static (PairingSessionIdentity Identity, MultiPeerInvitation Invitation) Candidate()
    {
        var now = DateTimeOffset.UtcNow;
        var fingerprint = new string('A', 64);
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "host", "peer", fingerprint, new string('B', 64), now.AddMinutes(2));
        var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, KeyboardPort = 24800, DataPort = 24822,
            HostName = "host", PeerName = "peer", CertificateSha256 = fingerprint, Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        return (identity, invitation);
    }

    private static async Task StageAsync(PeerInvitationStore store, PairingSessionIdentity identity, MultiPeerInvitation value)
        => await store.StagePendingAsync(identity, value, identity.PeerName, identity.HostFingerprint,
            PeerInvitationStore.ComputePendingDigest(value), DateTimeOffset.UtcNow);

    [Fact]
    public async Task EmptyStoreHasNoUnfinishedPairing()
    {
        using var folder = new TestFolder();
        await using var store = await PeerInvitationStore.OpenAsync(folder.Path, new TestProtector());
        Assert.Equal(PairingRecoveryPhase.None, (await store.GetPairingRecoveryStatusAsync()).Phase);
    }

    [Fact]
    public async Task UnsentPendingCanBeAbandonedWithoutChangingExistingInvitation()
    {
        using var folder = new TestFolder();
        await using var store = await PeerInvitationStore.OpenAsync(folder.Path, new TestProtector());
        var (identity, next) = Candidate();
        var old = next with { HostName = "oldhost", Port = 24921, CertificateSha256 = new string('C', 64) };
        await store.SaveConfirmedAsync(old, old.CertificateSha256);
        await StageAsync(store, identity, next);
        var state = await store.GetPairingRecoveryStatusAsync();
        Assert.Equal(PairingRecoveryPhase.Unsent, state.Phase);
        Assert.Equal("host", state.PeerName);
        Assert.True(await store.AbandonUnsentPairingAsync(state));
        Assert.Equal(old, store.Get());
        Assert.Null(await store.GetPendingMetadataAsync());
    }

    [Fact]
    public async Task BadOrOrphanedReadyEvidenceCannotBeAbandoned()
    {
        using var folder = new TestFolder();
        await using var store = await PeerInvitationStore.OpenAsync(folder.Path, new TestProtector());
        var (identity, next) = Candidate();
        await StageAsync(store, identity, next);
        var evidence = RandomNumberGenerator.GetBytes(32);
        var marker = Path.Combine(folder.Path, "peer-ready-evidence.bin");
        await File.WriteAllBytesAsync(marker, evidence);
        var problem = await store.GetPairingRecoveryStatusAsync();
        Assert.Equal(PairingRecoveryPhase.EvidenceProblem, problem.Phase);
        Assert.False(await store.AbandonUnsentPairingAsync(problem));
        Assert.Equal(evidence, await File.ReadAllBytesAsync(marker));
        Assert.NotNull(await store.GetPendingMetadataAsync());
    }

    [Fact]
    public async Task CancellationAndChangedActiveFileLeavePendingEvidence()
    {
        using var folder = new TestFolder();
        await using var store = await PeerInvitationStore.OpenAsync(folder.Path, new TestProtector());
        var (identity, next) = Candidate();
        var old = next with { HostName = "oldhost", Port = 24921, CertificateSha256 = new string('C', 64) };
        await store.SaveConfirmedAsync(old, old.CertificateSha256);
        await StageAsync(store, identity, next);
        var reviewed = await store.GetPairingRecoveryStatusAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AbandonUnsentPairingAsync(reviewed, cancellation.Token));
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "peer-invitation.bin"), "damaged synthetic data");
        Assert.Equal(PairingRecoveryPhase.EvidenceProblem, (await store.GetPairingRecoveryStatusAsync()).Phase);
        Assert.False(await store.AbandonUnsentPairingAsync(reviewed));
        Assert.NotNull(await store.GetPendingMetadataAsync());
    }

    [Fact]
    public async Task StaleUnsentConfirmationMustNotDeleteReplacementTransaction()
    {
        using var folder = new TestFolder();
        await using var store = await PeerInvitationStore.OpenAsync(folder.Path, new TestProtector());
        var (firstIdentity, first) = Candidate();
        await StageAsync(store, firstIdentity, first);
        var reviewed = await store.GetPairingRecoveryStatusAsync();
        Assert.Equal(PairingRecoveryPhase.Unsent, reviewed.Phase);
        Assert.True(await store.AbortPendingAsync(firstIdentity, PeerInvitationStore.ComputePendingDigest(first)));
        var (secondIdentity, second) = Candidate();
        // A reused transaction identifier still must not revive an older selection.
        secondIdentity = secondIdentity with { TransactionId = firstIdentity.TransactionId };
        await StageAsync(store, secondIdentity, second);
        Assert.False(await store.AbandonUnsentPairingAsync(reviewed));
        Assert.Equal(secondIdentity.TransactionId, (await store.GetPendingMetadataAsync())!.TransactionId);
        Assert.True(await store.AbandonUnsentPairingAsync(await store.GetPairingRecoveryStatusAsync()));
    }

    [Fact]
    public async Task ReviewFromAnotherStoreCannotAuthorizeDiscard()
    {
        using var firstFolder = new TestFolder(); using var secondFolder = new TestFolder();
        await using var firstStore = await PeerInvitationStore.OpenAsync(firstFolder.Path, new TestProtector());
        await using var secondStore = await PeerInvitationStore.OpenAsync(secondFolder.Path, new TestProtector());
        var (identity, value) = Candidate();
        // Deliberately identical contents: the store owner must still match.
        await StageAsync(firstStore, identity, value); await StageAsync(secondStore, identity, value);
        var firstReview = await firstStore.GetPairingRecoveryStatusAsync();
        Assert.False(await secondStore.AbandonUnsentPairingAsync(firstReview));
        Assert.NotNull(await secondStore.GetPendingMetadataAsync());
        Assert.True(await secondStore.AbandonUnsentPairingAsync(await secondStore.GetPairingRecoveryStatusAsync()));
    }

    [Fact]
    public async Task ActualTlsReadyEvidenceIsStillPendingAfterReopen()
    {
        using var folder = new TestFolder();
        var hostPath = Path.Combine(folder.Path, "host.pem"); var peerPath = Path.Combine(folder.Path, "peer.pem");
        await PrivateFiles.EnsureCertificateAsync(hostPath, CancellationToken.None);
        await PrivateFiles.EnsureCertificateAsync(peerPath, CancellationToken.None);
        using var hostCertificate = PrivateFiles.LoadTlsCertificate(hostPath);
        using var peerCertificate = PrivateFiles.LoadTlsCertificate(peerPath);
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peerCertificate),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, DeskLinkFingerprint.OfCertificate(hostCertificate), offer.KeyboardFingerprint, now.AddMinutes(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connecting = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using var accepted = await listener.AcceptSocketAsync(deadline.Token); await connecting;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, hostCertificate, identity, offer, deadline.Token);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(client, peerCertificate, identity, offer, deadline.Token);
        await Task.WhenAll(hostTask, peerTask);
        await using var host = await hostTask; await using var peer = await peerTask;
        await Task.WhenAll(host.ConfirmLocalAsync(deadline.Token), peer.ReceiveRemoteConfirmationAsync(deadline.Token));
        await Task.WhenAll(peer.ConfirmLocalAsync(deadline.Token), host.ReceiveRemoteConfirmationAsync(deadline.Token));
        using var hostDelivery = host.BeginDelivery(); using var peerDelivery = peer.BeginDelivery();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(folder.Path, "vault"), new TestProtector());
        var peerDirectory = Path.Combine(folder.Path, "peer");
        await using var store = await PeerInvitationStore.OpenAsync(peerDirectory, new TestProtector());
        var (_, oldSeed) = Candidate();
        var old = oldSeed with { HostName = "oldhost", Port = 24921, CertificateSha256 = new string('C', 64) };
        await store.SaveConfirmedAsync(old, old.CertificateSha256);
        await hostDelivery.StageHostGrantAsync(vault, deadline.Token);
        await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, deadline.Token);
        await Task.WhenAll(hostDelivery.SendPrepareAndAwaitReadyAsync(deadline.Token), peerDelivery.ReceivePrepareAndStageAsync(store, deadline.Token));
        Assert.Equal(PairingRecoveryPhase.AwaitingPeer, (await store.GetPairingRecoveryStatusAsync()).Phase);
        Assert.False(await store.AbandonUnsentPairingAsync(await store.GetPairingRecoveryStatusAsync()));
        Assert.Equal(old, store.Get());
        await store.DisposeAsync();
        await using var reopened = await PeerInvitationStore.OpenAsync(peerDirectory, new TestProtector());
        Assert.Equal(PairingRecoveryPhase.AwaitingPeer, (await reopened.GetPairingRecoveryStatusAsync()).Phase);
        Assert.Equal(old, reopened.Get());
    }
}
