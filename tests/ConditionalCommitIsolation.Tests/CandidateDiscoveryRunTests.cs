using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Channels;
using DesktopToolbox.DeskLink;
using NianLink.CandidateDiscovery;

namespace ConditionalCommitIsolation.Tests;

public sealed class CandidateDiscoveryRunTests
{
    private static readonly IPAddress Host = IPAddress.Parse("192.168.20.10"), Peer = IPAddress.Parse("192.168.20.11");
    private static DiscoveryNetworkBinding Binding(IPAddress address) => DiscoveryNetworkBinding.FromSnapshotsForTest(address,
        () => [new DiscoveryNetworkInterface("synthetic", "test", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 12, address, IPAddress.Parse("255.255.255.0"))]);
    private static PeerIdentityOffer Offer() => new() { Name = "synthetic", Platform = "windows", KeyboardFingerprint = new string('A', 64),
        Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) };


    [Fact]
    public async Task SyntheticScanAndResponderExchangeOnlyValidatedPacketsAndDrainOnStop()
    {
        var hostSocket = new FakeTransport(Host, 53000, false);
        var peerSocket = new FakeTransport(Peer, DiscoveryProtocol.Port, true);
        hostSocket.Other = peerSocket; peerSocket.Other = hostSocket;
        var peerSession = DiscoveryResponderSession.ForTest(Binding(Peer), Offer(), () => DateTimeOffset.UtcNow);
        var scanSession = DiscoveryScanSession.ForTest(Binding(Host), TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        await using var responder = CandidateDiscoveryRun.ForResponderTest(peerSession, peerSocket, () => true, TimeSpan.FromSeconds(10));
        await using var scanner = CandidateDiscoveryRun.ForScanTest(scanSession, hostSocket, () => true, TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (scanner.GetCandidates().Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(Peer, Assert.Single(scanner.GetCandidates()).Source);
        await scanner.DisposeAsync();
        await responder.DisposeAsync();
        Assert.Empty(scanner.GetCandidates());
        Assert.True(hostSocket.Disposed);
        Assert.True(peerSocket.Disposed);
        Assert.True(scanner.Completion.IsCompleted);
        Assert.True(responder.Completion.IsCompleted);
    }

    [Fact]
    public async Task InvalidPacketsAreIgnoredAndNetworkLossStopsBlockedReceive()
    {
        bool active = true;
        var transport = new FakeTransport(Host, 53000, false);
        var scan = DiscoveryScanSession.ForTest(Binding(Host), TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        await using var run = CandidateDiscoveryRun.ForScanTest(scan, transport, () => active, TimeSpan.FromSeconds(10));
        transport.Push(new CandidateDiscoveryDatagram([1, 2, 3], new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 99));
        await Task.Delay(30);
        Assert.Empty(run.GetCandidates());
        active = false;
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(transport.Disposed);
        Assert.Empty(run.GetCandidates());
    }

    [Fact]
    public async Task ForgedMetadataAndInvalidFramesCannotBecomeCandidates()
    {
        var transport = new FakeTransport(Host, 53000, false);
        var scan = DiscoveryScanSession.ForTest(Binding(Host), TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        var query = scan.CreateQuery();
        Assert.True(DiscoveryQuery.TryParse(query, DateTimeOffset.UtcNow, out var parsed));
        var now = DateTimeOffset.UtcNow;
        var valid = DiscoveryAnnouncement.Encode(Offer(), parsed!.ScanNonce, now, now.AddSeconds(5));
        await using var run = CandidateDiscoveryRun.ForScanTest(scan, transport, () => true, TimeSpan.FromSeconds(10));
        transport.Push(new(valid, new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 13));
        transport.Push(new(valid, new IPEndPoint(Peer, DiscoveryProtocol.Port), Peer, 12));
        transport.Push(new(valid, new IPEndPoint(Peer, 50000), Host, 12));
        transport.Push(new(valid, new IPEndPoint(IPAddress.Parse("192.168.21.11"), DiscoveryProtocol.Port), Host, 12));
        transport.Push(new([1, 2, 3], new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (transport.ReceivedCount < 5 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(5, transport.ReceivedCount);
        Assert.Empty(run.GetCandidates());
        transport.Push(new(valid, new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        while (run.GetCandidates().Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(Peer, Assert.Single(run.GetCandidates()).Source);
    }

    [Fact]
    public async Task CancelCallbackFailureStillClosesTransportAndDrainsTask()
    {
        var transport = new ThrowingCancellationTransport();
        var scan = DiscoveryScanSession.ForTest(Binding(Host), TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        var run = CandidateDiscoveryRun.ForScanTest(scan, transport, () => true, TimeSpan.FromSeconds(10), disableTestDeadline: true);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(transport.Disposed);
        Assert.True(run.Completion.IsCompleted);
        Assert.IsType<AggregateException>(run.Error);
        Assert.Empty(run.GetCandidates());
    }

    [Fact]
    public void ConcurrentReplyClaimsCanConsumeActualPacketOnlyOnce()
    {
        var lease = new CandidateQueryLease();
        var packet = new CandidateDiscoveryDatagram([1, 2, 3], new IPEndPoint(Host, 53000), DiscoveryProtocol.MulticastGroup, 12);
        lease.Publish(packet);
        int successes = 0;
        Parallel.For(0, 100, index => { if (lease.TryTake(packet, out _)) Interlocked.Increment(ref successes); });
        Assert.Equal(1, successes);
        var other = new CandidateDiscoveryDatagram([4], new IPEndPoint(Host, 53000), DiscoveryProtocol.MulticastGroup, 12);
        lease.Publish(packet);
        lease.Publish(other);
        Assert.False(lease.TryTake(packet, out _));
        Assert.True(lease.TryTake(other, out var copy));
        Assert.NotSame(other, copy);
        lease.Close();
        Assert.Throws<ObjectDisposedException>(() => lease.Publish(packet));
        Assert.False(lease.TryTake(packet, out _));
    }

    [Fact]
    public void ProtocolSessionCannotBeUsedWithAnotherNetworkBinding()
    {
        var first = Binding(Host);
        var other = Binding(Peer);
        using var scan = DiscoveryScanSession.ForTest(first, TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        using var responder = DiscoveryResponderSession.ForTest(first, Offer(), () => DateTimeOffset.UtcNow);
        WindowsDiscoveryDatagramSocket.RequireBoundScan(scan, first);
        WindowsDiscoveryDatagramSocket.RequireBoundResponder(responder, first);
        Assert.Throws<InvalidOperationException>(() => WindowsDiscoveryDatagramSocket.RequireBoundScan(scan, other));
        Assert.Throws<InvalidOperationException>(() => WindowsDiscoveryDatagramSocket.RequireBoundResponder(responder, other));
    }

    [Fact]
    public async Task FailedTransportCloseIsReportedOnlyAfterRunDrains()
    {
        var transport = new ThrowingDisposeTransport();
        var scan = DiscoveryScanSession.ForTest(Binding(Host), TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        var run = CandidateDiscoveryRun.ForScanTest(scan, transport, () => true, TimeSpan.FromSeconds(10));
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(run.Completion.IsCompleted);
        Assert.True(transport.CloseAttempted);
        Assert.Empty(run.GetCandidates());
    }

    [Fact]
    public async Task SelectionRequiresCurrentRunAndActualCandidateAndDrainsOnce()
    {
        var binding = Binding(Host);
        var now = DateTimeOffset.UtcNow;
        var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => now);
        var offer = Offer();
        Assert.True(DiscoveryQuery.TryParse(scan.CreateQuery(), now, out var query));
        var frame = DiscoveryAnnouncement.Encode(offer, query!.ScanNonce, now, now.AddSeconds(5));
        Assert.True(scan.Observe(frame, new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        var transport = new FakeTransport(Host, 53000, false);
        var run = CandidateDiscoveryRun.ForScanHandoffTest(scan, binding, transport,
            () => DiscoveryReviewLease.ForTest(binding, TimeProvider.System), () => now);
        var choice = Assert.Single(run.GetCandidateChoices());
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.SelectCandidateAsync(Guid.NewGuid(), choice.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.SelectCandidateAsync(run.Generation, Guid.NewGuid()));
        var result = await run.SelectCandidateAsync(run.Generation, choice.Id);
        Assert.Equal(run.Generation, result.Generation);
        Assert.Equal(offer, result.Offer);
        Assert.Equal(Peer, result.SourceAddress);
        Assert.NotSame(result.SourceAddress, result.SourceAddress);
        Assert.True(result.TryClaimForSecurePairing());
        var notice = HostPairingNoticeFactory.Create(result, binding, "host", new string('B', 64), 53001, now);
        Assert.Equal(Guid.ParseExact(offer.Nonce, "N"), notice.OfferNonce);
        Assert.Equal(offer.Name, notice.PeerName);
        Assert.Equal(53001, notice.Port);
        Assert.False(result.TryClaimForSecurePairing());
        Assert.True(result.Lease.IsBoundTo(binding));
        Assert.True(transport.Disposed);
        Assert.True(run.Completion.IsCompleted);
        Assert.Empty(run.GetCandidates());
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.SelectCandidateAsync(run.Generation, choice.Id));
        await result.Lease.DisposeAsync();
    }

    [Fact]
    public async Task SelectedSourceBelongsToTheSelectedCandidateRatherThanAnotherDiscoveredPeer()
    {
        var binding = Binding(Host);
        var now = DateTimeOffset.UtcNow;
        var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => now);
        Assert.True(DiscoveryQuery.TryParse(scan.CreateQuery(), now, out var query));
        var first = Offer() with { Name = "first" };
        var second = Offer() with { Name = "second" };
        var secondAddress = IPAddress.Parse("192.168.20.12");
        Assert.True(scan.Observe(DiscoveryAnnouncement.Encode(first, query!.ScanNonce, now, now.AddSeconds(5)),
            new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        Assert.True(scan.Observe(DiscoveryAnnouncement.Encode(second, query.ScanNonce, now, now.AddSeconds(5)),
            new IPEndPoint(secondAddress, DiscoveryProtocol.Port), Host, 12));
        var transport = new FakeTransport(Host, 53000, false);
        var run = CandidateDiscoveryRun.ForScanHandoffTest(scan, binding, transport,
            () => DiscoveryReviewLease.ForTest(binding, TimeProvider.System), () => now);
        var choice = Assert.Single(run.GetCandidateChoices(), item => item.Name == "second");
        var result = await run.SelectCandidateAsync(run.Generation, choice.Id);
        Assert.Equal(second, result.Offer);
        Assert.Equal(secondAddress, result.SourceAddress);
        await result.Lease.DisposeAsync();
        await run.DisposeAsync();
    }

    [Fact]
    public async Task ExpiredCandidateAndChangedBindingCannotBeSelected()
    {
        var binding = Binding(Host);
        var now = DateTimeOffset.UtcNow;
        var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => now);
        var offer = Offer();
        Assert.True(DiscoveryQuery.TryParse(scan.CreateQuery(), now, out var query));
        var frame = DiscoveryAnnouncement.Encode(offer, query!.ScanNonce, now, now.AddSeconds(5));
        Assert.True(scan.Observe(frame, new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        var transport = new FakeTransport(Host, 53000, false);
        Assert.Throws<InvalidOperationException>(() => CandidateDiscoveryRun.ForScanHandoffTest(scan, Binding(Peer), transport,
            () => DiscoveryReviewLease.ForTest(binding, TimeProvider.System), () => now));
        var run = CandidateDiscoveryRun.ForScanHandoffTest(scan, binding, transport,
            () => DiscoveryReviewLease.ForTest(binding, TimeProvider.System), () => now);
        var choice = Assert.Single(run.GetCandidateChoices());
        now = now.AddSeconds(5);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.SelectCandidateAsync(run.Generation, choice.Id));
        await run.DisposeAsync();
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expire")]
    [InlineData("network")]
    [InlineData("receive-error")]
    [InlineData("close-error")]
    public async Task SelectionFailureDuringDrainDisposesLeaseAndNeverDelivers(string scenario)
    {
        var binding = Binding(Host);
        var now = DateTimeOffset.UtcNow;
        var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => now);
        var offer = Offer();
        Assert.True(DiscoveryQuery.TryParse(scan.CreateQuery(), now, out var query));
        var frame = DiscoveryAnnouncement.Encode(offer, query!.ScanNonce, now, now.AddSeconds(5));
        Assert.True(scan.Observe(frame, new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        var transport = new DrainingTransport(scenario == "receive-error", scenario == "close-error");
        var lease = DiscoveryReviewLease.ForTest(binding, TimeProvider.System);
        var run = CandidateDiscoveryRun.ForScanHandoffTest(scan, binding, transport, () => lease, () => now);
        await transport.Receiving.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var choice = Assert.Single(run.GetCandidateChoices());
        var selection = run.SelectCandidateAsync(run.Generation, choice.Id);
        await transport.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(selection.IsCompleted);
        Task? cancellation = null;
        if (scenario == "cancel") cancellation = run.DisposeAsync().AsTask();
        if (scenario == "expire") now = now.AddSeconds(5);
        if (scenario == "network") lease.TryRevoke();
        transport.Release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => selection);
        if (cancellation is not null) await cancellation;
        Assert.False(lease.IsFresh);
        Assert.True(run.Completion.IsCompleted);
        Assert.Empty(run.GetCandidateChoices());
    }

    [Fact]
    public async Task ConcurrentSelectionCannotDeliverTwiceOrBeforeDrain()
    {
        var binding = Binding(Host);
        var now = DateTimeOffset.UtcNow;
        var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => now);
        var offer = Offer();
        Assert.True(DiscoveryQuery.TryParse(scan.CreateQuery(), now, out var query));
        Assert.True(scan.Observe(DiscoveryAnnouncement.Encode(offer, query!.ScanNonce, now, now.AddSeconds(5)),
            new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        var transport = new DrainingTransport(false, false);
        var run = CandidateDiscoveryRun.ForScanHandoffTest(scan, binding, transport,
            () => DiscoveryReviewLease.ForTest(binding, TimeProvider.System), () => now);
        await transport.Receiving.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var choice = Assert.Single(run.GetCandidateChoices());
        var selected = run.SelectCandidateAsync(run.Generation, choice.Id);
        await transport.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.SelectCandidateAsync(run.Generation, choice.Id));
        Assert.False(selected.IsCompleted);
        transport.Release.TrySetResult();
        var result = await selected;
        Assert.Equal(offer, result.Offer);
        Assert.True(run.Completion.IsCompleted);
        await result.Lease.DisposeAsync();
    }

    [Fact]
    public async Task FailedLeaseCreationStillStopsAndDrainsTheScan()
    {
        var binding = Binding(Host);
        var now = DateTimeOffset.UtcNow;
        var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => now);
        var offer = Offer();
        Assert.True(DiscoveryQuery.TryParse(scan.CreateQuery(), now, out var query));
        Assert.True(scan.Observe(DiscoveryAnnouncement.Encode(offer, query!.ScanNonce, now, now.AddSeconds(5)),
            new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        var transport = new DrainingTransport(false, false);
        var run = CandidateDiscoveryRun.ForScanHandoffTest(scan, binding, transport,
            () => throw new InvalidOperationException("network unavailable"), () => now);
        await transport.Receiving.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var choice = Assert.Single(run.GetCandidateChoices());
        var selected = run.SelectCandidateAsync(run.Generation, choice.Id);
        await transport.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(selected.IsCompleted);
        transport.Release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => selected);
        Assert.True(run.Completion.IsCompleted);
        Assert.Empty(run.GetCandidateChoices());
    }

    [Fact]
    public async Task MonotonicRunDeadlineDuringDrainRejectsDelivery()
    {
        var binding = Binding(Host);
        var now = DateTimeOffset.UtcNow;
        var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => now);
        var offer = Offer();
        Assert.True(DiscoveryQuery.TryParse(scan.CreateQuery(), now, out var query));
        Assert.True(scan.Observe(DiscoveryAnnouncement.Encode(offer, query!.ScanNonce, now, now.AddSeconds(5)),
            new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        var transport = new DrainingTransport(false, false);
        var lease = DiscoveryReviewLease.ForTest(binding, TimeProvider.System);
        var run = CandidateDiscoveryRun.ForScanHandoffTest(scan, binding, transport, () => lease, () => now, TimeSpan.FromSeconds(1));
        await transport.Receiving.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var choice = Assert.Single(run.GetCandidateChoices());
        var selected = run.SelectCandidateAsync(run.Generation, choice.Id);
        await transport.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(1200);
        transport.Release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => selected);
        Assert.False(lease.IsFresh);
        Assert.True(run.Completion.IsCompleted);
    }

    [Fact]
    public async Task NetworkFailureObservedDuringSelectionPermanentlyRejectsHandoff()
    {
        var binding = Binding(Host);
        var now = DateTimeOffset.UtcNow;
        var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => now);
        var offer = Offer();
        Assert.True(DiscoveryQuery.TryParse(scan.CreateQuery(), now, out var query));
        Assert.True(scan.Observe(DiscoveryAnnouncement.Encode(offer, query!.ScanNonce, now, now.AddSeconds(5)),
            new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        using var checking = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        bool Active()
        {
            if (Interlocked.Increment(ref calls) == 2)
            { checking.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return false; }
            return true;
        }
        var transport = new FakeTransport(Host, 53000, false);
        var lease = DiscoveryReviewLease.ForTest(binding, TimeProvider.System);
        var run = CandidateDiscoveryRun.ForScanHandoffTest(scan, binding, transport, () => lease, () => now, testActive: Active);
        Assert.True(checking.Wait(TimeSpan.FromSeconds(3)));
        var choice = Assert.Single(run.GetCandidateChoices());
        var selection = run.SelectCandidateAsync(run.Generation, choice.Id);
        try { Assert.False(selection.IsCompleted); }
        finally { release.Set(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => selection);
        Assert.False(lease.IsFresh);
    }

    [Fact]
    public async Task ReusedOfferNonceDoesNotReviveOldChoiceOrChangeItsIdentity()
    {
        var binding = Binding(Host);
        var now = DateTimeOffset.UtcNow;
        var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => now);
        var old = Offer() with { ExpiresAtUtc = now.AddSeconds(2) };
        Assert.True(DiscoveryQuery.TryParse(scan.CreateQuery(), now, out var query));
        Assert.True(scan.Observe(DiscoveryAnnouncement.Encode(old, query!.ScanNonce, now, now.AddSeconds(1)),
            new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        var run = CandidateDiscoveryRun.ForScanHandoffTest(scan, binding, new FakeTransport(Host, 53000, false),
            () => DiscoveryReviewLease.ForTest(binding, TimeProvider.System), () => now);
        var previous = Assert.Single(run.GetCandidateChoices());
        now = now.AddSeconds(3);
        var next = old with { Name = "replacement", KeyboardFingerprint = new string('B', 64), ExpiresAtUtc = now.AddSeconds(5) };
        Assert.True(scan.Observe(DiscoveryAnnouncement.Encode(next, query.ScanNonce, now, now.AddSeconds(3)),
            new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        var current = Assert.Single(run.GetCandidateChoices());
        Assert.NotEqual(previous.Id, current.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.SelectCandidateAsync(run.Generation, previous.Id));
        var result = await run.SelectCandidateAsync(run.Generation, current.Id);
        Assert.Equal(next, result.Offer);
        await result.Lease.DisposeAsync();
    }

    [Fact]
    public void CancelledStartCannotStartTransportOrSendQuery()
    {
        var binding = Binding(Host);
        var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        var transport = new FakeTransport(Host, 53000, false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CandidateDiscoveryRun.ForScanWithCancellationTest(scan, transport, cancelled.Token));
        Assert.Equal(0, transport.QueryCount);
        scan.Dispose(); transport.Dispose();
    }

    [Fact]
    public void StartupCleanupAttemptsEveryResourceAfterOneFails()
    {
        var bad = new DisposableProbe(true);
        var good = new DisposableProbe(false);
        var error = DiscoveryStartupCleanup.TryDispose(bad, good);
        Assert.IsType<InvalidOperationException>(error);
        Assert.True(bad.Called);
        Assert.True(good.Called);
    }

    private sealed class DisposableProbe(bool throws) : IDisposable
    {
        internal bool Called;
        public void Dispose() { Called = true; if (throws) throw new InvalidOperationException("cleanup"); }
    }

    private sealed class DrainingTransport(bool receiveError, bool closeError) : ICandidateDiscoveryTransport
    {
        internal TaskCompletionSource Receiving { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendQueryAsync(DiscoveryScanSession scan, CancellationToken ct) { _ = scan.CreateQuery(); return Task.CompletedTask; }
        public Task ReplyToReceivedQueryAsync(DiscoveryResponderSession session, CandidateDiscoveryDatagram packet, CancellationToken ct) => throw new InvalidOperationException();
        public async Task<CandidateDiscoveryDatagram> ReceiveAsync(CancellationToken ct)
        {
            Receiving.TrySetResult();
            await Release.Task;
            if (receiveError) throw new IOException("unexpected receive failure");
            throw new OperationCanceledException(ct);
        }
        public void Dispose() { Closed.TrySetResult(); if (closeError) throw new IOException("close failed"); }
    }

    private sealed class ThrowingDisposeTransport : ICandidateDiscoveryTransport
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CloseAttempted { get; private set; }
        public Task SendQueryAsync(DiscoveryScanSession scan, CancellationToken ct) { _ = scan.CreateQuery(); return Task.CompletedTask; }
        public Task ReplyToReceivedQueryAsync(DiscoveryResponderSession session, CandidateDiscoveryDatagram packet, CancellationToken ct) => throw new InvalidOperationException();
        public async Task<CandidateDiscoveryDatagram> ReceiveAsync(CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        }
        public void Dispose() { CloseAttempted = true; throw new InvalidOperationException("synthetic close failure"); }
    }

    private sealed class ThrowingCancellationTransport : ICandidateDiscoveryTransport
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        private CancellationTokenRegistration callback;
        public Task SendQueryAsync(DiscoveryScanSession scan, CancellationToken ct)
        {
            _ = scan.CreateQuery();
            callback = ct.Register(() => throw new InvalidOperationException("synthetic cancellation failure"));
            return Task.CompletedTask;
        }
        public Task ReplyToReceivedQueryAsync(DiscoveryResponderSession session, CandidateDiscoveryDatagram packet, CancellationToken ct) => throw new InvalidOperationException();
        public async Task<CandidateDiscoveryDatagram> ReceiveAsync(CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        }
        public void Dispose() { callback.Dispose(); Disposed = true; }
    }

    private sealed class FakeTransport(IPAddress address, int port, bool responder) : ICandidateDiscoveryTransport
    {
        private readonly Channel<CandidateDiscoveryDatagram> incoming = Channel.CreateUnbounded<CandidateDiscoveryDatagram>();
        private readonly IPAddress localAddress = address;
        private readonly int localPort = port;
        public FakeTransport? Other { get; set; }
        public bool Disposed { get; private set; }
        private int receivedCount;
        public int ReceivedCount => Volatile.Read(ref receivedCount);
        public int QueryCount { get; private set; }
        public void Push(CandidateDiscoveryDatagram packet) => incoming.Writer.TryWrite(packet);
        public Task SendQueryAsync(DiscoveryScanSession scan, CancellationToken ct)
        {
            if (responder) throw new InvalidOperationException();
            QueryCount++;
            var data = scan.CreateQuery();
            Other?.Push(new(data, new IPEndPoint(localAddress, localPort), DiscoveryProtocol.MulticastGroup, 12));
            return Task.CompletedTask;
        }
        public Task ReplyToReceivedQueryAsync(DiscoveryResponderSession session, CandidateDiscoveryDatagram packet, CancellationToken ct)
        {
            var destination = packet.Source;
            if (!responder || Other is null || !destination.Address.Equals(Other.localAddress) || destination.Port != Other.localPort)
                throw new InvalidOperationException();
            var data = session.Reply(packet.Data, packet.Source, packet.Destination, packet.InterfaceIndex);
            if (data is not null) Other.Push(new(data, new IPEndPoint(localAddress, localPort), destination.Address, 12));
            return Task.CompletedTask;
        }
        public async Task<CandidateDiscoveryDatagram> ReceiveAsync(CancellationToken ct)
        {
            var packet = await incoming.Reader.ReadAsync(ct);
            Interlocked.Increment(ref receivedCount);
            return packet;
        }
        public void Dispose() { Disposed = true; incoming.Writer.TryComplete(); }
    }
}
