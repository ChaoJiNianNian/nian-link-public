using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingRecoveryStateTests
{
    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }

    private static (PairingSessionIdentity Identity, PeerIdentityOffer Offer) Candidate(DateTimeOffset? clock = null)
    {
        var now = clock ?? DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "newpeer", Platform = "windows", KeyboardFingerprint = new string('B', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(3) };
        return (new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, new string('A', 64), offer.KeyboardFingerprint, now.AddMinutes(2)), offer);
    }

    [Fact]
    public async Task EmptyStoreHasNoUnfinishedPairing()
    {
        using var folder = new TestFolder();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new TestProtector());
        var state = await vault.GetPairingRecoveryStatusAsync();
        Assert.Equal(PairingRecoveryPhase.None, state.Phase);
        Assert.Null(state.PeerName);
    }

    [Fact]
    public async Task UnsentStageCanBeAbandonedWithoutChangingExistingDevice()
    {
        using var folder = new TestFolder();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new TestProtector());
        var existing = await vault.AddConfirmedWithSlotAsync("oldpeer", new string('C', 64), "windows");
        var (identity, offer) = Candidate();
        await vault.StagePendingGrantAsync(identity, offer, DateTimeOffset.UtcNow);
        var before = await vault.GetPairingRecoveryStatusAsync();
        Assert.Equal(PairingRecoveryPhase.Unsent, before.Phase);
        Assert.Equal("newpeer", before.PeerName);
        Assert.True(await vault.AbandonUnsentPairingAsync(before));
        Assert.Equal(PairingRecoveryPhase.None, (await vault.GetPairingRecoveryStatusAsync()).Phase);
        Assert.True(vault.IsAuthorized("oldpeer", existing.Token));
        Assert.Null(await vault.GetPendingGrantMetadataAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task UnknownOrOrphanedMarkerIsNeverTreatedAsAnUnsentPairing()
    {
        using var folder = new TestFolder();
        var marker = Path.Combine(folder.Path, "pending-prepare.bin");
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new TestProtector());
        var (identity, offer) = Candidate();
        await vault.StagePendingGrantAsync(identity, offer, DateTimeOffset.UtcNow);
        var evidence = RandomNumberGenerator.GetBytes(32);
        await File.WriteAllBytesAsync(marker, evidence);
        var problem = await vault.GetPairingRecoveryStatusAsync();
        Assert.Equal(PairingRecoveryPhase.EvidenceProblem, problem.Phase);
        Assert.False(await vault.AbandonUnsentPairingAsync(problem));
        Assert.Equal(evidence, await File.ReadAllBytesAsync(marker));
        Assert.NotNull(await vault.GetPendingGrantMetadataAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task PreCancelledAbandonLeavesUnsentEvidenceUntouched()
    {
        using var folder = new TestFolder();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new TestProtector());
        var (identity, offer) = Candidate();
        await vault.StagePendingGrantAsync(identity, offer, DateTimeOffset.UtcNow);
        var reviewed = await vault.GetPairingRecoveryStatusAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.AbandonUnsentPairingAsync(reviewed, cancellation.Token));
        Assert.Equal(PairingRecoveryPhase.Unsent, (await vault.GetPairingRecoveryStatusAsync()).Phase);
    }

    [Fact]
    public async Task ExpiredUnsentStageCanBeDiscardedWithoutRevivingOldConfirmation()
    {
        using var folder = new TestFolder();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new TestProtector());
        var historic = DateTimeOffset.UtcNow.AddHours(-1);
        var (identity, offer) = Candidate(historic);
        await vault.StagePendingGrantAsync(identity, offer, historic, clock: () => historic);
        var reviewed = await vault.GetPairingRecoveryStatusAsync();
        Assert.Equal(PairingRecoveryPhase.Unsent, reviewed.Phase);
        Assert.True(await vault.AbandonUnsentPairingAsync(reviewed));
        Assert.Empty(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task UnexpectedOnDiskTrustChangesNeverPermitDiscard()
    {
        using var folder = new TestFolder();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new TestProtector());
        await vault.AddConfirmedWithSlotAsync("oldpeer", new string('C', 64), "windows");
        var (identity, offer) = Candidate();
        await vault.StagePendingGrantAsync(identity, offer, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "multi-device-trust.bin"), "damaged synthetic data");
        var problem = await vault.GetPairingRecoveryStatusAsync();
        Assert.Equal(PairingRecoveryPhase.EvidenceProblem, problem.Phase);
        Assert.False(await vault.AbandonUnsentPairingAsync(problem));
        Assert.NotNull(await vault.GetPendingGrantMetadataAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task StaleUnsentConfirmationMustNotDeleteReplacementTransaction()
    {
        using var folder = new TestFolder();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new TestProtector());
        var (firstIdentity, firstOffer) = Candidate();
        var first = await vault.StagePendingGrantAsync(firstIdentity, firstOffer, DateTimeOffset.UtcNow);
        var reviewed = await vault.GetPairingRecoveryStatusAsync();
        Assert.Equal(PairingRecoveryPhase.Unsent, reviewed.Phase);
        Assert.True(await vault.AbortPendingGrantAsync(first.StageId, firstIdentity));
        var (secondIdentity, secondOffer) = Candidate();
        var second = await vault.StagePendingGrantAsync(secondIdentity, secondOffer, DateTimeOffset.UtcNow);
        // The user approved abandoning the first transaction, not the replacement
        // bearing the same name and fingerprint.
        Assert.False(await vault.AbandonUnsentPairingAsync(reviewed));
        Assert.Equal(second.StageId, (await vault.GetPendingGrantMetadataAsync(DateTimeOffset.UtcNow))!.StageId);
        Assert.True(await vault.AbandonUnsentPairingAsync(await vault.GetPairingRecoveryStatusAsync()));
    }

    [Fact]
    public async Task ActualProtectedPrepareAndCommitRemainVisibleAfterReopen()
    {
        using var folder = new TestFolder();
        var hostPath = Path.Combine(folder.Path, "host.pem");
        var peerPath = Path.Combine(folder.Path, "peer.pem");
        await PrivateFiles.EnsureCertificateAsync(hostPath, CancellationToken.None);
        await PrivateFiles.EnsureCertificateAsync(peerPath, CancellationToken.None);
        using var hostCertificate = PrivateFiles.LoadTlsCertificate(hostPath);
        using var peerCertificate = PrivateFiles.LoadTlsCertificate(peerPath);
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peerCertificate),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, DeskLinkFingerprint.OfCertificate(hostCertificate), offer.KeyboardFingerprint, now.AddMinutes(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connecting = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using var accepted = await listener.AcceptSocketAsync(deadline.Token);
        await connecting;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, hostCertificate, identity, offer, deadline.Token);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(client, peerCertificate, identity, offer, deadline.Token);
        await Task.WhenAll(hostTask, peerTask);
        await using var host = await hostTask;
        await using var peer = await peerTask;
        await Task.WhenAll(host.ConfirmLocalAsync(deadline.Token), peer.ReceiveRemoteConfirmationAsync(deadline.Token));
        await Task.WhenAll(peer.ConfirmLocalAsync(deadline.Token), host.ReceiveRemoteConfirmationAsync(deadline.Token));
        using var hostDelivery = host.BeginDelivery();
        using var peerDelivery = peer.BeginDelivery();
        var vaultPath = Path.Combine(folder.Path, "vault");
        var protector = new TestProtector();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector);
        await using var store = await PeerInvitationStore.OpenAsync(Path.Combine(folder.Path, "peer"), protector);
        var oldToken = await vault.AddConfirmedAsync("oldpeer", new string('C', 64));
        await hostDelivery.StageHostGrantAsync(vault, deadline.Token);
        await hostDelivery.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, deadline.Token);
        await Task.WhenAll(hostDelivery.SendPrepareAndAwaitReadyAsync(deadline.Token), peerDelivery.ReceivePrepareAndStageAsync(store, deadline.Token));
        Assert.Equal(PairingRecoveryPhase.AwaitingPeer, (await vault.GetPairingRecoveryStatusAsync()).Phase);
        Assert.False(await vault.AbandonUnsentPairingAsync(await vault.GetPairingRecoveryStatusAsync()));
        await hostDelivery.CommitVerifiedReadyAsync(deadline.Token);
        Assert.Equal(PairingRecoveryPhase.CommittedAwaitingPeer, (await vault.GetPairingRecoveryStatusAsync()).Phase);
        Assert.False(await vault.AbandonUnsentPairingAsync(await vault.GetPairingRecoveryStatusAsync()));
        Assert.True(vault.IsAuthorized("oldpeer", oldToken));
        await vault.DisposeAsync();
        await using var reopened = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector);
        Assert.Equal(PairingRecoveryPhase.CommittedAwaitingPeer, (await reopened.GetPairingRecoveryStatusAsync()).Phase);
        Assert.True(reopened.IsAuthorized("oldpeer", oldToken));
        // If both local pairing evidence files disappear after a host commit, the
        // committed ledger is not proof that the peer applied the invitation.
        File.Delete(Path.Combine(vaultPath, "pending-grant.bin"));
        File.Delete(Path.Combine(vaultPath, "pending-prepare.bin"));
        Assert.Equal(PairingRecoveryPhase.EvidenceProblem, (await reopened.GetPairingRecoveryStatusAsync()).Phase);
        Assert.True(reopened.IsAuthorized("oldpeer", oldToken));
    }
}
