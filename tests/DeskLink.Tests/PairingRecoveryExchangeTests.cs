using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingRecoveryExchangeTests
{
    [Fact]
    public async Task CancelBlockedRecoverySendReleasesStoreGateAndDisposal()
    {
        using var folder = new TestFolder();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), deadline.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), deadline.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var (historical, _) = Identity(hostCert, peerCert);
        var (identity, offer) = Identity(hostCert, peerCert);
        await using var current = await ConnectAsync(identity, offer, hostCert, peerCert, deadline.Token);
        await ConfirmAsync(current, deadline.Token);
        using var peer = current.Peer.BeginDelivery();
        var store = await PeerInvitationStore.OpenAsync(Path.Combine(folder.Path, "peer"), new TestProtector());
        var binding = new PairingHistoricalBinding(historical, Guid.NewGuid(), 1, 24822, "127.0.0.1", 24821, 24800, 24822, new string('A', 64));
        using var cancelled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = store.SendHistoricalReadyOrAbsentAsync(peer, binding, async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, cancelled.Token);
        await entered.Task.WaitAsync(deadline.Token);
        var waiting = store.GetPairingRecoveryStatusAsync(deadline.Token);
        Assert.False(waiting.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(3)));
        await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(File.Exists(Path.Combine(folder.Path, "peer", "peer-aborted-" + historical.TransactionId.ToString("N") + ".bin")));
    }

    [Fact]
    public async Task ConcurrentConfirmedSessionsCannotOverwriteOneReservation()
    {
        using var folder = new TestFolder();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), deadline.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), deadline.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        await using var store = await PeerInvitationStore.OpenAsync(Path.Combine(folder.Path, "peer"), new TestProtector());
        var (firstIdentity, firstOffer) = Identity(hostCert, peerCert);
        var (secondIdentity, secondOffer) = Identity(hostCert, peerCert);
        await using var first = await ConnectAsync(firstIdentity, firstOffer, hostCert, peerCert, deadline.Token);
        await using var second = await ConnectAsync(secondIdentity, secondOffer, hostCert, peerCert, deadline.Token);
        await ConfirmAsync(first, deadline.Token); await ConfirmAsync(second, deadline.Token);
        using var firstLease = first.Peer.BeginDelivery(); using var secondLease = second.Peer.BeginDelivery();
        var firstBinding = new PairingHistoricalBinding(firstIdentity, Guid.NewGuid(), 1, 24822, "127.0.0.1", 24821, 24800, 24822, new string('A', 64));
        var secondBinding = firstBinding with { Identity = secondIdentity, StageId = Guid.NewGuid() };
        async Task<bool> TryReserve(PairingTlsConsentSession.DeliveryLease lease, PairingHistoricalBinding binding)
        {
            try { return await store.ReserveCapacityAsync(lease, binding, deadline.Token); }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(TryReserve(firstLease, firstBinding), TryReserve(secondLease, secondBinding));
        Assert.Single(results, x => x);
        Assert.True(File.Exists(Path.Combine(folder.Path, "peer", "peer-reservation.bin")));
    }

    [Fact]
    public async Task ReopenedReservationRequiresItsConfirmedSessionToStageInvitation()
    {
        using var folder = new TestFolder();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), deadline.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), deadline.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var (identity, offer) = Identity(hostCert, peerCert);
        await using var original = await ConnectAsync(identity, offer, hostCert, peerCert, deadline.Token);
        await ConfirmAsync(original, deadline.Token);
        using var lease = original.Peer.BeginDelivery();

        var path = Path.Combine(folder.Path, "peer");
        var protector = new TestProtector();
        var old = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24921, KeyboardPort = 24900, DataPort = 24922,
            HostName = "oldhost", PeerName = "peer", CertificateSha256 = new string('A', 64),
            Token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
        var candidate = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, KeyboardPort = 24800, DataPort = 24822,
            HostName = identity.HostName, PeerName = identity.PeerName, CertificateSha256 = identity.HostFingerprint,
            Token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
        var digest = PeerInvitationStore.ComputePendingDigest(candidate);
        var receipt = new PairingPrepareReceipt(Guid.NewGuid(), 1, 24822, digest);
        var binding = new PairingHistoricalBinding(identity, receipt.StageId, receipt.Slot, receipt.DataPortBase,
            candidate.Host, candidate.Port, candidate.KeyboardPort, candidate.DataPort, digest);
        await using (var store = await PeerInvitationStore.OpenAsync(path, protector, deadline.Token))
        {
            await store.SaveConfirmedAsync(old, old.CertificateSha256, deadline.Token);
            Assert.True(await store.ReserveCapacityAsync(lease, binding, deadline.Token));
        }

        var reservationPath = Path.Combine(path, "peer-reservation.bin");
        var before = await File.ReadAllBytesAsync(reservationPath, deadline.Token);
        await using var reopened = await PeerInvitationStore.OpenAsync(path, protector, deadline.Token);
        var now = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.StagePendingAsync(identity, candidate,
            identity.PeerName, identity.HostFingerprint, digest, now, deadline.Token));

        var (otherIdentity, otherOffer) = Identity(hostCert, peerCert);
        await using var other = await ConnectAsync(otherIdentity, otherOffer, hostCert, peerCert, deadline.Token);
        await ConfirmAsync(other, deadline.Token);
        using var otherLease = other.Peer.BeginDelivery();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.StagePendingAsync(identity, candidate,
            identity.PeerName, identity.HostFingerprint, digest, now, deadline.Token, reservationLease: otherLease, receipt: receipt));
        Assert.Equal(before, await File.ReadAllBytesAsync(reservationPath, deadline.Token));
        Assert.Null(await reopened.GetPendingMetadataAsync(deadline.Token));
        Assert.Equal(old, reopened.Get());

        // A valid original lease and matching receipt prove that the candidate itself was valid.
        var staged = await reopened.StagePendingAsync(identity, candidate, identity.PeerName, identity.HostFingerprint,
            digest, now, deadline.Token, reservationLease: lease, receipt: receipt);
        Assert.Equal(identity.TransactionId, staged.TransactionId);
        Assert.Equal(digest, staged.InvitationDigest);
        var persisted = await reopened.GetPendingMetadataAsync(deadline.Token);
        Assert.NotNull(persisted);
        Assert.Equal(identity.TransactionId, persisted.TransactionId);
        Assert.Equal(digest, persisted.InvitationDigest);
        Assert.Equal(old, reopened.Get());
        Assert.Equal(before, await File.ReadAllBytesAsync(reservationPath, deadline.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservationChangedDuringStageIsRejectedBeforePendingPublication(bool deleteReservation)
    {
        using var folder = new TestFolder();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), deadline.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), deadline.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var (identity, offer) = Identity(hostCert, peerCert);
        await using var connection = await ConnectAsync(identity, offer, hostCert, peerCert, deadline.Token);
        await ConfirmAsync(connection, deadline.Token);
        using var lease = connection.Peer.BeginDelivery();
        var path = Path.Combine(folder.Path, "peer");
        var protector = new InterceptingProtector();
        await using var store = await PeerInvitationStore.OpenAsync(path, protector, deadline.Token);
        var old = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24921, KeyboardPort = 24900, DataPort = 24922,
            HostName = "oldhost", PeerName = "peer", CertificateSha256 = new string('A', 64),
            Token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
        await store.SaveConfirmedAsync(old, old.CertificateSha256, deadline.Token);
        var candidate = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, KeyboardPort = 24800, DataPort = 24822,
            HostName = identity.HostName, PeerName = identity.PeerName, CertificateSha256 = identity.HostFingerprint,
            Token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
        var digest = PeerInvitationStore.ComputePendingDigest(candidate);
        var receipt = new PairingPrepareReceipt(Guid.NewGuid(), 1, 24822, digest);
        var binding = new PairingHistoricalBinding(identity, receipt.StageId, receipt.Slot, receipt.DataPortBase,
            candidate.Host, candidate.Port, candidate.KeyboardPort, candidate.DataPort, digest);
        Assert.True(await store.ReserveCapacityAsync(lease, binding, deadline.Token));
        var reservationPath = Path.Combine(path, "peer-reservation.bin");
        var before = await File.ReadAllBytesAsync(reservationPath, deadline.Token);
        var activePath = Path.Combine(path, "peer-invitation.bin");
        var activeBefore = await File.ReadAllBytesAsync(activePath, deadline.Token);
        var original = System.Text.Encoding.UTF8.GetString(before);
        var modified = original.Replace("\"decision\":\"reserved\"", "\"decision\":\"refused\"", StringComparison.Ordinal);
        Assert.NotEqual(original, modified);
        var modifiedBytes = System.Text.Encoding.UTF8.GetBytes(modified);
        var changed = false;
        protector.OnProtect = () =>
        {
            protector.OnProtect = null;
            changed = true;
            if (deleteReservation) File.Delete(reservationPath);
            else File.WriteAllBytes(reservationPath, modifiedBytes);
        };
        var now = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StagePendingAsync(identity, candidate,
            identity.PeerName, identity.HostFingerprint, digest, now, deadline.Token, reservationLease: lease, receipt: receipt));
        Assert.True(changed);
        Assert.Null(await store.GetPendingMetadataAsync(deadline.Token));
        Assert.Equal(old, store.Get());
        Assert.Equal(activeBefore, await File.ReadAllBytesAsync(activePath, deadline.Token));
        if (deleteReservation) Assert.False(File.Exists(reservationPath));
        else Assert.Equal(modifiedBytes, await File.ReadAllBytesAsync(reservationPath, deadline.Token));
        Assert.Empty(Directory.EnumerateFiles(path, "peer-pending-invitation.bin.*.tmp"));

        await File.WriteAllBytesAsync(reservationPath, before, deadline.Token);
        var staged = await store.StagePendingAsync(identity, candidate, identity.PeerName, identity.HostFingerprint,
            digest, DateTimeOffset.UtcNow, deadline.Token, reservationLease: lease, receipt: receipt);
        Assert.Equal(identity.TransactionId, staged.TransactionId);
        Assert.Equal(digest, (await store.GetPendingMetadataAsync(deadline.Token))!.InvitationDigest);
        Assert.Equal(old, store.Get());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapacityReservationIsDurableAndCannotBeOverwritten(bool corrupt)
    {
        using var folder = new TestFolder();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), deadline.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), deadline.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var path = Path.Combine(folder.Path, "peer");
        var store = await PeerInvitationStore.OpenAsync(path, new TestProtector());
        var (identity, offer) = Identity(hostCert, peerCert);
        await using var old = await ConnectAsync(identity, offer, hostCert, peerCert, deadline.Token);
        await ConfirmAsync(old, deadline.Token);
        using var lease = old.Peer.BeginDelivery();
        var binding = new PairingHistoricalBinding(identity, Guid.NewGuid(), 1, 24822, "127.0.0.1", 24821, 24800, 24822, new string('A', 64));
        Assert.True(await store.ReserveCapacityAsync(lease, binding, deadline.Token));
        await store.DisposeAsync();
        var reservation = Path.Combine(path, "peer-reservation.bin");
        Assert.True(File.Exists(reservation));
        if (corrupt) await File.WriteAllTextAsync(reservation, "damaged synthetic reservation", deadline.Token);
        await using var reopened = await PeerInvitationStore.OpenAsync(path, new TestProtector());
        if (corrupt)
            await Assert.ThrowsAnyAsync<Exception>(() => reopened.ReserveCapacityAsync(lease, binding, deadline.Token));
        else
        {
            Assert.True(await reopened.ReserveCapacityAsync(lease, binding, deadline.Token));
            var (otherIdentity, otherOffer) = Identity(hostCert, peerCert);
            await using var other = await ConnectAsync(otherIdentity, otherOffer, hostCert, peerCert, deadline.Token);
            await ConfirmAsync(other, deadline.Token);
            using var otherLease = other.Peer.BeginDelivery();
            await Assert.ThrowsAnyAsync<Exception>(() => reopened.ReserveCapacityAsync(otherLease, binding with { Identity = otherIdentity }, deadline.Token));
        }
        Assert.True(File.Exists(reservation));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FreshTlsCanComparePromotedEvidenceButRejectsDamage(bool damagePromotion, bool previousInvitation)
    {
        using var folder = new TestFolder();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), deadline.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), deadline.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        var vaultPath = Path.Combine(folder.Path, "vault"); var peerPath = Path.Combine(folder.Path, "peer");
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, new TestProtector());
        var store = await PeerInvitationStore.OpenAsync(peerPath, new TestProtector());
        if (previousInvitation)
        {
            var oldInvitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, HostName = "oldhost", PeerName = "peer",
                CertificateSha256 = new string('A', 64), Token = Convert.ToBase64String(new byte[32]) };
            await store.SaveConfirmedAsync(oldInvitation, oldInvitation.CertificateSha256, deadline.Token);
        }
        var (oldIdentity, oldOffer) = Identity(hostCert, peerCert);
        await using (var old = await ConnectAsync(oldIdentity, oldOffer, hostCert, peerCert, deadline.Token))
        {
            await ConfirmAsync(old, deadline.Token);
            using var host = old.Host.BeginDelivery(); using var peer = old.Peer.BeginDelivery();
            await host.StageHostGrantAsync(vault, deadline.Token);
            await host.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, deadline.Token);
            await Task.WhenAll(host.SendPrepareAndAwaitReadyAsync(deadline.Token), peer.ReceivePrepareAndStageAsync(store, deadline.Token));
            await host.CommitVerifiedReadyAsync(deadline.Token);
            await Task.WhenAll(host.SendCommittedAsync(deadline.Token), peer.ReceiveCommittedAsync(store, deadline.Token));
            await Task.WhenAll(host.ReceiveAppliedAsync(deadline.Token), peer.PromoteAndSendAppliedAsync(store, deadline.Token));
        }
        await store.DisposeAsync();
        var promotionPath = Path.Combine(peerPath, "peer-promotion.bin");
        if (damagePromotion)
        {
            var bytes = await File.ReadAllBytesAsync(promotionPath, deadline.Token); bytes[0] ^= 1;
            await File.WriteAllBytesAsync(promotionPath, bytes, deadline.Token);
        }
        await using var reopened = await PeerInvitationStore.OpenAsync(peerPath, new TestProtector());
        Assert.Equal(damagePromotion ? PairingRecoveryPhase.EvidenceProblem : PairingRecoveryPhase.CommittedAwaitingPeer,
            (await reopened.GetPairingRecoveryStatusAsync(deadline.Token)).Phase);
        var (identity, offer) = Identity(hostCert, peerCert);
        await using var current = await ConnectAsync(identity, offer, hostCert, peerCert, deadline.Token);
        await ConfirmAsync(current, deadline.Token);
        using var hostRecovery = current.Host.BeginDelivery(); using var peerRecovery = current.Peer.BeginDelivery();
        var hostTask = hostRecovery.CompareHistoricalRecoveryAsync(vault, deadline.Token);
        var peerTask = peerRecovery.AnswerHistoricalRecoveryAsync(reopened, deadline.Token);
        if (damagePromotion)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => peerTask);
            await Assert.ThrowsAnyAsync<Exception>(() => hostTask);
            Assert.Throws<PairingRecoveryRequiredException>(() => reopened.Get());
        }
        else
        {
            await Task.WhenAll(hostTask, peerTask);
            Assert.True((await hostTask).Matched); Assert.True((await peerTask).Matched);
            Assert.Throws<PairingRecoveryRequiredException>(() => reopened.Get());
        }
        Assert.True(File.Exists(promotionPath));
        Assert.Single(vault.GetTrustedDevices());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FreshMutuallyConfirmedTlsComparesHistoricalEvidenceWithoutActivatingIt(bool damagePeerMarker, bool expireOldSession)
    {
        using var folder = new TestFolder();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "host.pem"), deadline.Token);
        await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, "peer.pem"), deadline.Token);
        using var hostCertificate = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCertificate = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(folder.Path, "vault"), new TestProtector());
        await using var peerStore = await PeerInvitationStore.OpenAsync(Path.Combine(folder.Path, "peer"), new TestProtector());

        var (oldIdentity, oldOffer) = Identity(hostCertificate, peerCertificate, expireOldSession ? TimeSpan.FromSeconds(4) : null);
        await using (var old = await ConnectAsync(oldIdentity, oldOffer, hostCertificate, peerCertificate, deadline.Token))
        {
            await ConfirmAsync(old, deadline.Token);
            using var host = old.Host.BeginDelivery();
            using var peer = old.Peer.BeginDelivery();
            await host.StageHostGrantAsync(vault, deadline.Token);
            await host.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, deadline.Token);
            await Task.WhenAll(host.SendPrepareAndAwaitReadyAsync(deadline.Token), peer.ReceivePrepareAndStageAsync(peerStore, deadline.Token));
        }

        if (expireOldSession)
        {
            var untilOldExpires = oldIdentity.ExpiresAtUtc - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);
            if (untilOldExpires > TimeSpan.Zero) await Task.Delay(untilOldExpires, deadline.Token);
            Assert.True(DateTimeOffset.UtcNow >= oldIdentity.ExpiresAtUtc);
        }
        var markerPath = Path.Combine(folder.Path, "peer", "peer-ready-evidence.bin");
        if (damagePeerMarker)
        {
            var bytes = await File.ReadAllBytesAsync(markerPath, deadline.Token); bytes[0] ^= 0x01;
            await File.WriteAllBytesAsync(markerPath, bytes, deadline.Token);
        }
        var (identity, offer) = Identity(hostCertificate, peerCertificate);
        await using var current = await ConnectAsync(identity, offer, hostCertificate, peerCertificate, deadline.Token);
        Assert.Throws<InvalidOperationException>(() => current.Host.BeginDelivery());
        await ConfirmAsync(current, deadline.Token);
        using var hostRecovery = current.Host.BeginDelivery();
        using var peerRecovery = current.Peer.BeginDelivery();
        var hostTask = hostRecovery.CompareHistoricalRecoveryAsync(vault, deadline.Token);
        var peerTask = peerRecovery.AnswerHistoricalRecoveryAsync(peerStore, deadline.Token);
        if (damagePeerMarker)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => peerTask);
            await Assert.ThrowsAnyAsync<Exception>(() => hostTask);
            Assert.Equal(PairingRecoveryPhase.EvidenceProblem, (await peerStore.GetPairingRecoveryStatusAsync()).Phase);
            Assert.Equal(PairingRecoveryPhase.AwaitingPeer, (await vault.GetPairingRecoveryStatusAsync()).Phase);
            Assert.Empty(vault.GetTrustedDevices());
            Assert.Null(peerStore.Get());
            return;
        }
        await Task.WhenAll(hostTask, peerTask);
        Assert.True((await hostTask).Matched);
        Assert.True((await peerTask).Matched);
        Assert.Equal(PairingRecoveryPhase.AwaitingPeer, (await vault.GetPairingRecoveryStatusAsync()).Phase);
        Assert.Equal(PairingRecoveryPhase.AwaitingPeer, (await peerStore.GetPairingRecoveryStatusAsync()).Phase);
        Assert.Empty(vault.GetTrustedDevices());
        Assert.Null(peerStore.Get());
        await Assert.ThrowsAnyAsync<Exception>(() => hostRecovery.CompareHistoricalRecoveryAsync(vault, deadline.Token));
    }

    [Fact]
    public async Task DifferentFreshPeerCertificateCannotClaimOldTransaction()
    {
        using var folder = new TestFolder();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (var name in new[] { "host.pem", "peer.pem", "other.pem" })
            await PrivateFiles.EnsureCertificateAsync(Path.Combine(folder.Path, name), deadline.Token);
        using var hostCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "host.pem"));
        using var peerCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "peer.pem"));
        using var otherCert = PrivateFiles.LoadTlsCertificate(Path.Combine(folder.Path, "other.pem"));
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(folder.Path, "vault"), new TestProtector());
        await using var store = await PeerInvitationStore.OpenAsync(Path.Combine(folder.Path, "peer"), new TestProtector());
        var (identity, offer) = Identity(hostCert, peerCert);
        await using (var old = await ConnectAsync(identity, offer, hostCert, peerCert, deadline.Token))
        {
            await ConfirmAsync(old, deadline.Token);
            using var host = old.Host.BeginDelivery(); using var peer = old.Peer.BeginDelivery();
            await host.StageHostGrantAsync(vault, deadline.Token);
            await host.FreezeHostInvitationForLoopbackTestAsync(24821, 24800, 24822, deadline.Token);
            await Task.WhenAll(host.SendPrepareAndAwaitReadyAsync(deadline.Token), peer.ReceivePrepareAndStageAsync(store, deadline.Token));
        }
        var (otherIdentity, otherOffer) = Identity(hostCert, otherCert);
        await using var current = await ConnectAsync(otherIdentity, otherOffer, hostCert, otherCert, deadline.Token);
        await ConfirmAsync(current, deadline.Token);
        using var currentHost = current.Host.BeginDelivery();
        await Assert.ThrowsAnyAsync<Exception>(() => currentHost.CompareHistoricalRecoveryAsync(vault, deadline.Token));
        Assert.Equal(PairingRecoveryPhase.AwaitingPeer, (await vault.GetPairingRecoveryStatusAsync()).Phase);
        Assert.Empty(vault.GetTrustedDevices());
    }

    private static (PairingSessionIdentity Identity, PeerIdentityOffer Offer) Identity(X509Certificate2 host, X509Certificate2 peer,
        TimeSpan? duration = null)
    {
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peer),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.Add(duration ?? TimeSpan.FromMinutes(2)) };
        return (new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, DeskLinkFingerprint.OfCertificate(host), offer.KeyboardFingerprint,
            now.Add(duration ?? TimeSpan.FromMinutes(1))), offer);
    }

    private static async Task<Connection> ConnectAsync(PairingSessionIdentity identity, PeerIdentityOffer offer,
        X509Certificate2 host, X509Certificate2 peer, CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, ct);
        var accepted = await listener.AcceptSocketAsync(ct); await connect;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, host, identity, offer, ct);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(client, peer, identity, offer, ct);
        await Task.WhenAll(hostTask, peerTask);
        return new(await hostTask, await peerTask);
    }

    private static async Task ConfirmAsync(Connection connection, CancellationToken ct)
    {
        await Task.WhenAll(connection.Host.ConfirmLocalAsync(ct), connection.Peer.ReceiveRemoteConfirmationAsync(ct));
        await Task.WhenAll(connection.Peer.ConfirmLocalAsync(ct), connection.Host.ReceiveRemoteConfirmationAsync(ct));
    }

    private sealed record Connection(PairingAuthenticatedChannel Host, PairingAuthenticatedChannel Peer) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); await Peer.DisposeAsync(); }
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }

    private sealed class InterceptingProtector : IMultiDeviceSecretProtector
    {
        internal Action? OnProtect { get; set; }
        public byte[] Protect(byte[] value) { OnProtect?.Invoke(); return value.ToArray(); }
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
