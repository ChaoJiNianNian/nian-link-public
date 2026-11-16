using System.IO;
using System.Security.Cryptography;
using System.Text;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Services;
using Nian.Input.Security;
using NianLink.Windows;

namespace MultiPeerTrust.Tests;

public sealed class PendingPeerInvitationStoreTests
{
    private static MultiPeerInvitation Invitation(string host = "host") => new()
    {
        Host = "192.168.84.2", Port = 24801, KeyboardPort = 24800, DataPort = 24802,
        HostName = host, PeerName = "peer", CertificateSha256 = new string('A', 64),
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    };
    private static PairingSessionIdentity Identity(DateTimeOffset now) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", "peer", new string('A', 64), new string('B', 64), now.AddMinutes(2));

    [Fact]
    public async Task PendingIsProtectedAndSurvivesReopenWithoutChangingActiveInvitation()
    {
        var path = TempPath();
        try
        {
            var now = DateTimeOffset.UtcNow; var old = Invitation("oldhost"); var next = Invitation(); var identity = Identity(now);
            var digest = PeerInvitationStore.ComputePendingDigest(next);
            byte[] oldFile;
            await using (var store = await PeerInvitationStore.OpenAsync(path, new WindowsCredentialProtector()))
            {
                await store.SaveConfirmedAsync(old, old.CertificateSha256);
                oldFile = await File.ReadAllBytesAsync(Path.Combine(path, "peer-invitation.bin"));
                var result = await store.StagePendingAsync(identity, next, "peer", identity.HostFingerprint, digest, now);
                Assert.Equal(identity.TransactionId, result.TransactionId);
                Assert.Equal(digest, result.InvitationDigest);
                Assert.DoesNotContain(next.Token, result.ToString());
                Assert.Equal(old, store.Get());
                Assert.Equal(oldFile, await File.ReadAllBytesAsync(Path.Combine(path, "peer-invitation.bin")));
            }
            var encrypted = await File.ReadAllBytesAsync(Path.Combine(path, "peer-pending-invitation.bin"));
            Assert.DoesNotContain(next.Token, Encoding.UTF8.GetString(encrypted));
            await using var reopened = await PeerInvitationStore.OpenAsync(path, new WindowsCredentialProtector());
            Assert.Equal(old, reopened.Get());
            var pending = await reopened.GetPendingMetadataAsync();
            Assert.NotNull(pending); Assert.Equal(identity.TransactionId, pending.TransactionId); Assert.Equal(digest, pending.InvitationDigest);
        }
        finally { RemoveTemp(path); }
    }

    [Fact]
    public async Task FirstPairingPendingDoesNotBecomeActiveAfterRestart()
    {
        var path = TempPath();
        try
        {
            var now = DateTimeOffset.UtcNow; var value = Invitation(); var id = Identity(now);
            var digest = PeerInvitationStore.ComputePendingDigest(value);
            await using (var store = await PeerInvitationStore.OpenAsync(path, new WindowsCredentialProtector()))
            { await store.StagePendingAsync(id, value, "peer", id.HostFingerprint, digest, now); Assert.Null(store.Get()); }
            await using var reopened = await PeerInvitationStore.OpenAsync(path, new WindowsCredentialProtector());
            Assert.Null(reopened.Get()); Assert.Equal(id.TransactionId, (await reopened.GetPendingMetadataAsync())!.TransactionId);
        }
        finally { RemoveTemp(path); }
    }

    [Fact]
    public async Task StageRequiresExactIdentityAndDigestAndNeverReplacesAnotherPending()
    {
        var path = TempPath();
        try
        {
            var now = DateTimeOffset.UtcNow; var value = Invitation(); var id = Identity(now);
            var digest = PeerInvitationStore.ComputePendingDigest(value);
            await using var store = await PeerInvitationStore.OpenAsync(path, new WindowsCredentialProtector());
            await Assert.ThrowsAnyAsync<Exception>(() => store.StagePendingAsync(id, value, "other", id.HostFingerprint, digest, now));
            await Assert.ThrowsAnyAsync<Exception>(() => store.StagePendingAsync(id, value, "peer", new string('C', 64), digest, now));
            await Assert.ThrowsAnyAsync<Exception>(() => store.StagePendingAsync(id, value, "peer", id.HostFingerprint, new string('D', 64), now));
            await Assert.ThrowsAnyAsync<Exception>(() => store.StagePendingAsync(id with { OfferNonce = Guid.Empty }, value, "peer", id.HostFingerprint, digest, now));
            await Assert.ThrowsAnyAsync<Exception>(() => store.StagePendingAsync(id with { ExpiresAtUtc = now }, value, "peer", id.HostFingerprint, digest, now));
            Assert.Null(await store.GetPendingMetadataAsync());
            var first = await store.StagePendingAsync(id, value, "peer", id.HostFingerprint, digest, now);
            var before = await File.ReadAllBytesAsync(Path.Combine(path, "peer-pending-invitation.bin"));
            Assert.Equal(first, await store.StagePendingAsync(id, value, "peer", id.HostFingerprint, digest, now));
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(path, "peer-pending-invitation.bin")));
            await Assert.ThrowsAnyAsync<Exception>(() => store.StagePendingAsync(id with { TransactionId = Guid.NewGuid() }, value, "peer", id.HostFingerprint, digest, now));
            var replacement = value with { Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            await Assert.ThrowsAnyAsync<Exception>(() => store.StagePendingAsync(id, replacement, "peer", id.HostFingerprint, PeerInvitationStore.ComputePendingDigest(replacement), now));
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(path, "peer-pending-invitation.bin")));
        }
        finally { RemoveTemp(path); }
    }

    [Fact]
    public async Task AbortMustMatchWholeTransactionAndCannotTouchActivePairing()
    {
        var path = TempPath();
        try
        {
            var now = DateTimeOffset.UtcNow; var old = Invitation("oldhost"); var value = Invitation(); var id = Identity(now);
            var digest = PeerInvitationStore.ComputePendingDigest(value);
            await using var store = await PeerInvitationStore.OpenAsync(path, new WindowsCredentialProtector());
            await store.SaveConfirmedAsync(old, old.CertificateSha256);
            await store.StagePendingAsync(id, value, "peer", id.HostFingerprint, digest, now);
            Assert.False(await store.AbortPendingAsync(id with { SessionId = Guid.NewGuid() }, digest));
            Assert.False(await store.AbortPendingAsync(id, new string('F', 64)));
            Assert.NotNull(await store.GetPendingMetadataAsync());
            Assert.True(await store.AbortPendingAsync(id, digest));
            Assert.False(await store.AbortPendingAsync(id, digest));
            Assert.Null(await store.GetPendingMetadataAsync()); Assert.Equal(old, store.Get());
        }
        finally { RemoveTemp(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptOrOversizedPendingIsRetainedAndOldPairingRemainsReadable(bool oversized)
    {
        var path = TempPath();
        try
        {
            var old = Invitation("oldhost");
            await using (var store = await PeerInvitationStore.OpenAsync(path, new WindowsCredentialProtector()))
                await store.SaveConfirmedAsync(old, old.CertificateSha256);
            var pendingFile = Path.Combine(path, "peer-pending-invitation.bin");
            var bytes = oversized ? new byte[65537] : new byte[] { 1, 2, 3 };
            await File.WriteAllBytesAsync(pendingFile, bytes);
            await using var reopened = await PeerInvitationStore.OpenAsync(path, new WindowsCredentialProtector());
            Assert.Equal(old, reopened.Get());
            await Assert.ThrowsAnyAsync<Exception>(() => reopened.GetPendingMetadataAsync());
            var now = DateTimeOffset.UtcNow; var value = Invitation(); var id = Identity(now);
            await Assert.ThrowsAnyAsync<Exception>(() => reopened.StagePendingAsync(id, value, "peer", id.HostFingerprint, PeerInvitationStore.ComputePendingDigest(value), now));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(pendingFile)); Assert.Equal(old, reopened.Get());
        }
        finally { RemoveTemp(path); }
    }

    [Fact]
    public async Task FailedProtectionDoesNotChangeActiveOrPreviouslyStagedData()
    {
        var path = TempPath();
        try
        {
            var protector = new FailingProtector(); var old = Invitation("oldhost"); var value = Invitation(); var now = DateTimeOffset.UtcNow; var id = Identity(now);
            await using var store = await PeerInvitationStore.OpenAsync(path, protector);
            await store.SaveConfirmedAsync(old, old.CertificateSha256);
            protector.Fail = true;
            await Assert.ThrowsAsync<CryptographicException>(() => store.StagePendingAsync(id, value, "peer", id.HostFingerprint, PeerInvitationStore.ComputePendingDigest(value), now));
            Assert.Equal(old, store.Get()); Assert.Null(await store.GetPendingMetadataAsync());
            protector.Fail = false;
            await store.StagePendingAsync(id, value, "peer", id.HostFingerprint, PeerInvitationStore.ComputePendingDigest(value), now);
            var before = await File.ReadAllBytesAsync(Path.Combine(path, "peer-pending-invitation.bin"));
            protector.Fail = true;
            var different = Invitation(); var second = Identity(now);
            await Assert.ThrowsAnyAsync<Exception>(() => store.StagePendingAsync(second, different, "peer", second.HostFingerprint, PeerInvitationStore.ComputePendingDigest(different), now));
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(path, "peer-pending-invitation.bin"))); Assert.Equal(old, store.Get());
        }
        finally { RemoveTemp(path); }
    }

    [Fact]
    public async Task MissingMacKeyWithPendingOnlyNeverCreatesReplacementKeyOrDiscardsData()
    {
        var path = TempPath();
        try
        {
            var vault = new MemoryVault(); byte[] protectedPending;
            using (var protector = await MacMultiPeerProtector.OpenAsync(path, vault)) protectedPending = protector.Protect(Encoding.UTF8.GetBytes("synthetic"));
            var file = Path.Combine(path, "peer-pending-invitation.bin"); await File.WriteAllBytesAsync(file, protectedPending);
            vault.Clear();
            await Assert.ThrowsAsync<CryptographicException>(() => MacMultiPeerProtector.OpenAsync(path, vault));
            Assert.Equal(0, vault.Count); Assert.Equal(protectedPending, await File.ReadAllBytesAsync(file));
        }
        finally { RemoveTemp(path); }
    }

    [Fact]
    public async Task ConcurrentDifferentStagesLeaveOnlyOneWholePendingTransaction()
    {
        var path = TempPath();
        try
        {
            var now = DateTimeOffset.UtcNow; var first = Invitation(); var second = Invitation();
            var firstId = Identity(now); var secondId = Identity(now);
            await using var store = await PeerInvitationStore.OpenAsync(path, new WindowsCredentialProtector());
            async Task<Guid?> Stage(PairingSessionIdentity id, MultiPeerInvitation value)
            {
                try { return (await store.StagePendingAsync(id, value, "peer", id.HostFingerprint, PeerInvitationStore.ComputePendingDigest(value), now)).TransactionId; }
                catch (InvalidOperationException) { return null; }
            }
            var outcomes = await Task.WhenAll(Stage(firstId, first), Stage(secondId, second));
            var winner = Assert.Single(outcomes, x => x.HasValue);
            Assert.Equal(winner, (await store.GetPendingMetadataAsync())!.TransactionId);
            Assert.Null(store.Get());
        }
        finally { RemoveTemp(path); }
    }

    [Fact]
    public async Task PendingPathConflictAndCancelledWritePreserveActivePairing()
    {
        var path = TempPath();
        try
        {
            var now = DateTimeOffset.UtcNow; var old = Invitation("oldhost"); var next = Invitation(); var id = Identity(now);
            var digest = PeerInvitationStore.ComputePendingDigest(next);
            await using var store = await PeerInvitationStore.OpenAsync(path, new WindowsCredentialProtector());
            await store.SaveConfirmedAsync(old, old.CertificateSha256);
            var activeBefore = await File.ReadAllBytesAsync(Path.Combine(path, "peer-invitation.bin"));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StagePendingAsync(id, next, "peer", id.HostFingerprint, digest, now, cancelled.Token));
            Directory.CreateDirectory(Path.Combine(path, "peer-pending-invitation.bin"));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.StagePendingAsync(id, next, "peer", id.HostFingerprint, digest, now));
            Assert.Equal(old, store.Get()); Assert.Equal(activeBefore, await File.ReadAllBytesAsync(Path.Combine(path, "peer-invitation.bin")));
        }
        finally { RemoveTemp(path); }
    }

    [Fact]
    public async Task MacProtectorCanReopenPendingOnlyWithoutActivatingIt()
    {
        var path = TempPath();
        try
        {
            var now = DateTimeOffset.UtcNow; var value = Invitation(); var id = Identity(now); var vault = new MemoryVault();
            using (var protector = await MacMultiPeerProtector.OpenAsync(path, vault))
            await using (var store = await PeerInvitationStore.OpenAsync(path, protector))
                await store.StagePendingAsync(id, value, "peer", id.HostFingerprint, PeerInvitationStore.ComputePendingDigest(value), now);
            using var reopenedProtector = await MacMultiPeerProtector.OpenAsync(path, vault);
            await using var reopened = await PeerInvitationStore.OpenAsync(path, reopenedProtector);
            Assert.Null(reopened.Get()); Assert.Equal(id.TransactionId, (await reopened.GetPendingMetadataAsync())!.TransactionId);
        }
        finally { RemoveTemp(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClockExpiryOrRollbackBeforePublishDoesNotCreatePending(bool rollback)
    {
        var path = TempPath();
        try
        {
            var now = DateTimeOffset.UtcNow; var id = Identity(now); var value = Invitation();
            var clock = new SequenceClock(now, now, rollback ? now.AddSeconds(-1) : id.ExpiresAtUtc);
            await using var store = await PeerInvitationStore.OpenAsync(path, new WindowsCredentialProtector());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.StagePendingAsync(id, value, "peer", id.HostFingerprint,
                PeerInvitationStore.ComputePendingDigest(value), now, clock: clock));
            Assert.Null(await store.GetPendingMetadataAsync()); Assert.Null(store.Get());
            Assert.False(File.Exists(Path.Combine(path, "peer-pending-invitation.bin")));
        }
        finally { RemoveTemp(path); }
    }

    [Fact]
    public async Task ExpiringWhileWaitingForGateCannotReplaceAnotherPending()
    {
        var path = TempPath(); var protector = new BlockingProtector();
        try
        {
            var now = DateTimeOffset.UtcNow; var old = Invitation("oldhost");
            var second = Invitation(); var secondId = Identity(now) with { ExpiresAtUtc = now.AddSeconds(5) };
            var secondClock = new ManualClock(now);
            await using var store = await PeerInvitationStore.OpenAsync(path, protector);
            var oldSave = Task.Run(() => store.SaveConfirmedAsync(old, old.CertificateSha256));
            Assert.True(protector.Entered.Wait(TimeSpan.FromSeconds(5)));
            var secondStage = store.StagePendingAsync(secondId, second, "peer", secondId.HostFingerprint,
                PeerInvitationStore.ComputePendingDigest(second), now, clock: secondClock);
            Assert.False(secondStage.IsCompleted);
            secondClock.UtcNow = secondId.ExpiresAtUtc;
            protector.Release.Set();
            await oldSave.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => secondStage);
            Assert.Null(await store.GetPendingMetadataAsync()); Assert.Equal(old, store.Get());
        }
        finally { protector.Release.Set(); RemoveTemp(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateFieldsInProtectedPendingAreRejectedAndRetained(bool nested)
    {
        var path = TempPath();
        try
        {
            var now = DateTimeOffset.UtcNow; var old = Invitation("oldhost"); var next = Invitation(); var id = Identity(now);
            var protector = new WindowsCredentialProtector();
            await using var store = await PeerInvitationStore.OpenAsync(path, protector);
            await store.SaveConfirmedAsync(old, old.CertificateSha256);
            await store.StagePendingAsync(id, next, "peer", id.HostFingerprint, PeerInvitationStore.ComputePendingDigest(next), now);
            var file = Path.Combine(path, "peer-pending-invitation.bin");
            var plain = protector.Unprotect(await File.ReadAllBytesAsync(file));
            string json;
            try { json = Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var field = $"\"sessionId\":\"{id.SessionId:D}\"";
            var duplicate = nested ? json.Replace(field, field + "," + field, StringComparison.Ordinal) : json.Insert(1, "\"version\":1,");
            Assert.NotEqual(json, duplicate);
            var tampered = protector.Protect(Encoding.UTF8.GetBytes(duplicate));
            await File.WriteAllBytesAsync(file, tampered);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetPendingMetadataAsync());
            Assert.Equal(tampered, await File.ReadAllBytesAsync(file)); Assert.Equal(old, store.Get());
        }
        finally { RemoveTemp(path); }
    }

    [Theory]
    [InlineData("root", "baseInvitationDigest")]
    [InlineData("identity", "peerFingerprint")]
    [InlineData("invitation", "version")]
    [InlineData("invitation", "keyboardPort")]
    [InlineData("invitation", "dataPort")]
    public async Task MissingRequiredPendingFieldsAreRejectedAndRetained(string scope, string field)
    {
        var path = TempPath();
        try
        {
            var now = DateTimeOffset.UtcNow; var old = Invitation("oldhost"); var next = Invitation(); var id = Identity(now);
            var protector = new WindowsCredentialProtector();
            await using var store = await PeerInvitationStore.OpenAsync(path, protector);
            await store.SaveConfirmedAsync(old, old.CertificateSha256);
            await store.StagePendingAsync(id, next, "peer", id.HostFingerprint, PeerInvitationStore.ComputePendingDigest(next), now);
            var file = Path.Combine(path, "peer-pending-invitation.bin");
            var plain = protector.Unprotect(await File.ReadAllBytesAsync(file));
            System.Text.Json.Nodes.JsonObject root;
            try { root = System.Text.Json.Nodes.JsonNode.Parse(plain)!.AsObject(); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var target = scope == "root" ? root : root[scope]!.AsObject();
            Assert.True(target.Remove(field));
            var tampered = protector.Protect(Encoding.UTF8.GetBytes(root.ToJsonString()));
            await File.WriteAllBytesAsync(file, tampered);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetPendingMetadataAsync());
            Assert.Equal(tampered, await File.ReadAllBytesAsync(file)); Assert.Equal(old, store.Get());
        }
        finally { RemoveTemp(path); }
    }

    private sealed class FailingProtector : IMultiDeviceSecretProtector
    {
        private readonly WindowsCredentialProtector inner = new();
        public bool Fail { get; set; }
        public byte[] Protect(byte[] data) => Fail ? throw new CryptographicException("Synthetic failure.") : inner.Protect(data);
        public byte[] Unprotect(byte[] data) => inner.Unprotect(data);
    }
    private sealed class BlockingProtector : IMultiDeviceSecretProtector
    {
        private readonly WindowsCredentialProtector inner = new();
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public byte[] Protect(byte[] data)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return inner.Protect(data);
        }
        public byte[] Unprotect(byte[] data) => inner.Unprotect(data);
    }
    private sealed class ManualClock(DateTimeOffset value) : TimeProvider
    {
        private long ticks = value.UtcTicks;
        public DateTimeOffset UtcNow { set => Interlocked.Exchange(ref ticks, value.UtcTicks); }
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    }
    private sealed class SequenceClock(params DateTimeOffset[] values) : TimeProvider
    {
        private int index;
        public override DateTimeOffset GetUtcNow() => values[Math.Min(Interlocked.Increment(ref index) - 1, values.Length - 1)];
    }
    private sealed class MemoryVault : IIdentityVault
    {
        private readonly Dictionary<string, byte[]> keys = new();
        public int Count => keys.Count;
        public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(keys.TryGetValue(key, out var data) ? data.ToArray() : null);
        public Task StoreAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) { keys.Add(key, value.ToArray()); return Task.CompletedTask; }
        public void Clear() => keys.Clear();
    }
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "nian-pending-test-" + Guid.NewGuid().ToString("N"));
    private static void RemoveTemp(string path)
    {
        var absolute = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(absolute), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(absolute).StartsWith("nian-pending-test-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid test cleanup target.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
}
