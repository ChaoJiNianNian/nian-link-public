using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace MultiPeerTrust.Tests;

public sealed class FrozenPendingGrantTests
{
    private sealed class FailingProtector : IMultiDeviceSecretProtector
    {
        private readonly WindowsCredentialProtector inner = new();
        public bool Fail { get; set; }
        public Action? BeforeProtect { get; set; }
        public byte[] Protect(byte[] value) { if (Fail) throw new CryptographicException("Synthetic failure."); BeforeProtect?.Invoke(); return inner.Protect(value); }
        public byte[] Unprotect(byte[] value) => inner.Unprotect(value);
    }
    private static string Root() => Path.Combine(Path.GetTempPath(), "nian-freeze-grant-" + Guid.NewGuid().ToString("N"));
    private static string Pending(string root) => Path.Combine(root, "pending-grant.bin");
    private static void Clean(string root)
    {
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), temp, comparison) || !Path.GetFileName(full).StartsWith("nian-freeze-grant-", StringComparison.Ordinal) ||
            Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to remove a non-test directory.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
    private static (PairingSessionIdentity Identity, PeerIdentityOffer Offer) Proposal(DateTimeOffset now)
    {
        var nonce = Guid.NewGuid();
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('B', 64), Nonce = nonce.ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), nonce, Guid.NewGuid(), "host", "peer", new string('A', 64), new string('B', 64), now.AddMinutes(1));
        return (identity, offer);
    }
    private static Task<PendingFrozenInvitationMetadata> Freeze(MultiDeviceCredentialVault vault, Guid stageId, PairingSessionIdentity identity,
        DateTimeOffset now, int control = 24801, int keyboard = 24800, int basePort = 24802, CancellationToken ct = default, Func<DateTimeOffset>? clock = null) =>
        vault.FreezePendingInvitationForLoopbackTestAsync(stageId, identity, IPAddress.Loopback, control, keyboard, basePort, now, ct, clock);

    [Fact]
    public async Task FreezeV1PreservesTokenAndMatchesPeerDigestAcrossRestart()
    {
        var root = Root(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now); var protector = new WindowsCredentialProtector();
        try
        {
            string token, digest; Guid stageId;
            await using (var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector))
            {
                var old = await vault.AddConfirmedAsync("old", new string('C', 64));
                var staged = await vault.StagePendingGrantAsync(identity, offer, now); stageId = staged.StageId;
                var oldActive = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "multi-device-trust.bin")));
                var before = protector.Unprotect(await File.ReadAllBytesAsync(Pending(root)));
                try { using var parsed = JsonDocument.Parse(before); Assert.Equal(1, parsed.RootElement.GetProperty("version").GetInt32()); token = parsed.RootElement.GetProperty("token").GetString()!; }
                finally { CryptographicOperations.ZeroMemory(before); }
                var frozen = await Freeze(vault, stageId, identity, now);
                Assert.Equal(2, frozen.Slot); Assert.Equal(24803, frozen.DataPort); Assert.Equal("127.0.0.1", frozen.Host);
                var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24801, KeyboardPort = 24800, DataPort = 24803, HostName = "host", PeerName = "peer", CertificateSha256 = identity.HostFingerprint, Token = token };
                digest = PeerInvitationStore.ComputePendingDigest(invitation);
                Assert.Equal(digest, frozen.InvitationDigest);
                await using (var peerStore = await PeerInvitationStore.OpenAsync(Path.Combine(root, "peer-isolated"), protector))
                {
                    var peerPending = await peerStore.StagePendingAsync(identity, invitation, "peer", identity.HostFingerprint, digest, now);
                    Assert.Equal(digest, peerPending.InvitationDigest);
                    Assert.Null(peerStore.Get());
                }
                Assert.DoesNotContain(token, frozen.ToString());
                Assert.DoesNotContain(frozen.Host, frozen.ToString());
                Assert.False(vault.IsAuthorized("peer", token)); Assert.True(vault.IsAuthorized("old", old));
                Assert.Equal(oldActive, SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "multi-device-trust.bin"))));
                Assert.Equal(frozen, await Freeze(vault, stageId, identity, now));
            }
            await using (var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector))
            {
                var restored = await vault.GetPendingGrantMetadataAsync(now);
                Assert.NotNull(restored); Assert.Equal(digest, restored.Frozen!.InvitationDigest);
                Assert.Equal(2, restored.Frozen.Slot); Assert.Equal(24803, restored.Frozen.DataPort);
                Assert.Equal(digest, (await Freeze(vault, stageId, identity, now)).InvitationDigest);
                Assert.False(vault.IsAuthorized("peer", token));
            }
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ChangesToIdentityStageOrPortsAreRejectedWithoutReplacingFrozenRecord()
    {
        var root = Root(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new WindowsCredentialProtector());
            var staged = await vault.StagePendingGrantAsync(identity, offer, now);
            var first = await Freeze(vault, staged.StageId, identity, now);
            var original = await File.ReadAllBytesAsync(Pending(root));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Freeze(vault, Guid.NewGuid(), identity, now));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Freeze(vault, staged.StageId, identity with { OfferNonce = Guid.NewGuid() }, now));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Freeze(vault, staged.StageId, identity with { HostFingerprint = new string('D', 64) }, now));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Freeze(vault, staged.StageId, identity, now, control: 24901));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Freeze(vault, staged.StageId, identity, now, keyboard: 24900));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Freeze(vault, staged.StageId, identity, now, basePort: 24902));
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.FreezePendingInvitationForLoopbackTestAsync(staged.StageId, identity, IPAddress.Parse("127.0.0.2"), 24801, 24800, 24802, now));
            await Assert.ThrowsAsync<ArgumentException>(() => vault.FreezePendingInvitationForLoopbackTestAsync(staged.StageId, identity, IPAddress.Parse("192.168.44.2"), 24801, 24800, 24802, now));
            Assert.Equal(original, await File.ReadAllBytesAsync(Pending(root)));
            Assert.Equal(first, (await vault.GetPendingGrantMetadataAsync(now))!.Frozen);
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task FreezeUpperDataPortBoundaryKeepsOriginalSlot()
    {
        var root = Root(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new WindowsCredentialProtector());
            for (var i = 1; i <= 3; i++) await vault.AddConfirmedAsync("pc" + i, i.ToString("X64"));
            var staged = await vault.StagePendingGrantAsync(identity, offer, now);
            var frozen = await Freeze(vault, staged.StageId, identity, now, 65530, 65531, 65532);
            Assert.Equal(4, frozen.Slot); Assert.Equal(65535, frozen.DataPort);
            Assert.Equal(3, vault.GetTrustedDevices().Count);
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task FailedReplacementOrCancellationKeepsOriginalV1Pending()
    {
        var root = Root(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            var protector = new FailingProtector();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var old = await vault.AddConfirmedAsync("old", new string('C', 64));
            var staged = await vault.StagePendingGrantAsync(identity, offer, now);
            var original = await File.ReadAllBytesAsync(Pending(root));
            using (var held = new FileStream(Pending(root), FileMode.Open, FileAccess.Read, FileShare.Read))
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Freeze(vault, staged.StageId, identity, now));
            Assert.Equal(original, await File.ReadAllBytesAsync(Pending(root)));
            Assert.Empty(Directory.GetFiles(root, "pending-grant.bin.*.tmp"));
            using var cancel = new CancellationTokenSource();
            protector.BeforeProtect = cancel.Cancel;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Freeze(vault, staged.StageId, identity, now, ct: cancel.Token));
            protector.BeforeProtect = null;
            Assert.Equal(original, await File.ReadAllBytesAsync(Pending(root)));
            Assert.True(vault.IsAuthorized("old", old)); Assert.Null((await vault.GetPendingGrantMetadataAsync(now))!.Frozen);
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ChangedPendingDocumentDuringFreezeIsNotOverwritten()
    {
        var root = Root(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            var protector = new FailingProtector();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var staged = await vault.StagePendingGrantAsync(identity, offer, now);
            var replacement = new byte[] { 1, 2, 3, 4 };
            protector.BeforeProtect = () => File.WriteAllBytes(Pending(root), replacement);
            await Assert.ThrowsAnyAsync<CryptographicException>(() => Freeze(vault, staged.StageId, identity, now));
            protector.BeforeProtect = null;
            Assert.Equal(replacement, await File.ReadAllBytesAsync(Pending(root)));
            Assert.Empty(Directory.GetFiles(root, "pending-grant.bin.*.tmp"));
            Assert.Empty(vault.GetTrustedDevices());
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ConcurrentDifferentFreezesCannotReplaceTheWinningConfiguration()
    {
        var root = Root(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new WindowsCredentialProtector());
            var staged = await vault.StagePendingGrantAsync(identity, offer, now);
            using var start = new ManualResetEventSlim();
            async Task<PendingFrozenInvitationMetadata?> Attempt(int control)
            {
                start.Wait();
                try { return await Freeze(vault, staged.StageId, identity, now, control: control); }
                catch (InvalidOperationException) { return null; }
            }
            var first = Task.Run(() => Attempt(24801)); var second = Task.Run(() => Attempt(24901));
            start.Set(); var results = await Task.WhenAll(first, second);
            var winner = Assert.Single(results, x => x is not null);
            Assert.Equal(winner, (await vault.GetPendingGrantMetadataAsync(now))!.Frozen);
            Assert.Empty(vault.GetTrustedDevices());
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ExpirationWhileWaitingForVaultGateCannotRewriteOrReturnFrozenData()
    {
        var root = Root(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now); long ticks = now.UtcTicks;
        try
        {
            var protector = new FailingProtector();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var staged = await vault.StagePendingGrantAsync(identity, offer, now, clock: () => now);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            protector.BeforeProtect = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); };
            var first = Task.Run(() => Freeze(vault, staged.StageId, identity, now, clock: () => now));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var second = Freeze(vault, staged.StageId, identity, now, clock: () => new(Interlocked.Read(ref ticks), TimeSpan.Zero));
                Assert.False(second.IsCompleted);
                Interlocked.Exchange(ref ticks, identity.ExpiresAtUtc.UtcTicks);
                release.Set();
                var winner = await first.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAnyAsync<ArgumentException>(() => second);
                Assert.Equal(winner, (await vault.GetPendingGrantMetadataAsync(now))!.Frozen);
            }
            finally { release.Set(); }
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task InvalidPortRangesConflictsOrUnavailableBindingNeverFreeze()
    {
        var root = Root(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now);
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new WindowsCredentialProtector());
            var staged = await vault.StagePendingGrantAsync(identity, offer, now);
            var original = await File.ReadAllBytesAsync(Pending(root));
            foreach (var ports in new[] { (0, 24800, 24802), (24801, 24801, 24802), (24802, 24800, 24802), (24801, 24804, 24802), (24801, 24800, 0), (24801, 24800, 65533) })
                await Assert.ThrowsAsync<ArgumentException>(() => Freeze(vault, staged.StageId, identity, now, ports.Item1, ports.Item2, ports.Item3));
            var address = IPAddress.Parse("192.168.44.2");
            var inactive = VerifiedLanBinding.FromSnapshots(address, true, [new LanInterfaceSnapshot("physical", "test", NetworkInterfaceType.Ethernet, OperationalStatus.Up, [address])]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.FreezePendingInvitationAsync(staged.StageId, identity, inactive, 24801, 24800, 24802, now));
            Assert.Equal(original, await File.ReadAllBytesAsync(Pending(root)));
            var competing = await vault.AddConfirmedAsync("other", new string('C', 64));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Freeze(vault, staged.StageId, identity, now));
            Assert.True(vault.IsAuthorized("other", competing));
            Assert.Equal(original, await File.ReadAllBytesAsync(Pending(root)));
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task FreezeFailedProtectionOrExpiryPreservesV1AndExistingTrust()
    {
        var root = Root(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now); long ticks = now.UtcTicks;
        try
        {
            var protector = new FailingProtector();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var old = await vault.AddConfirmedAsync("old", new string('C', 64));
            var staged = await vault.StagePendingGrantAsync(identity, offer, now, clock: () => now);
            var before = await File.ReadAllBytesAsync(Pending(root));
            protector.Fail = true;
            await Assert.ThrowsAsync<CryptographicException>(() => Freeze(vault, staged.StageId, identity, now));
            protector.Fail = false;
            Assert.Equal(before, await File.ReadAllBytesAsync(Pending(root)));
            var protectionReached = false;
            protector.BeforeProtect = () => { protectionReached = true; Interlocked.Exchange(ref ticks, identity.ExpiresAtUtc.UtcTicks); };
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Freeze(vault, staged.StageId, identity, now, clock: () => new(Interlocked.Read(ref ticks), TimeSpan.Zero)));
            Assert.True(protectionReached);
            protector.BeforeProtect = null;
            Assert.Equal(before, await File.ReadAllBytesAsync(Pending(root))); Assert.True(vault.IsAuthorized("old", old));
            Assert.Null((await vault.GetPendingGrantMetadataAsync(now))!.Frozen);
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task CorruptedFrozenDigestOrMissingFieldsCannotDowngradeOrChangeEvidence()
    {
        var root = Root(); var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(now); var protector = new WindowsCredentialProtector();
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var staged = await vault.StagePendingGrantAsync(identity, offer, now);
            var frozen = await Freeze(vault, staged.StageId, identity, now);
            var original = await File.ReadAllBytesAsync(Pending(root)); var plain = protector.Unprotect(original);
            try
            {
                var json = Encoding.UTF8.GetString(plain);
                var changes = new[] { json.Replace(frozen.InvitationDigest, new string('0', 64), StringComparison.Ordinal), json.Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal), json.Replace("\"dataPortBase\":24802,", "", StringComparison.Ordinal), json.Replace("\"dataPort\":24802", "\"dataPort\":24803", StringComparison.Ordinal) };
                foreach (var change in changes)
                {
                    Assert.NotEqual(json, change);
                    var bytes = Encoding.UTF8.GetBytes(change); byte[] encrypted;
                    try { encrypted = protector.Protect(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
                    await File.WriteAllBytesAsync(Pending(root), encrypted);
                    await Assert.ThrowsAsync<InvalidDataException>(() => vault.GetPendingGrantMetadataAsync(now));
                    Assert.Equal(encrypted, await File.ReadAllBytesAsync(Pending(root)));
                }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { Clean(root); }
    }
}
