using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace VaultGrantIdentity.Research;

public sealed class GrantIdentityFenceTests
{
    [Fact]
    public void ResearchBuildProvidesAnExplicitVaultSnapshotBoundary()
    {
        var method = typeof(MultiDeviceCredentialVault).GetMethod("TryCommitUniqueResearchAuthorizedAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
    }

    [Fact]
    public async Task AuthorizedIdentityIsStableAcrossVaultReopenWithoutRewritingTrust()
    {
        var root = NewDirectory(); var protector = new WindowsCredentialProtector();
        using var fence = new GrantIdentityFence(); var operation = Guid.NewGuid(); byte[] token;
        byte[] before;
        await using (var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector))
        {
            token = Convert.FromBase64String(await vault.AddConfirmedAsync("peer", new string('A', 64)));
            before = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "multi-device-trust.bin")));
            Assert.Equal(ResearchResult.Accepted, await fence.SealAsync(vault, "peer", token, operation));
        }
        try
        {
            await using var reopened = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            Assert.Equal(ResearchResult.Accepted, await fence.SealAsync(reopened, "PEER", token, operation));
            Assert.Equal(ResearchResult.Rejected, await fence.BeginAsync(reopened, "PEER", token, operation));
            var after = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "multi-device-trust.bin")));
            Assert.True(CryptographicOperations.FixedTimeEquals(before, after));
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    [Fact]
    public async Task RevokedAndRepairedSameIdentityCannotReuseOldOperation()
    {
        var root = NewDirectory(); var protector = new WindowsCredentialProtector();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
        using var fence = new GrantIdentityFence(); var operation = Guid.NewGuid();
        var oldToken = Convert.FromBase64String(await vault.AddConfirmedAsync("peer", new string('A', 64)));
        byte[]? replacement = null;
        try
        {
            Assert.Equal(ResearchResult.Accepted, await fence.SealAsync(vault, "peer", oldToken, operation));
            Assert.True(await vault.RevokeAsync("peer"));
            replacement = Convert.FromBase64String(await vault.AddConfirmedAsync("peer", new string('A', 64)));
            Assert.Equal(ResearchResult.Unauthorized, await fence.SealAsync(vault, "peer", oldToken, operation));
            Assert.Equal(ResearchResult.Rejected, await fence.SealAsync(vault, "peer", replacement, operation));
            Assert.Equal(ResearchResult.Accepted, await fence.SealAsync(vault, "peer", replacement, Guid.NewGuid()));
        }
        finally { CryptographicOperations.ZeroMemory(oldToken); if (replacement is not null) CryptographicOperations.ZeroMemory(replacement); }
    }

    [Fact]
    public async Task UnauthorizedOrWrongPeerCannotRegisterAnyFence()
    {
        var root = NewDirectory(); var protector = new WindowsCredentialProtector();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
        using var fence = new GrantIdentityFence(); var operation = Guid.NewGuid();
        var valid = Convert.FromBase64String(await vault.AddConfirmedAsync("peer", new string('A', 64)));
        var wrong = RandomNumberGenerator.GetBytes(32);
        try
        {
            Assert.Equal(ResearchResult.Unauthorized, await fence.SealAsync(vault, "peer", wrong, operation));
            Assert.Equal(ResearchResult.Unauthorized, await fence.SealAsync(vault, "other", valid, operation));
            Assert.Equal(ResearchResult.Accepted, await fence.SealAsync(vault, "peer", valid, operation));
            Assert.False(fence.ToString()!.Contains(Convert.ToBase64String(valid), StringComparison.Ordinal));
        }
        finally { CryptographicOperations.ZeroMemory(valid); CryptographicOperations.ZeroMemory(wrong); }
    }

    [Fact]
    public async Task RevocationWaitsForAuthenticatedFenceRegistration()
    {
        var root = NewDirectory(); var protector = new WindowsCredentialProtector();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
        using var fence = new GrantIdentityFence();
        var token = Convert.FromBase64String(await vault.AddConfirmedAsync("peer", new string('A', 64)));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fence.ResearchOnlyBeforePublish = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); };
        try
        {
            var registration = Task.Run(() => fence.SealAsync(vault, "peer", token, Guid.NewGuid()));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var revoke = vault.RevokeAsync("peer");
            Assert.False(revoke.IsCompleted);
            release.Set();
            Assert.Equal(ResearchResult.Accepted, await registration);
            Assert.True(await revoke);
            Assert.Equal(ResearchResult.Unauthorized, await fence.SealAsync(vault, "peer", token, Guid.NewGuid()));
        }
        finally { release.Set(); CryptographicOperations.ZeroMemory(token); }
    }

    [Fact]
    public async Task CancellationBeforeTheVaultGateNeverRunsResearchCallback()
    {
        var root = NewDirectory(); var protector = new WindowsCredentialProtector();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
        var token = Convert.FromBase64String(await vault.AddConfirmedAsync("peer", new string('A', 64)));
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var holding = Task.Run(() => vault.TryCommitAuthorizedAsync("peer", token, () =>
        { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var blocked = vault.TryCommitUniqueResearchAuthorizedAsync("peer", token, _ => Interlocked.Increment(ref callbackCount), cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            Assert.Equal(0, Volatile.Read(ref callbackCount));
        }
        finally { release.Set(); await holding; CryptographicOperations.ZeroMemory(token); }
    }

    [Fact]
    public async Task AmbiguousLegacyV1DuplicateCredentialFailsClosed()
    {
        var root = NewDirectory(); var protector = new WindowsCredentialProtector();
        var token = RandomNumberGenerator.GetBytes(32);
        try
        {
            var encoded = Convert.ToBase64String(token);
            var plain = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, devices = new[] {
                new { name = "peer", fingerprint = new string('A', 64), token = encoded, platform = "windows", slot = 1 },
                new { name = "other", fingerprint = new string('B', 64), token = encoded, platform = "windows", slot = 2 }
            } });
            try { await File.WriteAllBytesAsync(Path.Combine(root, "multi-device-trust.bin"), protector.Protect(plain)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            using var fence = new GrantIdentityFence();
            Assert.Equal(ResearchResult.Rejected, await fence.SealAsync(vault, "peer", token, Guid.NewGuid()));
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    [Fact]
    public async Task SamePendingTransactionSurvivesReopenButDoesNotAuthorizeFence()
    {
        var root = NewDirectory(); var protector = new WindowsCredentialProtector();
        var now = DateTimeOffset.UtcNow; var nonce = Guid.NewGuid();
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('B', 64), Nonce = nonce.ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), nonce, Guid.NewGuid(), "host", "peer", new string('A', 64), offer.KeyboardFingerprint, now.AddMinutes(1));
        var candidateIdentity = new TrustedDeviceIdentity("peer", offer.KeyboardFingerprint, "windows", 1);
        using var fence = new GrantIdentityFence();
        byte[] first; Guid stage;
        await using (var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector))
        {
            stage = (await vault.StagePendingGrantAsync(identity, offer, now)).StageId;
            first = await ReadSyntheticPendingTokenAsync(root, protector);
            Assert.Equal(ResearchResult.Unauthorized, await fence.SealAsync(vault, "peer", first, Guid.NewGuid()));
        }
        try
        {
            await using var reopened = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            Assert.Equal(stage, (await reopened.GetPendingGrantMetadataAsync(now))!.StageId);
            var second = await ReadSyntheticPendingTokenAsync(root, protector);
            try
            {
                Assert.True(GrantIdentityFence.ResearchOnlySameCandidate(first, second, candidateIdentity));
                Assert.Equal(ResearchResult.Unauthorized, await fence.SealAsync(reopened, "peer", second, Guid.NewGuid()));
                Assert.Empty(reopened.GetTrustedDevices());
            }
            finally { CryptographicOperations.ZeroMemory(second); }
        }
        finally { CryptographicOperations.ZeroMemory(first); }
    }

    [Fact]
    public async Task SyntheticOldBackupCanResurrectAnEarlierGrant()
    {
        var root = NewDirectory(); var protector = new WindowsCredentialProtector();
        using var fence = new GrantIdentityFence(); var operation = Guid.NewGuid();
        byte[] old; byte[] backup; byte[]? replacement = null;
        await using (var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector))
        {
            old = Convert.FromBase64String(await vault.AddConfirmedAsync("peer", new string('A', 64)));
            Assert.Equal(ResearchResult.Accepted, await fence.SealAsync(vault, "peer", old, operation));
            backup = await File.ReadAllBytesAsync(Path.Combine(root, "multi-device-trust.bin"));
            Assert.True(await vault.RevokeAsync("peer"));
            replacement = Convert.FromBase64String(await vault.AddConfirmedAsync("peer", new string('A', 64)));
        }
        try
        {
            // This intentionally restores only the synthetic fixture, never a user vault.
            await File.WriteAllBytesAsync(Path.Combine(root, "multi-device-trust.bin"), backup);
            await using var restored = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            Assert.Equal(ResearchResult.Accepted, await fence.SealAsync(restored, "peer", old, operation));
            Assert.Equal(ResearchResult.Unauthorized, await fence.SealAsync(restored, "peer", replacement!, Guid.NewGuid()));
        }
        finally { CryptographicOperations.ZeroMemory(old); if (replacement is not null) CryptographicOperations.ZeroMemory(replacement); }
    }

    [Fact]
    public async Task CapacityRefusesNewSealsWithoutLosingExistingFence()
    {
        var root = NewDirectory(); var protector = new WindowsCredentialProtector();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
        using var fence = new GrantIdentityFence();
        var token = Convert.FromBase64String(await vault.AddConfirmedAsync("peer", new string('A', 64)));
        try
        {
            var first = Guid.NewGuid();
            Assert.Equal(ResearchResult.Accepted, await fence.SealAsync(vault, "peer", token, first));
            for (var i = 1; i < 64; i++) Assert.Equal(ResearchResult.Accepted, await fence.SealAsync(vault, "peer", token, Guid.NewGuid()));
            Assert.Equal(ResearchResult.Rejected, await fence.SealAsync(vault, "peer", token, Guid.NewGuid()));
            Assert.Equal(ResearchResult.Rejected, await fence.BeginAsync(vault, "peer", token, first));
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    private static async Task<byte[]> ReadSyntheticPendingTokenAsync(string root, WindowsCredentialProtector protector)
    {
        // This fixture creates the pending record itself; never use on user profiles.
        var ciphertext = await File.ReadAllBytesAsync(Path.Combine(root, "pending-grant.bin"));
        var plain = protector.Unprotect(ciphertext);
        try
        {
            using var json = JsonDocument.Parse(plain);
            return Convert.FromBase64String(json.RootElement.GetProperty("token").GetString()!);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static string NewDirectory()
    {
        var root = Path.GetFullPath(Path.Combine(Path.Combine(Path.GetTempPath(), "nian-link", "synthetic-vault-grant-identity"), "test-" + Guid.NewGuid().ToString("N")));
        for (var path = root; path is not null; path = Path.GetDirectoryName(path))
            if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Research directory contains a link.");
        Directory.CreateDirectory(root);
        return root;
    }
}
