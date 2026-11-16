using System.Net;
using System.Net.NetworkInformation;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed class DiscoveryReviewLeaseTests
{
    private static DiscoveryNetworkBinding SyntheticBinding()
    {
        var address = IPAddress.Parse("192.168.20.10");
        var adapter = new DiscoveryNetworkInterface("synthetic", "test", NetworkInterfaceType.Ethernet, OperationalStatus.Up,
            12, address, IPAddress.Parse("255.255.255.0"));
        return DiscoveryNetworkBinding.FromSnapshotsForTest(address, () => [adapter]);
    }

    [Fact]
    public async Task SyntheticLeaseCannotEnterPublicDiscoveryReview()
    {
        var binding = SyntheticBinding();
        Assert.Throws<InvalidOperationException>(() => DiscoveryReviewLease.Start(binding));
        var lease = DiscoveryReviewLease.ForTest(binding, new FakeClock());
        Assert.False(lease.HasLiveNetworkOrigin);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task RevocationAndStaleObservationArePermanent()
    {
        var clock = new FakeClock();
        await using var revoked = DiscoveryReviewLease.ForTest(SyntheticBinding(), clock);
        Assert.True(revoked.IsFresh);
        Assert.True(revoked.TryRevoke());
        Assert.False(revoked.IsFresh);
        revoked.RecordForTest(true);
        Assert.False(revoked.IsFresh);

        await using var stale = DiscoveryReviewLease.ForTest(SyntheticBinding(), clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(stale.IsFresh);
        stale.RecordForTest(true);
        Assert.False(stale.IsFresh);

        await using var stalled = DiscoveryReviewLease.ForTest(SyntheticBinding(), clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        stalled.RecordForTest(true);
        Assert.False(stalled.IsFresh);
    }

    [Fact]
    public async Task FailedMonitorAndTimestampRollbackFailClosed()
    {
        var clock = new FakeClock();
        await using var failed = DiscoveryReviewLease.ForTest(SyntheticBinding(), clock);
        failed.RecordForTest(false);
        Assert.False(failed.IsFresh);
        await using var rollback = DiscoveryReviewLease.ForTest(SyntheticBinding(), clock);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        rollback.RecordForTest(true);
        clock.Advance(TimeSpan.FromSeconds(-1));
        Assert.False(rollback.IsFresh);
    }

    [Fact]
    public void SlowInitialHardwareQueryCannotStartFreshLease()
    {
        var clock = new FakeClock();
        Assert.Throws<InvalidOperationException>(() => DiscoveryReviewLease.StartForTest(SyntheticBinding(), clock,
            () => { clock.Advance(TimeSpan.FromSeconds(2)); return true; }));
    }

    [Fact]
    public async Task MonitorFreshnessStartsAtQueryStartRatherThanCompletion()
    {
        var clock = new FakeClock();
        await using var lease = DiscoveryReviewLease.ForTest(SyntheticBinding(), clock);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var started = clock.GetTimestamp();
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        lease.RecordForTest(true, started);
        Assert.True(lease.IsFresh);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(lease.IsFresh);
    }

    [Fact]
    public async Task RevokeAndPublicationClaimAreMutuallyExclusive()
    {
        await using var cancelled = DiscoveryReviewLease.ForTest(SyntheticBinding(), new FakeClock());
        Assert.True(cancelled.TryRevoke());
        Assert.Throws<InvalidOperationException>(cancelled.ClaimForTest);
        await using var claimed = DiscoveryReviewLease.ForTest(SyntheticBinding(), new FakeClock());
        claimed.ClaimForTest();
        Assert.False(claimed.TryRevoke());
        Assert.Throws<InvalidOperationException>(claimed.ClaimForTest);
    }

    [Fact]
    public async Task RevocationDuringProtectionPreventsTrustPublication()
    {
        using var folder = new TestFolder();
        using var protector = new BlockingProtector();
        var now = DateTimeOffset.UtcNow;
        var lease = DiscoveryReviewLease.ForTest(SyntheticBinding(), new FakeClock());
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protector);
        var old = await vault.AddConfirmedAsync("old", new string('1', 64));
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var offer = new PeerIdentityOffer { Name = "device", Platform = "windows", KeyboardFingerprint = new string('A', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(10) };
        Assert.Throws<InvalidOperationException>(() => pairing.ReviewForDiscovery(offer.ToJson(now), now, lease));
        var review = pairing.ReviewForDiscoveryForTest(offer.ToJson(now), now, lease);
        protector.Block = true;
        var pending = Task.Run(() => pairing.ConfirmAsync(review.Id, review.KeyboardFingerprint, now));
        try
        {
            Assert.True(protector.Started.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(lease.TryRevoke());
        }
        finally { protector.Release.Set(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.True(vault.IsAuthorized("old", old));
        Assert.Single(vault.GetTrustedDevices());
        await lease.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitorFailureOrStalenessDuringProtectionPreventsTrustPublication(bool stale)
    {
        using var folder = new TestFolder();
        using var protector = new BlockingProtector();
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeClock();
        await using var lease = DiscoveryReviewLease.ForTest(SyntheticBinding(), clock);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protector);
        var old = await vault.AddConfirmedAsync("old", new string('1', 64));
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var offer = new PeerIdentityOffer { Name = "device", Platform = "windows", KeyboardFingerprint = new string('A', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(10) };
        var review = pairing.ReviewForDiscoveryForTest(offer.ToJson(now), now, lease);
        protector.Block = true;
        var pending = Task.Run(() => pairing.ConfirmAsync(review.Id, review.KeyboardFingerprint, now));
        try
        {
            Assert.True(protector.Started.Wait(TimeSpan.FromSeconds(3)));
            if (stale) clock.Advance(TimeSpan.FromSeconds(2)); else lease.RecordForTest(false);
        }
        finally { protector.Release.Set(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.True(vault.IsAuthorized("old", old));
        Assert.Single(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task NewManualReviewRevokesOldLeaseWithoutBeingClearedByOldCancellation()
    {
        using var folder = new TestFolder();
        var now = DateTimeOffset.UtcNow;
        await using var lease = DiscoveryReviewLease.ForTest(SyntheticBinding(), new FakeClock());
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var offer = new PeerIdentityOffer { Name = "device", Platform = "windows", KeyboardFingerprint = new string('A', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(10) };
        var old = pairing.ReviewForDiscoveryForTest(offer.ToJson(now), now, lease);
        var current = pairing.Review((offer with { Name = "manual" }).ToJson(now), now);
        Assert.False(lease.IsFresh);
        Assert.False(pairing.CancelReview(old.Id));
        var invitation = await pairing.ConfirmAsync(current.Id, current.KeyboardFingerprint, now);
        Assert.Equal("manual", invitation.PeerName);
    }

    [Fact]
    public async Task NewManualReviewRevokesConsumedDiscoveryBeforePublication()
    {
        using var folder = new TestFolder();
        using var protector = new BlockingProtector();
        var now = DateTimeOffset.UtcNow;
        await using var lease = DiscoveryReviewLease.ForTest(SyntheticBinding(), new FakeClock());
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protector);
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var offer = new PeerIdentityOffer { Name = "device", Platform = "windows", KeyboardFingerprint = new string('A', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(10) };
        var old = pairing.ReviewForDiscoveryForTest(offer.ToJson(now), now, lease);
        protector.Block = true;
        var committing = Task.Run(() => pairing.ConfirmAsync(old.Id, old.KeyboardFingerprint, now));
        MultiPeerPairingPreview replacement;
        try
        {
            Assert.True(protector.Started.Wait(TimeSpan.FromSeconds(3)));
            replacement = pairing.Review((offer with { Name = "manual" }).ToJson(now), now);
            Assert.False(lease.IsFresh);
        }
        finally { protector.Release.Set(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => committing);
        var invitation = await pairing.ConfirmAsync(replacement.Id, replacement.KeyboardFingerprint, now);
        Assert.Equal("manual", invitation.PeerName);
        Assert.Single(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task SameLeaseCannotBeReusedForAnotherReview()
    {
        using var folder = new TestFolder();
        var now = DateTimeOffset.UtcNow;
        await using var lease = DiscoveryReviewLease.ForTest(SyntheticBinding(), new FakeClock());
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var offer = new PeerIdentityOffer { Name = "device", Platform = "windows", KeyboardFingerprint = new string('A', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(10) };
        var first = pairing.ReviewForDiscoveryForTest(offer.ToJson(now), now, lease);
        Assert.Throws<InvalidOperationException>(() => pairing.ReviewForDiscoveryForTest(offer.ToJson(now), now, lease));
        Assert.True(pairing.CancelReview(first.Id));
    }

    [Fact]
    public async Task ClaimedPublicationBlocksNewReviewAndDisposeDoesNotDrainCommit()
    {
        using var folder = new TestFolder();
        using var claimed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var now = DateTimeOffset.UtcNow;
        var lease = DiscoveryReviewLease.ForTest(SyntheticBinding(), new FakeClock());
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var offer = new PeerIdentityOffer { Name = "device", Platform = "windows", KeyboardFingerprint = new string('A', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(10) };
        var old = pairing.ReviewForDiscoveryForTest(offer.ToJson(now), now, lease);
        vault.AfterReviewClaimForTest = () => { claimed.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); };
        var committing = Task.Run(() => pairing.ConfirmAsync(old.Id, old.KeyboardFingerprint, now));
        try
        {
            Assert.True(claimed.Wait(TimeSpan.FromSeconds(3)));
            Assert.Throws<InvalidOperationException>(() => pairing.Review((offer with { Name = "manual" }).ToJson(now), now));
            await lease.DisposeAsync();
            Assert.False(committing.IsCompleted);
        }
        finally { release.Set(); }
        var invitation = await committing;
        Assert.Equal("device", invitation.PeerName);
        Assert.Single(vault.GetTrustedDevices());
    }

    private sealed class BlockingProtector : IMultiDeviceSecretProtector, IDisposable
    {
        private readonly WindowsCredentialProtector inner = new();
        public readonly ManualResetEventSlim Started = new(), Release = new();
        public bool Block { get; set; }
        public byte[] Protect(byte[] plaintext)
        {
            if (Block) { Started.Set(); if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
            return inner.Protect(plaintext);
        }
        public byte[] Unprotect(byte[] ciphertext) => inner.Unprotect(ciphertext);
        public void Dispose() { Release.Set(); Started.Dispose(); Release.Dispose(); }
    }

    private sealed class TestFolder : IDisposable
    {
        private static readonly string Root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../.work/discovery-review-lease"));
        internal string Path { get; } = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));
        public TestFolder() { EnsureNoLinks(Root); Directory.CreateDirectory(Path); }
        public void Dispose()
        {
            EnsureNoLinks(Path);
            if (!Directory.Exists(Path)) return;
            foreach (var file in Directory.EnumerateFiles(Path))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked test file refused.");
                File.Delete(file);
            }
            Directory.Delete(Path);
        }
        private static void EnsureNoLinks(string path)
        {
            for (string? current = System.IO.Path.GetFullPath(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked test path refused.");
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan value) => Interlocked.Add(ref ticks, value.Ticks);
    }
}
