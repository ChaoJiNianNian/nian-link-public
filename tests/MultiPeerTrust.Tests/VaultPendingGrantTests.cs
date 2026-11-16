using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace MultiPeerTrust.Tests;

public sealed class VaultPendingGrantTests
{
    private sealed class FailingProtector : IMultiDeviceSecretProtector
    {
        private readonly WindowsCredentialProtector inner = new();
        public bool Fail { get; set; }
        public Action? BeforeProtect { get; set; }
        public Action? BeforeUnprotect { get; set; }
        public byte[] Protect(byte[] plaintext)
        {
            if (Fail) throw new CryptographicException("Synthetic protect failure.");
            BeforeProtect?.Invoke();
            return inner.Protect(plaintext);
        }
        public byte[] Unprotect(byte[] ciphertext) { BeforeUnprotect?.Invoke(); return inner.Unprotect(ciphertext); }
    }
    private static (PairingSessionIdentity Identity, PeerIdentityOffer Offer) Proposal(DateTimeOffset now, string name = "peer", char fingerprint = 'B')
    {
        var nonce = Guid.NewGuid();
        var offer = new PeerIdentityOffer { Name = name, Platform = "windows", KeyboardFingerprint = new string(fingerprint, 64), Nonce = nonce.ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), nonce, Guid.NewGuid(), "host", name, new string('A', 64), new string(fingerprint, 64), now.AddMinutes(1));
        return (identity, offer);
    }
    private static string DirectoryPath() => Path.Combine(Path.GetTempPath(), "nian-stage-grant-" + Guid.NewGuid().ToString("N"));
    private static string PendingPath(string root) => Path.Combine(root, "pending-grant.bin");
    private static void Clean(string root)
    {
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), temp, comparison) ||
            !Path.GetFileName(full).StartsWith("nian-stage-grant-", StringComparison.Ordinal) ||
            Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to remove a non-test directory.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

    [Fact]
    public async Task StageIsProtectedAndNeverAuthorizesOrChangesExistingGrantAcrossRestart()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            string old; string token; byte[] activeHash;
            var protector = new WindowsCredentialProtector();
            await using (var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector))
            {
                old = await vault.AddConfirmedAsync("old", new string('C', 64));
                activeHash = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "multi-device-trust.bin")));
                var metadata = await vault.StagePendingGrantAsync(identity, offer, now);
                Assert.Equal(2, metadata.Slot); Assert.False(metadata.HasConflict); Assert.False(metadata.IsExpired);
                var encrypted = await File.ReadAllBytesAsync(PendingPath(root));
                var plain = protector.Unprotect(encrypted);
                try
                {
                    using var json = JsonDocument.Parse(plain);
                    token = json.RootElement.GetProperty("token").GetString()!;
                    Assert.Equal(32, Convert.FromBase64String(token).Length);
                    Assert.DoesNotContain(token, Encoding.UTF8.GetString(encrypted));
                    Assert.DoesNotContain(token, metadata.ToString());
                    Assert.False(vault.IsAuthorized("peer", token));
                    Assert.True(vault.IsAuthorized("old", old));
                    Assert.Single(vault.GetTrustedDevices());
                    Assert.Equal(activeHash, SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "multi-device-trust.bin"))));
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            await using (var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector))
            {
                var restored = await vault.GetPendingGrantMetadataAsync(now);
                Assert.NotNull(restored); Assert.Equal(identity.TransactionId, restored.TransactionId); Assert.Equal(2, restored.Slot);
                Assert.False(vault.IsAuthorized("peer", token)); Assert.True(vault.IsAuthorized("old", old));
                Assert.False(await vault.AbortPendingGrantAsync(restored.StageId, identity with { SessionId = Guid.NewGuid() }));
                Assert.False(await vault.AbortPendingGrantAsync(restored.StageId, identity with { OfferNonce = Guid.NewGuid() }));
                Assert.False(await vault.AbortPendingGrantAsync(restored.StageId, identity with { PeerFingerprint = new string('D', 64) }));
                Assert.NotNull(await vault.GetPendingGrantMetadataAsync(now));
                Assert.True(await vault.AbortPendingGrantAsync(restored.StageId, identity));
                Assert.Null(await vault.GetPendingGrantMetadataAsync(now)); Assert.True(vault.IsAuthorized("old", old));
            }
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task SlotAndIdentityChangesAreReportedAsConflictsAndNeverAlterExistingGrants()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new WindowsCredentialProtector());
            await vault.AddConfirmedAsync("one", new string('1', 64));
            var staged = await vault.StagePendingGrantAsync(identity, offer, now);
            Assert.Equal(2, staged.Slot);
            var other = await vault.AddConfirmedWithSlotAsync("another", new string('2', 64), "windows");
            Assert.Equal(staged.Slot, other.Slot);
            var conflicted = await vault.GetPendingGrantMetadataAsync(now);
            Assert.NotNull(conflicted); Assert.True(conflicted.HasConflict);
            Assert.True(vault.IsAuthorized("another", other.Token));
            Assert.True(await vault.AbortPendingGrantAsync(staged.StageId, identity));
            var (sameName, sameOffer) = Proposal(now, "another", '3');
            await Assert.ThrowsAsync<ArgumentException>(() => vault.StagePendingGrantAsync(sameName, sameOffer, now));
            var (sameFinger, sameFingerOffer) = Proposal(now, "different", '2');
            await Assert.ThrowsAsync<ArgumentException>(() => vault.StagePendingGrantAsync(sameFinger, sameFingerOffer, now));
            Assert.True(vault.IsAuthorized("another", other.Token));
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task FullCapacityOrMismatchedAndExpiredBindingsCannotCreatePending()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new WindowsCredentialProtector());
            await Assert.ThrowsAsync<ArgumentException>(() => vault.StagePendingGrantAsync(identity with { OfferNonce = Guid.NewGuid() }, offer, now));
            await Assert.ThrowsAsync<ArgumentException>(() => vault.StagePendingGrantAsync(identity, offer with { Name = "wrong" }, now));
            await Assert.ThrowsAsync<ArgumentException>(() => vault.StagePendingGrantAsync(identity, offer with { KeyboardFingerprint = new string('D', 64) }, now));
            await Assert.ThrowsAsync<ArgumentException>(() => vault.StagePendingGrantAsync(identity, offer, identity.ExpiresAtUtc));
            foreach (var i in Enumerable.Range(1, 4)) await vault.AddConfirmedAsync("pc" + i, i.ToString("X64"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.StagePendingGrantAsync(identity, offer, now));
            Assert.Null(await vault.GetPendingGrantMetadataAsync(now)); Assert.Equal(4, vault.GetTrustedDevices().Count);
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task FailedProtectionOrMalformedProtectedFilePreservesActiveAndOriginalEvidence()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            var protector = new FailingProtector();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var old = await vault.AddConfirmedAsync("old", new string('C', 64));
            protector.Fail = true;
            await Assert.ThrowsAsync<CryptographicException>(() => vault.StagePendingGrantAsync(identity, offer, now));
            protector.Fail = false;
            Assert.Null(await vault.GetPendingGrantMetadataAsync(now)); Assert.True(vault.IsAuthorized("old", old));
            await vault.StagePendingGrantAsync(identity, offer, now);
            var bytes = await File.ReadAllBytesAsync(PendingPath(root)); bytes[^1] ^= 0x20; await File.WriteAllBytesAsync(PendingPath(root), bytes);
            await Assert.ThrowsAnyAsync<CryptographicException>(() => vault.GetPendingGrantMetadataAsync(now));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(PendingPath(root)));
            await Assert.ThrowsAnyAsync<CryptographicException>(() => vault.StagePendingGrantAsync(identity, offer, now));
            Assert.True(vault.IsAuthorized("old", old)); Assert.Single(vault.GetTrustedDevices());
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ConcurrentStagesAllowOnlyOneTransactionAndDoNotAffectExistingTrust()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow;
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new WindowsCredentialProtector());
            var old = await vault.AddConfirmedAsync("old", new string('C', 64));
            var proposals = Enumerable.Range(0, 12).Select(i => Proposal(now, "peer" + i, 'B')).ToArray();
            var results = await Task.WhenAll(proposals.Select(async x =>
            {
                try { return await vault.StagePendingGrantAsync(x.Identity, x.Offer, now); }
                catch (InvalidOperationException) { return null; }
            }));
            var only = Assert.Single(results, x => x is not null);
            Assert.Equal(only!.TransactionId, (await vault.GetPendingGrantMetadataAsync(now))!.TransactionId);
            Assert.True(vault.IsAuthorized("old", old)); Assert.Single(vault.GetTrustedDevices());
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task OldStageCannotAbortANewStageEvenWhenTransactionIsReused()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new WindowsCredentialProtector());
            var first = await vault.StagePendingGrantAsync(identity, offer, now);
            Assert.True(await vault.AbortPendingGrantAsync(first.StageId, identity));
            var second = await vault.StagePendingGrantAsync(identity, offer, now);
            Assert.NotEqual(first.StageId, second.StageId);
            Assert.False(await vault.AbortPendingGrantAsync(first.StageId, identity));
            Assert.Equal(second.StageId, (await vault.GetPendingGrantMetadataAsync(now))!.StageId);
            Assert.True(await vault.AbortPendingGrantAsync(second.StageId, identity));
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task AbortingOldStageCannotRemoveARevokedAndRepairedNamesake()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new WindowsCredentialProtector());
            var staged = await vault.StagePendingGrantAsync(identity, offer, now);
            await vault.AddConfirmedAsync("peer", new string('1', 64));
            Assert.True(await vault.RevokeAsync("peer"));
            var replacement = await vault.AddConfirmedAsync("peer", new string('2', 64));
            Assert.True((await vault.GetPendingGrantMetadataAsync(now))!.HasConflict);
            Assert.True(await vault.AbortPendingGrantAsync(staged.StageId, identity));
            Assert.True(vault.IsAuthorized("peer", replacement));
            Assert.Equal(new string('2', 64), Assert.Single(vault.GetTrustedDevices()).Fingerprint);
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task OversizedProtectedRecordFailsBeforeDecryptionAndIsPreserved()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow;
        try
        {
            var protector = new FailingProtector();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var oversized = new byte[65537]; RandomNumberGenerator.Fill(oversized);
            await File.WriteAllBytesAsync(PendingPath(root), oversized);
            var decryptAttempted = false;
            protector.BeforeUnprotect = () => decryptAttempted = true;
            await Assert.ThrowsAsync<InvalidDataException>(() => vault.GetPendingGrantMetadataAsync(now));
            Assert.False(decryptAttempted);
            Assert.Equal(oversized, await File.ReadAllBytesAsync(PendingPath(root)));
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task FailedFileMoveLeavesActiveVaultIntactAndNoPendingFile()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            var protector = new FailingProtector();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var old = await vault.AddConfirmedAsync("old", new string('C', 64));
            var previous = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "multi-device-trust.bin")));
            protector.BeforeProtect = () => Directory.CreateDirectory(PendingPath(root));
            await Assert.ThrowsAnyAsync<IOException>(() => vault.StagePendingGrantAsync(identity, offer, now));
            protector.BeforeProtect = null;
            Assert.False(File.Exists(PendingPath(root)));
            Assert.Empty(Directory.GetFiles(root, "pending-grant.bin.*.tmp"));
            Assert.Equal(previous, SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "multi-device-trust.bin"))));
            Assert.True(vault.IsAuthorized("old", old));
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ExpiredOrRolledBackClockReportsPendingButDoesNotActivateOrEraseIt()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new WindowsCredentialProtector());
            var staged = await vault.StagePendingGrantAsync(identity, offer, now);
            Assert.True((await vault.GetPendingGrantMetadataAsync(identity.ExpiresAtUtc))!.IsExpired);
            Assert.True((await vault.GetPendingGrantMetadataAsync(now.AddSeconds(-1)))!.IsExpired);
            Assert.Empty(vault.GetTrustedDevices()); Assert.True(File.Exists(PendingPath(root)));
            Assert.True(await vault.AbortPendingGrantAsync(staged.StageId, identity));
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ProtectedPendingDocumentRejectsMissingDuplicateAndUnknownFieldsWithoutErasingEvidence()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            var protector = new WindowsCredentialProtector();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            await vault.StagePendingGrantAsync(identity, offer, now);
            var original = await File.ReadAllBytesAsync(PendingPath(root));
            var plain = protector.Unprotect(original);
            try
            {
                var json = Encoding.UTF8.GetString(plain);
                var malformed = new[]
                {
                    json.Replace("\"version\":1,", "", StringComparison.Ordinal),
                    json.Insert(1, "\"version\":1,"),
                    json.Insert(1, "\"extra\":true,"),
                    json.Replace("\"slot\":1", "\"slot\":0", StringComparison.Ordinal)
                };
                foreach (var changed in malformed)
                {
                    var bytes = Encoding.UTF8.GetBytes(changed);
                    byte[] protectedBytes;
                    try { protectedBytes = protector.Protect(bytes); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                    await File.WriteAllBytesAsync(PendingPath(root), protectedBytes);
                    await Assert.ThrowsAsync<InvalidDataException>(() => vault.GetPendingGrantMetadataAsync(now));
                    Assert.Equal(protectedBytes, await File.ReadAllBytesAsync(PendingPath(root)));
                    Assert.Empty(vault.GetTrustedDevices());
                }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ConcurrentNormalAddAndStagingNeverOverwriteOrMisreportSlotConflict()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new WindowsCredentialProtector());
            var stagedTask = vault.StagePendingGrantAsync(identity, offer, now);
            var addTask = vault.AddConfirmedWithSlotAsync("other", new string('C', 64), "windows");
            var staged = await stagedTask; var added = await addTask;
            var metadata = await vault.GetPendingGrantMetadataAsync(now);
            Assert.NotNull(metadata);
            Assert.Equal(staged.Slot == added.Slot, metadata.HasConflict);
            Assert.True(vault.IsAuthorized("other", added.Token));
            Assert.Single(vault.GetTrustedDevices());
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ExpirationWhileWaitingForVaultGateCannotPublishPendingGrant()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow;
        var (firstIdentity, firstOffer) = Proposal(now, "first", '1');
        var (secondIdentity, secondOffer) = Proposal(now, "second", '2');
        long ticks = now.UtcTicks;
        try
        {
            var protector = new FailingProtector();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            protector.BeforeProtect = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); throw new CryptographicException("Synthetic stopped writer."); };
            var first = Task.Run(() => vault.StagePendingGrantAsync(firstIdentity, firstOffer, now, clock: () => new(Interlocked.Read(ref ticks), TimeSpan.Zero)));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var second = vault.StagePendingGrantAsync(secondIdentity, secondOffer, now, clock: () => new(Interlocked.Read(ref ticks), TimeSpan.Zero));
                Assert.False(second.IsCompleted);
                Interlocked.Exchange(ref ticks, secondIdentity.ExpiresAtUtc.UtcTicks);
                release.Set();
                await Assert.ThrowsAsync<CryptographicException>(() => first);
                await Assert.ThrowsAsync<ArgumentException>(() => second);
                Assert.False(File.Exists(PendingPath(root)));
                Assert.Empty(vault.GetTrustedDevices());
            }
            finally { release.Set(); }
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ExpiryOrClockRollbackDuringProtectionAndCancellationNeverPublish()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var scenario in new[] { "expiry", "rollback", "cancel" })
        {
            var root = DirectoryPath(); var (identity, offer) = Proposal(now); long ticks = now.UtcTicks;
            try
            {
                var protector = new FailingProtector();
                await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
                var old = await vault.AddConfirmedAsync("old", new string('C', 64));
                using var cancel = new CancellationTokenSource();
                protector.BeforeProtect = () =>
                {
                    if (scenario == "expiry") Interlocked.Exchange(ref ticks, identity.ExpiresAtUtc.UtcTicks);
                    else if (scenario == "rollback") Interlocked.Exchange(ref ticks, now.AddSeconds(-1).UtcTicks);
                    else cancel.Cancel();
                };
                if (scenario == "cancel")
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.StagePendingGrantAsync(identity, offer, now, cancel.Token, () => new(Interlocked.Read(ref ticks), TimeSpan.Zero)));
                else
                    await Assert.ThrowsAnyAsync<ArgumentException>(() => vault.StagePendingGrantAsync(identity, offer, now, clock: () => new(Interlocked.Read(ref ticks), TimeSpan.Zero)));
                Assert.False(File.Exists(PendingPath(root)));
                Assert.Empty(Directory.GetFiles(root, "pending-grant.bin.*.tmp"));
                Assert.True(vault.IsAuthorized("old", old));
            }
            finally { Clean(root); }
        }
    }

    [Fact]
    public async Task CancellationAfterReadingBeforeAbortPreservesPendingGrant()
    {
        var root = DirectoryPath(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            var protector = new FailingProtector();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var stage = await vault.StagePendingGrantAsync(identity, offer, now);
            var bytes = await File.ReadAllBytesAsync(PendingPath(root));
            using var cancel = new CancellationTokenSource();
            protector.BeforeUnprotect = () => cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.AbortPendingGrantAsync(stage.StageId, identity, cancel.Token));
            protector.BeforeUnprotect = null;
            Assert.Equal(bytes, await File.ReadAllBytesAsync(PendingPath(root)));
            Assert.NotNull(await vault.GetPendingGrantMetadataAsync(now));
            Assert.Empty(vault.GetTrustedDevices());
        }
        finally { Clean(root); }
    }
}
