using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Channels;
using DesktopToolbox.DeskLink;
using NianLink.CandidateDiscovery;

namespace PairingDiagnostics.Tests;

public sealed class CandidateScanRetryTests
{
    private static readonly IPAddress Host = IPAddress.Parse("192.168.20.10"), Peer = IPAddress.Parse("192.168.20.11");
    private static DiscoveryNetworkBinding Binding(IPAddress address) => DiscoveryNetworkBinding.FromSnapshotsForTest(address,
        () => [new DiscoveryNetworkInterface("synthetic", "test", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 12, address, IPAddress.Parse("255.255.255.0"))]);
    private static PeerIdentityOffer Offer() => new() { Name = "synthetic", Platform = "windows", KeyboardFingerprint = new string('A', 64),
        Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) };

    [Fact]
    public async Task ScannerFindsResponderWhichStartsAfterFirstQueryWithoutChangingNonce()
    {
        var started = DateTimeOffset.UtcNow;
        var hostSocket = new FakeTransport(Host, 53000, false);
        var hostSession = DiscoveryScanSession.ForTest(Binding(Host), TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        await using var scan = CandidateDiscoveryRun.ForScanTest(hostSession, hostSocket, () => true, TimeSpan.FromSeconds(10));
        var first = await hostSocket.Queries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(DiscoveryQuery.TryParse(first.Packet, DateTimeOffset.UtcNow, out var firstQuery));

        // The peer becomes discoverable only after the first query was sent.
        var peerSocket = new FakeTransport(Peer, DiscoveryProtocol.Port, true);
        hostSocket.Other = peerSocket; peerSocket.Other = hostSocket;
        var peerSession = DiscoveryResponderSession.ForTest(Binding(Peer), Offer(), () => DateTimeOffset.UtcNow);
        await using var responder = CandidateDiscoveryRun.ForResponderTest(peerSession, peerSocket, () => true, TimeSpan.FromSeconds(10));
        var second = await hostSocket.Queries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(DiscoveryQuery.TryParse(second.Packet, DateTimeOffset.UtcNow, out var secondQuery));
        Assert.Equal(firstQuery!.ScanNonce, secondQuery!.ScanNonce);
        Assert.True(firstQuery.ExpiresAtUtc <= started.AddSeconds(11));
        Assert.True(secondQuery.ExpiresAtUtc <= started.AddSeconds(11));
        Assert.True(Stopwatch.GetElapsedTime(first.Timestamp, second.Timestamp) >= TimeSpan.FromMilliseconds(1800));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (scan.GetCandidates().Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(Peer, Assert.Single(scan.GetCandidates()).Source);
        var third = await hostSocket.Queries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(DiscoveryQuery.TryParse(third.Packet, DateTimeOffset.UtcNow, out var thirdQuery));
        Assert.Equal(firstQuery.ScanNonce, thirdQuery!.ScanNonce);
        deadline = DateTime.UtcNow.AddSeconds(3);
        while (peerSocket.ReplyAttempts < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(peerSocket.ReplyAttempts >= 2);
        // Repeating the scan nonce does not relax the peer's replay protection.
        Assert.Equal(1, peerSocket.Replies);
    }

    [Fact]
    public async Task ContinuousInboundPacketsDoNotStarveOrAccelerateQueries()
    {
        var socket = new FakeTransport(Host, 53000, false) { Flood = true };
        var scan = DiscoveryScanSession.ForTest(Binding(Host), TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        await using var run = CandidateDiscoveryRun.ForScanTest(scan, socket, () => true, TimeSpan.FromSeconds(10));
        var first = await socket.Queries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var second = await socket.Queries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(socket.Received > 100);
        Assert.True(Stopwatch.GetElapsedTime(first.Timestamp, second.Timestamp) >= TimeSpan.FromMilliseconds(1800));
        Assert.Empty(run.GetCandidates());
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("network")]
    [InlineData("deadline")]
    [InlineData("clock-backward")]
    public async Task ClosedExpiredOrUnavailableScansStopQueriesAndDisposeTransport(string reason)
    {
        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        var active = 1;
        var socket = new FakeTransport(Host, 53000, false);
        var binding = Binding(Host);
        var duration = reason == "deadline" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10);
        var scan = DiscoveryScanSession.ForTest(binding, duration, () => new DateTimeOffset(Interlocked.Read(ref ticks), TimeSpan.Zero));
        var run = CandidateDiscoveryRun.ForScanTest(scan, socket, () => Volatile.Read(ref active) != 0, duration);
        await socket.Queries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        if (reason == "cancel") await run.DisposeAsync();
        if (reason == "network") Interlocked.Exchange(ref active, 0);
        if (reason == "clock-backward") Interlocked.Add(ref ticks, -TimeSpan.TicksPerSecond);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Null(run.Error);
        Assert.True(socket.Disposed);
        Assert.Equal(1, socket.QueryCount);
        Assert.False(await socket.Queries.Reader.WaitToReadAsync());
        await run.DisposeAsync();
    }

    [Fact]
    public async Task SelectionClosesTheRunBeforeAnotherQueryCanStart()
    {
        var binding = Binding(Host);
        var now = DateTimeOffset.UtcNow;
        var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => now);
        Assert.True(DiscoveryQuery.TryParse(scan.CreateQuery(), now, out var query));
        var offer = Offer();
        Assert.True(scan.Observe(DiscoveryAnnouncement.Encode(offer, query!.ScanNonce, now, now.AddSeconds(5)),
            new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        var socket = new FakeTransport(Host, 53000, false);
        var run = CandidateDiscoveryRun.ForScanHandoffTest(scan, binding, socket,
            () => DiscoveryReviewLease.ForTest(binding, TimeProvider.System), () => now);
        await socket.Queries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var result = await run.SelectCandidateAsync(run.Generation, Assert.Single(run.GetCandidateChoices()).Id);
        Assert.True(socket.Disposed);
        Assert.True(run.Completion.IsCompleted);
        Assert.Equal(1, socket.QueryCount);
        Assert.False(await socket.Queries.Reader.WaitToReadAsync());
        await result.Lease.DisposeAsync();
        await run.DisposeAsync();
    }

    [Fact]
    public async Task SecondQueryFailureEndsRunAndDisposesTransport()
    {
        var socket = new FakeTransport(Host, 53000, false) { FailOnQuery = 2 };
        var scan = DiscoveryScanSession.ForTest(Binding(Host), TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        var run = CandidateDiscoveryRun.ForScanTest(scan, socket, () => true, TimeSpan.FromSeconds(10));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<IOException>(run.Error);
        Assert.True(socket.Disposed);
        Assert.Equal(2, socket.QueryCount);
        await run.DisposeAsync();
    }

    [Fact]
    public async Task QueryCannotStartWhenActiveCheckCrossesMonotonicDeadline()
    {
        var socket = new FakeTransport(Host, 53000, false);
        var scan = DiscoveryScanSession.ForTest(Binding(Host), TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        var activeCalls = 0;
        var run = CandidateDiscoveryRun.ForScanTest(scan, socket, () =>
        {
            if (Interlocked.Increment(ref activeCalls) == 2) Thread.Sleep(1200);
            return true;
        }, TimeSpan.FromSeconds(1));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, socket.QueryCount);
        Assert.Null(run.Error);
        Assert.True(socket.Disposed);
        await run.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedRetryDrainsOnCancellationOrHandoffWithoutReportingDisposedSocket(bool handoff)
    {
        var binding = Binding(Host);
        var now = DateTimeOffset.UtcNow;
        var scan = DiscoveryScanSession.ForTest(binding, TimeSpan.FromSeconds(10), () => now);
        Assert.True(DiscoveryQuery.TryParse(scan.CreateQuery(), now, out var query));
        var offer = Offer();
        Assert.True(scan.Observe(DiscoveryAnnouncement.Encode(offer, query!.ScanNonce, now, now.AddSeconds(9)),
            new IPEndPoint(Peer, DiscoveryProtocol.Port), Host, 12));
        var socket = new FakeTransport(Host, 53000, false) { BlockOnQuery = 2 };
        var run = CandidateDiscoveryRun.ForScanHandoffTest(scan, binding, socket,
            () => DiscoveryReviewLease.ForTest(binding, TimeProvider.System), () => now);
        await socket.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (handoff)
        {
            var result = await run.SelectCandidateAsync(run.Generation, Assert.Single(run.GetCandidateChoices()).Id);
            await result.Lease.DisposeAsync();
        }
        else await run.DisposeAsync();
        Assert.True(run.Completion.IsCompleted);
        Assert.Null(run.Error);
        Assert.Equal(2, socket.QueryCount);
        Assert.True(socket.Disposed);
        await run.DisposeAsync();
    }

    [Fact]
    public async Task CleanupFailureDuringBlockedRetryRemainsVisible()
    {
        var socket = new FakeTransport(Host, 53000, false) { BlockOnQuery = 2, FailOnDispose = true };
        var scan = DiscoveryScanSession.ForTest(Binding(Host), TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        var run = CandidateDiscoveryRun.ForScanTest(scan, socket, () => true, TimeSpan.FromSeconds(10));
        await socket.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.DisposeAsync().AsTask());
        Assert.True(socket.Disposed);
        Assert.IsType<IOException>(run.Error);
        Assert.Equal(2, socket.QueryCount);
    }

    [Fact]
    public async Task StoppingDoesNotWaitForAStalledHardwareCheck()
    {
        var socket = new FakeTransport(Host, 53000, false);
        var scan = DiscoveryScanSession.ForTest(Binding(Host), TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCalls = 0;
        var run = CandidateDiscoveryRun.ForScanTest(scan, socket, () =>
        {
            if (Interlocked.Increment(ref activeCalls) == 2) { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); }
            return true;
        }, TimeSpan.FromSeconds(10));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = Task.Run(async () => await run.DisposeAsync());
        try
        {
            // Cancellation and socket closure should begin promptly even if completion
            // must still drain the stalled synchronous hardware call.
            await socket.DisposedSignal.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(0, socket.QueryCount);
        Assert.True(socket.Disposed);
    }

    [Fact]
    public async Task StopClosesSocketWhileSendPreparationIsStalledAndCancelsThatQuery()
    {
        var socket = new FakeTransport(Host, 53000, false) { BlockSynchronouslyOnQuery = 1 };
        var scan = DiscoveryScanSession.ForTest(Binding(Host), TimeSpan.FromSeconds(10), () => DateTimeOffset.UtcNow);
        var run = CandidateDiscoveryRun.ForScanTest(scan, socket, () => true, TimeSpan.FromSeconds(10));
        await socket.SyncEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = Task.Run(async () => await run.DisposeAsync());
        try { await socket.DisposedSignal.Task.WaitAsync(TimeSpan.FromMilliseconds(500)); }
        finally { socket.SyncRelease.Set(); await stop.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Null(run.Error);
        Assert.True(socket.Disposed);
        Assert.False(await socket.Queries.Reader.WaitToReadAsync());
    }

    private sealed class FakeTransport(IPAddress address, int port, bool responder) : ICandidateDiscoveryTransport
    {
        private readonly Channel<CandidateDiscoveryDatagram> incoming = Channel.CreateUnbounded<CandidateDiscoveryDatagram>();
        public readonly Channel<(byte[] Packet, long Timestamp)> Queries = Channel.CreateUnbounded<(byte[], long)>();
        private readonly IPAddress localAddress = address;
        private readonly int localPort = port;
        private int replies, replyAttempts, queryCount, received;
        private readonly TaskCompletionSource releaseBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource DisposedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource SyncEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim SyncRelease = new();
        public FakeTransport? Other { get; set; }
        public int Replies => Volatile.Read(ref replies);
        public int ReplyAttempts => Volatile.Read(ref replyAttempts);
        public int QueryCount => Volatile.Read(ref queryCount);
        public int Received => Volatile.Read(ref received);
        public bool Flood { get; init; }
        public bool Disposed { get; private set; }
        public int FailOnQuery { get; init; }
        public int BlockOnQuery { get; init; }
        public bool FailOnDispose { get; init; }
        public int BlockSynchronouslyOnQuery { get; init; }
        public Task SendQueryAsync(DiscoveryScanSession scan, CancellationToken ct) =>
            SendQueryGuardedAsync(scan, ct, send => send());
        public Task SendQueryGuardedAsync(DiscoveryScanSession scan, CancellationToken ct, Func<Func<Task>, Task> start)
        {
            ct.ThrowIfCancellationRequested();
            if (responder) throw new InvalidOperationException();
            if (Interlocked.Increment(ref queryCount) == FailOnQuery) throw new IOException("synthetic send failure");
            var data = scan.CreateQuery();
            if (QueryCount == BlockSynchronouslyOnQuery)
            {
                SyncEntered.TrySetResult();
                SyncRelease.Wait(TimeSpan.FromSeconds(5));
            }
            return start(() =>
            {
                if (QueryCount == BlockOnQuery)
                {
                    Blocked.TrySetResult();
                    return BlockedSendAsync();
                }
                Queries.Writer.TryWrite((data, Stopwatch.GetTimestamp()));
                Other?.incoming.Writer.TryWrite(new(data, new IPEndPoint(localAddress, localPort), DiscoveryProtocol.MulticastGroup, 12));
                return Task.CompletedTask;
            });
        }
        private async Task BlockedSendAsync()
        {
            await releaseBlocked.Task;
            throw new ObjectDisposedException("synthetic socket");
        }
        public Task ReplyToReceivedQueryAsync(DiscoveryResponderSession session, CandidateDiscoveryDatagram packet, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var peer = Other;
            if (!responder || peer is null || !packet.Source.Address.Equals(peer.localAddress) || packet.Source.Port != peer.localPort)
                throw new InvalidOperationException();
            var data = session.Reply(packet.Data, packet.Source, packet.Destination, packet.InterfaceIndex);
            Interlocked.Increment(ref replyAttempts);
            if (data is not null)
            {
                Interlocked.Increment(ref replies);
                peer.incoming.Writer.TryWrite(new(data, new IPEndPoint(localAddress, localPort), packet.Source.Address, 12));
            }
            return Task.CompletedTask;
        }
        public async Task<CandidateDiscoveryDatagram> ReceiveAsync(CancellationToken ct)
        {
            if (Flood)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref received);
                return new([1], new IPEndPoint(Peer, DiscoveryProtocol.Port), localAddress, 12);
            }
            return await incoming.Reader.ReadAsync(ct);
        }
        public void Dispose()
        {
            Disposed = true; incoming.Writer.TryComplete(); Queries.Writer.TryComplete(); releaseBlocked.TrySetResult(); DisposedSignal.TrySetResult();
            if (FailOnDispose) throw new IOException("synthetic cleanup failure");
        }
    }
}
