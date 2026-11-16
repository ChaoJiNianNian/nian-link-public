using DesktopToolbox.DeskLink;
using NianLink.Windows;
using System.IO;

namespace MultiPeerTrust.Tests;

public sealed class PairingReviewLifecycleTests
{
    private static PeerIdentityOffer Offer(string name, DateTimeOffset now) => new()
    {
        Name = name, Platform = "windows", KeyboardFingerprint = new string('A', 64),
        Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(10)
    };

    [Fact]
    public async Task StaleCancellationCannotEraseNewerManualReview()
    {
        using var folder = new TestFolder();
        var now = DateTimeOffset.UtcNow;
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var old = pairing.ReviewForDiscovery(Offer("first", now).ToJson(now), now);
        var currentOffer = Offer("second", now);
        var current = pairing.Review(currentOffer.ToJson(now), now);
        Assert.False(pairing.CancelReview(old.Id));
        var invitation = await pairing.ConfirmAsync(current.Id, current.KeyboardFingerprint, now);
        Assert.Equal("second", invitation.PeerName);
        Assert.False(pairing.CancelReview(current.Id));
    }

    [Fact]
    public async Task ExplicitCancellationPreventsAuthorization()
    {
        using var folder = new TestFolder();
        var now = DateTimeOffset.UtcNow;
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var reviewed = pairing.ReviewForDiscovery(Offer("device", now).ToJson(now), now);
        Assert.True(pairing.CancelReview(reviewed.Id));
        Assert.False(pairing.CancelReview(reviewed.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pairing.ConfirmAsync(reviewed.Id, reviewed.KeyboardFingerprint, now));
        Assert.Empty(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task CancellationAfterReviewWasConsumedDoesNotPretendToUndoWriting()
    {
        using var folder = new TestFolder();
        using var protector = new BlockingProtector();
        var now = DateTimeOffset.UtcNow;
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protector);
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var reviewed = pairing.ReviewForDiscovery(Offer("device", now).ToJson(now), now);
        protector.Block = true;
        var committing = Task.Run(() => pairing.ConfirmAsync(reviewed.Id, reviewed.KeyboardFingerprint, now));
        try
        {
            Assert.True(protector.Started.Wait(TimeSpan.FromSeconds(3)));
            Assert.False(pairing.CancelReview(reviewed.Id));
        }
        finally { protector.Release.Set(); }
        var invitation = await committing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(vault.IsAuthorized("device", invitation.Token));
    }

    [Fact]
    public async Task DiscoveryReviewExpiresDuringRuntimeStopAndPreservesOldAuthorization()
    {
        using var folder = new TestFolder();
        var now = DateTimeOffset.UtcNow;
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var old = await vault.AddConfirmedAsync("old", new string('1', 64));
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var reviewed = pairing.ReviewForDiscovery(Offer("device", now).ToJson(now), now);
        Assert.Equal(now.AddMinutes(2), reviewed.ExpiresAtUtc);
        var runtime = new FakeRuntime();
        var change = new MultiPeerTrustChangeCoordinator(vault, runtime, new FixedClock(now.AddMinutes(2)));
        await Assert.ThrowsAsync<ArgumentException>(() => change.AddAsync(pairing, reviewed.Id, reviewed.KeyboardFingerprint, now));
        Assert.True(vault.IsAuthorized("old", old));
        Assert.Single(vault.GetTrustedDevices());
        Assert.Equal(new[] { "stop", "start:old" }, runtime.Events);
    }

    [Fact]
    public async Task DiscoveryExpiryWhileWaitingForTrustGateCannotGrant()
    {
        using var folder = new TestFolder();
        var now = DateTimeOffset.UtcNow;
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var reviewed = pairing.ReviewForDiscovery(Offer("device", now).ToJson(now), now);
        var change = new MultiPeerTrustChangeCoordinator(vault, new FakeRuntime(), new FixedClock(now.AddMinutes(2)));
        await vault.SessionChangeGate.WaitAsync();
        Task<MultiPeerInvitation> pending;
        try
        {
            pending = change.AddAsync(pairing, reviewed.Id, reviewed.KeyboardFingerprint, now);
            Assert.False(pending.IsCompleted);
        }
        finally { vault.SessionChangeGate.Release(); }
        await Assert.ThrowsAsync<ArgumentException>(() => pending);
        Assert.Empty(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task DiscoveryReviewCannotOutliveOfferOrSurviveClockRollback()
    {
        using var folder = new TestFolder();
        var now = DateTimeOffset.UtcNow;
        var offer = Offer("device", now) with { ExpiresAtUtc = now.AddSeconds(20) };
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var review = pairing.ReviewForDiscovery(offer.ToJson(now), now);
        Assert.Equal(offer.ExpiresAtUtc, review.ExpiresAtUtc);
        await Assert.ThrowsAsync<ArgumentException>(() => pairing.ConfirmAsync(review.Id, review.KeyboardFingerprint, now.AddTicks(-1)));
        await Assert.ThrowsAsync<ArgumentException>(() => pairing.ConfirmAsync(review.Id, review.KeyboardFingerprint, offer.ExpiresAtUtc));
        Assert.Empty(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task DiscoveryReviewExpiresByMonotonicTimeEvenWhenWallClockMovesBack()
    {
        using var folder = new TestFolder();
        var now = DateTimeOffset.UtcNow;
        var monotonic = new ManualTimestampClock(now);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64), monotonic);
        var review = pairing.ReviewForDiscovery(Offer("device", now).ToJson(now), now);
        monotonic.Advance(TimeSpan.FromMinutes(2));
        await Assert.ThrowsAsync<ArgumentException>(() => pairing.ConfirmAsync(review.Id, review.KeyboardFingerprint, now.AddMinutes(1)));
        Assert.Empty(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task ExpiryWhileProtectingTrustPreventsFilePublication()
    {
        using var folder = new TestFolder();
        using var protector = new BlockingProtector();
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualTimestampClock(now);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protector);
        var old = await vault.AddConfirmedAsync("old", new string('1', 64));
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64), clock);
        var review = pairing.ReviewForDiscovery(Offer("device", now).ToJson(now), now);
        var change = new MultiPeerTrustChangeCoordinator(vault, new FakeRuntime(), clock);
        protector.Block = true;
        var pending = Task.Run(() => change.AddAsync(pairing, review.Id, review.KeyboardFingerprint, now));
        try
        {
            Assert.True(protector.Started.Wait(TimeSpan.FromSeconds(3)));
            clock.Advance(TimeSpan.FromMinutes(2));
        }
        finally { protector.Release.Set(); }
        await Assert.ThrowsAsync<ArgumentException>(() => pending);
        Assert.True(vault.IsAuthorized("old", old));
        Assert.Single(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task ExpiryWhileWaitingForVaultGatePreventsGrant()
    {
        using var folder = new TestFolder();
        using var protector = new BlockingProtector();
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualTimestampClock(now);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protector);
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64), clock);
        var review = pairing.ReviewForDiscovery(Offer("device", now).ToJson(now), now);
        protector.Block = true;
        var otherWrite = Task.Run(() => vault.AddConfirmedAsync("other", new string('1', 64)));
        Task<MultiPeerInvitation>? pending = null;
        try
        {
            Assert.True(protector.Started.Wait(TimeSpan.FromSeconds(3)));
            pending = pairing.ConfirmAsync(review.Id, review.KeyboardFingerprint, now);
            Assert.False(pending.IsCompleted);
            Assert.False(pairing.CancelReview(review.Id));
            clock.Advance(TimeSpan.FromMinutes(2));
        }
        finally { protector.Release.Set(); }
        var otherToken = await otherWrite.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<ArgumentException>(() => pending!);
        Assert.True(vault.IsAuthorized("other", otherToken));
        Assert.Single(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task ObservedWallClockRollbackInvalidatesDiscoveryReview()
    {
        using var folder = new TestFolder();
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualTimestampClock(now);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64), clock);
        var review = pairing.ReviewForDiscovery(Offer("device", now).ToJson(now), now);
        pairing.ValidateConfirmation(review.Id, review.KeyboardFingerprint, now.AddSeconds(40));
        await Assert.ThrowsAsync<ArgumentException>(() => pairing.ConfirmAsync(review.Id, review.KeyboardFingerprint, now.AddSeconds(20)));
        Assert.Empty(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task ManualReviewRetainsOriginalIdentityCardExpiration()
    {
        using var folder = new TestFolder();
        var now = DateTimeOffset.UtcNow;
        var offer = Offer("device", now);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('B', 64));
        var review = pairing.Review(offer.ToJson(now), now);
        Assert.Equal(offer.ExpiresAtUtc, review.ExpiresAtUtc);
        var invite = await pairing.ConfirmAsync(review.Id, review.KeyboardFingerprint, now.AddMinutes(3));
        Assert.Equal("device", invite.PeerName);
    }

    private sealed class FixedClock(DateTimeOffset value) : TimeProvider { public override DateTimeOffset GetUtcNow() => value; }
    private sealed class ManualTimestampClock(DateTimeOffset initial) : TimeProvider
    {
        private long ticks;
        private long utcTicks = initial.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) { Interlocked.Add(ref ticks, duration.Ticks); Interlocked.Add(ref utcTicks, duration.Ticks); }
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
    private sealed class FakeRuntime : IMultiPeerRuntime
    {
        public List<string> Events { get; } = [];
        public Task StopAsync(CancellationToken ct) { Events.Add("stop"); return Task.CompletedTask; }
        public Task StartAsync(IReadOnlyList<TrustedDeviceIdentity> peers, CancellationToken ct)
        { Events.Add("start:" + string.Join(",", peers.Select(peer => peer.Name))); return Task.CompletedTask; }
    }
    private sealed class TestFolder : IDisposable
    {
        private static readonly string Root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../.work/pairing-review-tests"));
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
}
