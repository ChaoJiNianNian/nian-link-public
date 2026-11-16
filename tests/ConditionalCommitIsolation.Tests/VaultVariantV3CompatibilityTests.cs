using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Theory]
    [InlineData("revoked")]
    [InlineData("aborted")]
    public async Task V3HistoricalTransactionCredentialCannotBeReissued(string state)
    {
        var root = VaultRoot();
        Directory.CreateDirectory(root);
        try
        {
            var token = RandomNumberGenerator.GetBytes(32);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes("nian-link-vault-token-v2\0"));
            sha.AppendData(token);
            var digest = Convert.ToHexString(sha.GetHashAndReset());
            var transaction = new { transactionId = Guid.NewGuid(), stageId = Guid.NewGuid(), sessionId = Guid.NewGuid(), offerNonce = Guid.NewGuid(),
                hostName = "host", hostFingerprint = new string('B', 64), peerName = "previous", peerFingerprint = new string('C', 64),
                platform = "windows", slot = 1, invitationDigest = new string('D', 64), credentialDigest = digest, state,
                expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
            var file = Path.Combine(root, "multi-device-trust.bin");
            var input = JsonSerializer.SerializeToUtf8Bytes(new { version = 3, devices = Array.Empty<object>(),
                transactions = new[] { transaction }, retired = "" });
            await File.WriteAllBytesAsync(file, input);
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new VaultV3TestProtector());
            vault.NextCredentialForTest = () => token.ToArray();
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.AddConfirmedAsync("peer", new string('A', 64)));
            Assert.Empty(vault.GetTrustedDevices());
            Assert.Equal(input, await File.ReadAllBytesAsync(file));
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task RetiredCredentialCannotBeReissuedAndOldHistoryIsUnchanged()
    {
        var root = VaultRoot();
        Directory.CreateDirectory(root);
        try
        {
            var token = RandomNumberGenerator.GetBytes(32);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes("nian-link-vault-token-v2\0"));
            sha.AppendData(token);
            var digest = sha.GetHashAndReset();
            var retired = new byte[96];
            Guid.NewGuid().TryWriteBytes(retired.AsSpan(0, 16));
            Guid.NewGuid().TryWriteBytes(retired.AsSpan(16, 16));
            Guid.NewGuid().TryWriteBytes(retired.AsSpan(32, 16));
            Guid.NewGuid().TryWriteBytes(retired.AsSpan(48, 16));
            digest.CopyTo(retired.AsSpan(64));
            var file = Path.Combine(root, "multi-device-trust.bin");
            var input = JsonSerializer.SerializeToUtf8Bytes(new { version = 3, devices = Array.Empty<object>(),
                transactions = Array.Empty<object>(), retired = Convert.ToBase64String(retired) });
            await File.WriteAllBytesAsync(file, input);
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new VaultV3TestProtector());
            vault.NextCredentialForTest = () => token.ToArray();
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.AddConfirmedAsync("peer", new string('A', 64)));
            Assert.Empty(vault.GetTrustedDevices());
            Assert.Equal(input, await File.ReadAllBytesAsync(file));
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("applied")]
    [InlineData("finished")]
    [InlineData("retired")]
    public async Task RevokingV3ActiveDeviceRevokesItsTransaction(string state)
    {
        var root = VaultRoot();
        Directory.CreateDirectory(root);
        try
        {
            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var token = Convert.ToBase64String(tokenBytes);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes("nian-link-vault-token-v2\0"));
            sha.AppendData(tokenBytes);
            var credentialDigest = Convert.ToHexString(sha.GetHashAndReset());
            var fingerprint = new string('A', 64);
            var device = new { name = "peer", fingerprint, token, platform = "windows", slot = 1 };
            var transaction = new { transactionId = Guid.NewGuid(), stageId = Guid.NewGuid(), sessionId = Guid.NewGuid(), offerNonce = Guid.NewGuid(),
                hostName = "host", hostFingerprint = new string('B', 64), peerName = "peer", peerFingerprint = fingerprint,
                platform = "windows", slot = 1, invitationDigest = new string('C', 64), credentialDigest, state, expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) };
            var file = Path.Combine(root, "multi-device-trust.bin");
            await File.WriteAllBytesAsync(file, JsonSerializer.SerializeToUtf8Bytes(new { version = 3, devices = new[] { device }, transactions = new[] { transaction }, retired = "" }));
            await using (var vault = await MultiDeviceCredentialVault.OpenAsync(root, new VaultV3TestProtector()))
            {
                Assert.True(vault.IsAuthorized("peer", token));
                Assert.True(await vault.RevokeAsync("peer"));
                Assert.False(vault.IsAuthorized("peer", token));
            }
            using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(file));
            Assert.Equal(3, doc.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("revoked", doc.RootElement.GetProperty("transactions")[0].GetProperty("state").GetString());
            Assert.Empty(doc.RootElement.GetProperty("devices").EnumerateArray());
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task V3LargeRetiredHistorySurvivesAddRevokeAndReopen()
    {
        var root = VaultRoot();
        Directory.CreateDirectory(root);
        try
        {
            var packed = new byte[700 * 96];
            for (var i = 0; i < 700; i++)
            {
                var offset = i * 96;
                Guid.NewGuid().TryWriteBytes(packed.AsSpan(offset, 16));
                Guid.NewGuid().TryWriteBytes(packed.AsSpan(offset + 16, 16));
                Guid.NewGuid().TryWriteBytes(packed.AsSpan(offset + 32, 16));
                Guid.NewGuid().TryWriteBytes(packed.AsSpan(offset + 48, 16));
                SHA256.HashData(BitConverter.GetBytes(i)).CopyTo(packed.AsSpan(offset + 64, 32));
            }
            var history = Convert.ToBase64String(packed);
            var input = JsonSerializer.SerializeToUtf8Bytes(new { version = 3, devices = Array.Empty<object>(), transactions = Array.Empty<object>(), retired = history });
            Assert.True(input.Length > 65536);
            var file = Path.Combine(root, "multi-device-trust.bin");
            await File.WriteAllBytesAsync(file, input);
            await using (var vault = await MultiDeviceCredentialVault.OpenAsync(root, new VaultV3TestProtector()))
            {
                var token = await vault.AddConfirmedAsync("peer", new string('A', 64));
                Assert.True(vault.IsAuthorized("peer", token));
                Assert.True(await vault.RevokeAsync("peer"));
            }
            using (var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(file)))
            {
                Assert.Equal(3, doc.RootElement.GetProperty("version").GetInt32());
                Assert.Equal(history, doc.RootElement.GetProperty("retired").GetString());
                Assert.Empty(doc.RootElement.GetProperty("devices").EnumerateArray());
            }
            await using var reopened = await MultiDeviceCredentialVault.OpenAsync(root, new VaultV3TestProtector());
            Assert.Empty(reopened.GetTrustedDevices());
        }
        finally { CleanVault(root); }
    }

    private sealed class VaultV3TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
