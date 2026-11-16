using System.Net;
using System.Net.NetworkInformation;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingNoticeInboxTests
{
    private static readonly IPAddress Local = IPAddress.Parse("192.168.88.4"), Peer = IPAddress.Parse("192.168.88.9");
    private static DiscoveryNetworkBinding Binding() => DiscoveryNetworkBinding.FromSnapshotsForTest(Local, () =>
        [new("test-id", "test", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 7, Local, IPAddress.Parse("255.255.255.0"))]);
    private static PeerIdentityOffer Offer(DateTimeOffset now) => new()
    { Name = "peer", Platform = "mac", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
    private static PairingDiscoveryNotice Notice(PeerIdentityOffer offer, DateTimeOffset now) => new("host", offer.Name, new string('B',64),
        Guid.ParseExact(offer.Nonce,"N"), Guid.NewGuid(), Guid.NewGuid(), 53123, now.AddSeconds(20), now.AddMinutes(1));

    [Fact]
    public void ValidReceivedNoticeCanOnlyBeClaimedOnceAndPreservesSource()
    {
        var now = DateTimeOffset.UtcNow; var offer = Offer(now); var binding = Binding();
        using var inbox = new PairingNoticeInbox(binding, offer, TimeSpan.FromSeconds(30), default, () => now);
        var packet = Notice(offer, now).Encode(now);
        Assert.True(inbox.Observe(packet, new IPEndPoint(Peer, 50500), Local, 7));
        var request = Assert.IsType<PairingIncomingRequest>(inbox.GetPending());
        Assert.Equal("host", request.HostName);
        var claim = Assert.IsType<PairingIncomingClaim>(request.TryClaim());
        Assert.Equal(Peer, claim.SourceAddress);
        Assert.Same(binding, claim.Binding);
        Assert.Null(request.TryClaim());
        Assert.Null(inbox.GetPending());
    }

    [Theory]
    [InlineData("source")][InlineData("local")][InlineData("destination")][InlineData("index")]
    [InlineData("nonce")][InlineData("name")][InlineData("expired")][InlineData("oversize")]
    public void IncorrectNoticeOrNetworkIsIgnored(string mode)
    {
        var now = DateTimeOffset.UtcNow; var offer = Offer(now); var binding = Binding();
        using var inbox = new PairingNoticeInbox(binding, offer, TimeSpan.FromSeconds(30), default, () => now);
        var notice = Notice(offer,now);
        if (mode == "nonce") notice = notice with { OfferNonce = Guid.NewGuid() };
        if (mode == "name") notice = notice with { PeerName = "other" };
        var data = mode == "oversize" ? new byte[PairingDiscoveryNotice.MaximumBytes + 1] : notice.Encode(now);
        if (mode == "expired") now = now.AddSeconds(21);
        Assert.False(inbox.Observe(data, new IPEndPoint(mode == "source" ? IPAddress.Parse("192.168.89.8") : mode == "local" ? Local : Peer, 50001),
            mode == "destination" ? IPAddress.Parse("192.168.88.5") : Local, mode == "index" ? 8 : 7));
        Assert.Null(inbox.GetPending());
    }

    [Theory]
    [InlineData("reject")][InlineData("cancel")][InlineData("expiry")][InlineData("stop")]
    public void RejectedStoppedCancelledOrExpiredRequestCannotBeClaimed(string mode)
    {
        var now = DateTimeOffset.UtcNow; var offer = Offer(now); using var cancel = new CancellationTokenSource();
        using var inbox = new PairingNoticeInbox(Binding(), offer, TimeSpan.FromSeconds(30), cancel.Token, () => now);
        var notice = Notice(offer,now);
        var packet = notice.Encode(now);
        Assert.True(inbox.Observe(packet, new IPEndPoint(Peer, 50001), Local, 7));
        var request = Assert.IsType<PairingIncomingRequest>(inbox.GetPending());
        if (mode == "reject") Assert.True(inbox.Reject(request.Id));
        if (mode == "cancel") cancel.Cancel();
        if (mode == "expiry") now = now.AddSeconds(21);
        if (mode == "stop") inbox.Dispose();
        Assert.Null(request.TryClaim());
        Assert.Null(inbox.GetPending());
        Assert.False(inbox.Observe(packet, new IPEndPoint(Peer, 50001), Local, 7));
    }

    [Fact]
    public void PendingCannotBeReplacedAndRejectedNoticeCannotBeReplayed()
    {
        var now = DateTimeOffset.UtcNow; var offer = Offer(now); using var inbox = new PairingNoticeInbox(Binding(), offer, TimeSpan.FromSeconds(30), default, () => now);
        var first = Notice(offer, now); var second = Notice(offer, now);
        Assert.True(inbox.Observe(first.Encode(now), new IPEndPoint(Peer, 50001), Local, 7));
        Assert.False(inbox.Observe(second.Encode(now), new IPEndPoint(Peer, 50001), Local, 7));
        var request = Assert.IsType<PairingIncomingRequest>(inbox.GetPending());
        Assert.True(inbox.Reject(request.Id));
        Assert.False(inbox.Observe(first.Encode(now), new IPEndPoint(Peer, 50001), Local, 7));
        Assert.True(inbox.Observe(second.Encode(now), new IPEndPoint(Peer, 50001), Local, 7));
    }

    [Fact]
    public async Task ClaimedRequestAcquiresIndependentNetworkBeforeDiscoveryExpires()
    {
        var now = DateTimeOffset.UtcNow; var offer = Offer(now); using var discovery = new CancellationTokenSource();
        var network = new FakeHandoff(Binding());
        using var inbox = new PairingNoticeInbox(Binding(), offer, TimeSpan.FromSeconds(30), discovery.Token, () => now,
            _ => Task.FromResult<PairingNetworkHandoff>(network));
        Assert.True(inbox.Observe(Notice(offer, now).Encode(now), new IPEndPoint(Peer, 50001), Local, 7));
        var claim = Assert.IsType<PairingIncomingRequest>(inbox.GetPending()).TryClaim()!;
        var handoff = await claim.AcquireNetworkAsync(default);
        discovery.Cancel();
        Assert.False(handoff.RevocationToken.IsCancellationRequested);
        Assert.True(await handoff.RevalidateAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => claim.AcquireNetworkAsync(default));
        await handoff.DisposeAsync();
    }

    [Fact]
    public async Task DiscoveryCancellationDuringNetworkHandoffDisposesLateResult()
    {
        var now = DateTimeOffset.UtcNow; var offer = Offer(now); using var discovery = new CancellationTokenSource();
        var source = new TaskCompletionSource<PairingNetworkHandoff>(TaskCreationOptions.RunContinuationsAsynchronously);
        var network = new FakeHandoff(Binding());
        using var inbox = new PairingNoticeInbox(Binding(), offer, TimeSpan.FromSeconds(30), discovery.Token, () => now, _ => source.Task);
        Assert.True(inbox.Observe(Notice(offer, now).Encode(now), new IPEndPoint(Peer, 50001), Local, 7));
        var claim = Assert.IsType<PairingIncomingRequest>(inbox.GetPending()).TryClaim()!;
        var pending = claim.AcquireNetworkAsync(default);
        discovery.Cancel(); source.SetResult(network);
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        await network.DisposeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(network.Disposed);
    }

    [Fact]
    public async Task FailedRevalidationAndFailedCleanupReportStartupCleanupFailure()
    {
        var now = DateTimeOffset.UtcNow; var offer = Offer(now);
        var network = new FakeHandoff(Binding()) { FailRevalidation = true, FailDispose = true };
        using var inbox = new PairingNoticeInbox(Binding(), offer, TimeSpan.FromSeconds(30), default, () => now,
            _ => Task.FromResult<PairingNetworkHandoff>(network));
        Assert.True(inbox.Observe(Notice(offer, now).Encode(now), new IPEndPoint(Peer, 50001), Local, 7));
        var claim = Assert.IsType<PairingIncomingRequest>(inbox.GetPending()).TryClaim()!;
        var failure = await Assert.ThrowsAsync<PairingStartupCleanupException>(() => claim.AcquireNetworkAsync(default));
        Assert.True(network.Disposed);
        var errors = Assert.IsType<AggregateException>(failure.InnerException).InnerExceptions;
        Assert.Contains(errors, error => error is InvalidOperationException);
        Assert.Contains(errors, error => error is IOException && error.Message == "cleanup failed");
    }

    [Fact]
    public async Task CancelledUnfinishedHandoffCannotPermitImmediateRestartAndIsDisposedWhenItArrives()
    {
        var now = DateTimeOffset.UtcNow; var offer = Offer(now); using var discovery = new CancellationTokenSource();
        var source = new TaskCompletionSource<PairingNetworkHandoff>(TaskCreationOptions.RunContinuationsAsynchronously);
        var network = new FakeHandoff(Binding()) { FailDispose = true };
        using var inbox = new PairingNoticeInbox(Binding(), offer, TimeSpan.FromSeconds(30), discovery.Token, () => now, _ => source.Task);
        Assert.True(inbox.Observe(Notice(offer, now).Encode(now), new IPEndPoint(Peer, 50001), Local, 7));
        var claim = Assert.IsType<PairingIncomingRequest>(inbox.GetPending()).TryClaim()!;
        var pending = claim.AcquireNetworkAsync(default);
        discovery.Cancel();
        await Assert.ThrowsAsync<PairingStartupCleanupException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        source.SetResult(network);
        await network.DisposeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(network.Disposed);
        Assert.Null(inbox.GetPending());
    }

    [Fact]
    public async Task OrdinaryHandoffFailureRemainsOrdinaryFailure()
    {
        var now = DateTimeOffset.UtcNow; var offer = Offer(now);
        using var inbox = new PairingNoticeInbox(Binding(), offer, TimeSpan.FromSeconds(30), default, () => now,
            _ => Task.FromException<PairingNetworkHandoff>(new IOException("unavailable")));
        Assert.True(inbox.Observe(Notice(offer, now).Encode(now), new IPEndPoint(Peer, 50001), Local, 7));
        var claim = Assert.IsType<PairingIncomingRequest>(inbox.GetPending()).TryClaim()!;
        await Assert.ThrowsAsync<IOException>(() => claim.AcquireNetworkAsync(default));
    }

    [Fact]
    public async Task ConcurrentCancellationAndFactoryCompletionAlwaysDrainsOrReturnsHandoff()
    {
        // Complete the factory and cancel discovery from separate workers released at the same barrier.
        // Every produced handoff must either be returned to the caller or have disposal attempted.
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var now = DateTimeOffset.UtcNow;
            var offer = Offer(now);
            using var discovery = new CancellationTokenSource();
            using var barrier = new Barrier(3);
            var source = new TaskCompletionSource<PairingNetworkHandoff>(TaskCreationOptions.RunContinuationsAsynchronously);
            var network = new FakeHandoff(Binding());
            using var inbox = new PairingNoticeInbox(Binding(), offer, TimeSpan.FromSeconds(30), discovery.Token, () => now, _ => source.Task);
            Assert.True(inbox.Observe(Notice(offer, now).Encode(now), new IPEndPoint(Peer, 50001), Local, 7));
            var claim = Assert.IsType<PairingIncomingRequest>(inbox.GetPending()).TryClaim()!;
            var pending = claim.AcquireNetworkAsync(default);
            var cancelling = Task.Run(() => { barrier.SignalAndWait(); discovery.Cancel(); });
            var completing = Task.Run(() => { barrier.SignalAndWait(); source.SetResult(network); });
            barrier.SignalAndWait();
            await Task.WhenAll(cancelling, completing);
            try { await (await pending).DisposeAsync(); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
            await network.DisposeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(network.Disposed);
        }
    }

    private sealed class FakeHandoff(DiscoveryNetworkBinding binding) : PairingNetworkHandoff
    {
        private readonly CancellationTokenSource cancelled = new();
        public bool FailRevalidation { get; init; }
        public bool FailDispose { get; init; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource DisposeAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override DiscoveryNetworkBinding Binding => binding;
        internal override CancellationToken RevocationToken => cancelled.Token;
        internal override Task<bool> RevalidateAsync(CancellationToken ct) => Task.FromResult(!FailRevalidation && !cancelled.IsCancellationRequested);
        public override ValueTask DisposeAsync()
        {
            Disposed = true; cancelled.Cancel(); DisposeAttempted.TrySetResult();
            return FailDispose ? ValueTask.FromException(new IOException("cleanup failed")) : ValueTask.CompletedTask;
        }
    }
}
