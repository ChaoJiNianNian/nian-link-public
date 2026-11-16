using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingHostCapacityTests
{
    [Fact]
    public async Task TrustChangedBetweenStageChecksCannotPublishPendingGrant()
    {
        using var folder = new TestFolder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new TestProtector(), timeout.Token);
        var oldToken = await vault.AddConfirmedAsync("oldpeer", new string('A', 64), timeout.Token);
        Assert.True(vault.IsAuthorized("oldpeer", oldToken));
        var trustPath = Path.Combine(folder.Path, "multi-device-trust.bin");
        var original = await File.ReadAllBytesAsync(trustPath, timeout.Token);
        var originalJson = System.Text.Encoding.UTF8.GetString(original);
        var changed = System.Text.Encoding.UTF8.GetBytes(originalJson.Replace("oldpeer", "otherpeer", StringComparison.Ordinal));
        var calls = 0;
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('B', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, new string('C', 64), offer.KeyboardFingerprint, now.AddMinutes(1));

        DateTimeOffset ChangeTrustDuringStage()
        {
            if (++calls == 1) File.WriteAllBytes(trustPath, changed);
            return now;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.StagePendingGrantAsync(identity, offer, now, timeout.Token, ChangeTrustDuringStage));
        Assert.Equal(1, calls);
        Assert.NotEqual(original, changed);
        Assert.Equal(changed, await File.ReadAllBytesAsync(trustPath, timeout.Token));
        Assert.False(File.Exists(Path.Combine(folder.Path, "pending-grant.bin")));
        Assert.False(vault.IsAuthorized("oldpeer", oldToken));
        Assert.Equal(PairingRecoveryPhase.EvidenceProblem, (await vault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
    }

    [Fact]
    public async Task CorruptedRetiredIndexFailsClosedWithoutChangingTheVaultFile()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "multi-device-trust.bin");
        var invalid = JsonSerializer.SerializeToUtf8Bytes(new { version = 3, devices = Array.Empty<object>(),
            transactions = Array.Empty<object>(), retired = "AA==" });
        await File.WriteAllBytesAsync(path, invalid);
        await Assert.ThrowsAsync<InvalidDataException>(() => MultiDeviceCredentialVault.OpenAsync(folder.Path, new TestProtector()));
        Assert.Equal(invalid, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ChangedTrustAfterOpenPreventsNewPendingGrant()
    {
        using var folder = new TestFolder();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new TestProtector());
        await vault.AddConfirmedAsync("oldpeer", new string('A', 64));
        var trustPath = Path.Combine(folder.Path, "multi-device-trust.bin");
        const string damage = "changed protected trust";
        await File.WriteAllTextAsync(trustPath, damage);
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('B', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, new string('C', 64), offer.KeyboardFingerprint, now.AddMinutes(1));
        await Assert.ThrowsAnyAsync<Exception>(() => vault.StagePendingGrantAsync(identity, offer, now));
        Assert.False(File.Exists(Path.Combine(folder.Path, "pending-grant.bin")));
        Assert.Equal(damage, await File.ReadAllTextAsync(trustPath));
        Assert.Equal(PairingRecoveryPhase.EvidenceProblem, (await vault.GetPairingRecoveryStatusAsync()).Phase);
    }

    [Fact]
    public async Task FullRetiredIndexIsReadableButCannotDiscardOldOrCreateNewEvidence()
    {
        using var folder = new TestFolder();
        var now = DateTimeOffset.UtcNow; var packed = new byte[1100 * 96];
        for (var i = 0; i < 1100; i++)
        {
            var offset = i * 96;
            Guid.NewGuid().TryWriteBytes(packed.AsSpan(offset, 16));
            Guid.NewGuid().TryWriteBytes(packed.AsSpan(offset + 16, 16));
            Guid.NewGuid().TryWriteBytes(packed.AsSpan(offset + 32, 16));
            Guid.NewGuid().TryWriteBytes(packed.AsSpan(offset + 48, 16));
            SHA256.HashData(BitConverter.GetBytes(i)).CopyTo(packed.AsSpan(offset + 64, 32));
        }
        var transactions = Enumerable.Range(0, 64).Select(i => new {
            transactionId = Guid.NewGuid(), stageId = Guid.NewGuid(), sessionId = Guid.NewGuid(), offerNonce = Guid.NewGuid(),
            hostName = "host", hostFingerprint = new string('A', 64), peerName = "previous", peerFingerprint = new string('B', 64),
            platform = "windows", slot = 1, invitationDigest = new string('C', 64),
            credentialDigest = i.ToString("X64"), state = "aborted", expiresAtUtc = now.AddMinutes(-1)
        }).ToArray();
        var document = JsonSerializer.SerializeToUtf8Bytes(new { version = 3, devices = Array.Empty<object>(), transactions, retired = Convert.ToBase64String(packed) });
        Assert.True(document.Length > 65536);
        var trustFile = Path.Combine(folder.Path, "multi-device-trust.bin");
        await File.WriteAllBytesAsync(trustFile, document);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new TestProtector());
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('D', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, new string('E', 64), offer.KeyboardFingerprint, now.AddMinutes(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.StagePendingGrantAsync(identity, offer, now));
        Assert.False(File.Exists(Path.Combine(folder.Path, "pending-grant.bin")));
        Assert.Equal(document, await File.ReadAllBytesAsync(trustFile));
    }

    [Theory]
    [InlineData(63, true, "none")]
    [InlineData(64, true, "none")]
    [InlineData(64, false, "transaction")]
    [InlineData(64, false, "session")]
    [InlineData(64, false, "nonce")]
    public async Task HostReservesTransactionCapacityBeforeCreatingPendingGrant(int count, bool allowed, string replay)
    {
        using var folder = new TestFolder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
        using var delivery = host.BeginDelivery();
        var vaultPath = Path.Combine(folder.Path, "vault"); Directory.CreateDirectory(vaultPath);
        var transactions = Enumerable.Range(0, count).Select(i => new {
            transactionId = replay == "transaction" && i == 0 ? identity.TransactionId : Guid.NewGuid(), stageId = Guid.NewGuid(),
            sessionId = replay == "session" && i == 0 ? identity.SessionId : Guid.NewGuid(),
            offerNonce = replay == "nonce" && i == 0 ? identity.OfferNonce : Guid.NewGuid(),
            hostName = "host", hostFingerprint = new string('A', 64), peerName = "previous", peerFingerprint = new string('B', 64),
            platform = "windows", slot = 1, invitationDigest = new string('C', 64),
            credentialDigest = i.ToString("X64"), state = "aborted", expiresAtUtc = now.AddMinutes(-1)
        }).ToArray();
        var document = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, devices = Array.Empty<object>(), transactions });
        await File.WriteAllBytesAsync(Path.Combine(vaultPath, "multi-device-trust.bin"), document, timeout.Token);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, new TestProtector(), timeout.Token);
        if (allowed) await delivery.StageHostGrantAsync(vault, timeout.Token);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.StageHostGrantAsync(vault, timeout.Token));
        Assert.Equal(allowed, File.Exists(Path.Combine(vaultPath, "pending-grant.bin")));
        Assert.Empty(vault.GetTrustedDevices());
        if (count == 64)
        {
            using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(vaultPath, "multi-device-trust.bin"), timeout.Token));
            Assert.Equal(3, saved.RootElement.GetProperty("version").GetInt32());
            Assert.NotEmpty(saved.RootElement.GetProperty("retired").GetString()!);
            if (allowed)
            {
                var status = await vault.GetPairingRecoveryStatusAsync(timeout.Token);
                Assert.True(await vault.AbandonUnsentPairingAsync(status, timeout.Token));
                await vault.DisposeAsync();
                await using var reopened = await MultiDeviceCredentialVault.OpenAsync(vaultPath, new TestProtector(), timeout.Token);
                var oldTransaction = identity with { TransactionId = transactions[0].transactionId };
                await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.StagePendingGrantAsync(oldTransaction, offer, DateTimeOffset.UtcNow, timeout.Token));
                Assert.False(File.Exists(Path.Combine(vaultPath, "pending-grant.bin")));
                var freshToken = await reopened.AddConfirmedAsync("freshpeer", new string('E', 64), timeout.Token);
                Assert.True(reopened.IsAuthorized("freshpeer", freshToken));
                Assert.True(await reopened.RevokeAsync("freshpeer", timeout.Token));
                Assert.False(reopened.IsAuthorized("freshpeer", freshToken));
                await reopened.DisposeAsync();
                await using var reopenedAgain = await MultiDeviceCredentialVault.OpenAsync(vaultPath, new TestProtector(), timeout.Token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => reopenedAgain.StagePendingGrantAsync(oldTransaction, offer, DateTimeOffset.UtcNow, timeout.Token));
            }
        }
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
