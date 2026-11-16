using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Channels;
using DesktopToolbox.DeskLink;
using NianLink.CandidateDiscovery;

namespace PairingDiagnostics.Tests;

public sealed class PairingDiagnosticsTests
{
    private static readonly IPAddress Host = IPAddress.Parse("192.168.20.10"), Peer = IPAddress.Parse("192.168.20.11");
    private static DiscoveryNetworkBinding Binding() => DiscoveryNetworkBinding.FromSnapshotsForTest(Peer,
        () => [new DiscoveryNetworkInterface("synthetic", "test", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 12, Peer, IPAddress.Parse("255.255.255.0"))]);
    private static PeerIdentityOffer Offer() => new() { Name = "synthetic", Platform = "windows", KeyboardFingerprint = new string('A', 64),
        Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) };

#if FILELESS_PAIRING_DIAGNOSTICS
    [Fact]
    public async Task DiagnosticTracksRejectedAndAcceptedUnicastWithoutAlteringSecurityAndResetsPerRun()
    {
        var binding = Binding(); var offer = Offer(); var transport = new FakeTransport();
        var session = DiscoveryResponderSession.ForTest(binding, offer, () => DateTimeOffset.UtcNow);
        await using var run = CandidateDiscoveryRun.ForResponderWithPairingTest(session, binding, offer, transport, () => true, TimeSpan.FromSeconds(10));
        Assert.Equal((0, 0, 0), (run.GetPairingDiagnostics().Observed, run.GetPairingDiagnostics().Rejected, run.GetPairingDiagnostics().Accepted));
        transport.Push(new([1, 2, 3], new IPEndPoint(Host, 50000), Peer, 12));
        var now = DateTimeOffset.UtcNow;
        var valid = new PairingDiscoveryNotice("host", offer.Name, new string('B', 64), Guid.ParseExact(offer.Nonce, "N"),
            Guid.NewGuid(), Guid.NewGuid(), 53123, now.AddSeconds(20), now.AddSeconds(45));
        transport.Push(new(valid.Encode(now), new IPEndPoint(Host, 50000), Peer, 12));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (run.GetPairingDiagnostics().Observed < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
        var result = run.GetPairingDiagnostics();
        Assert.Equal((2, 1, 1), (result.Observed, result.Rejected, result.Accepted));
        Assert.NotNull(run.GetPendingPairingRequest());
        await run.DisposeAsync();
        Assert.Equal(result, run.GetPairingDiagnostics());
        var secondTransport = new FakeTransport();
        var secondSession = DiscoveryResponderSession.ForTest(binding, offer, () => DateTimeOffset.UtcNow);
        await using var next = CandidateDiscoveryRun.ForResponderWithPairingTest(secondSession, binding, offer, secondTransport, () => true, TimeSpan.FromSeconds(10));
        Assert.Equal((0, 0, 0), (next.GetPairingDiagnostics().Observed, next.GetPairingDiagnostics().Rejected, next.GetPairingDiagnostics().Accepted));
    }

    [Fact]
    public async Task WrongInterfaceExpiredAndWrongNonceRemainRejected()
    {
        var binding = Binding(); var offer = Offer(); var transport = new FakeTransport();
        var session = DiscoveryResponderSession.ForTest(binding, offer, () => DateTimeOffset.UtcNow);
        await using var run = CandidateDiscoveryRun.ForResponderWithPairingTest(session, binding, offer, transport, () => true, TimeSpan.FromSeconds(10));
        var now = DateTimeOffset.UtcNow;
        var valid = new PairingDiscoveryNotice("host", offer.Name, new string('B', 64), Guid.ParseExact(offer.Nonce, "N"),
            Guid.NewGuid(), Guid.NewGuid(), 53123, now.AddSeconds(20), now.AddSeconds(45));
        transport.Push(new(valid.Encode(now), new IPEndPoint(Host, 50000), Peer, 13));
        var earlier = now.AddSeconds(-25);
        transport.Push(new((valid with { ExpiresAtUtc = earlier.AddSeconds(20) }).Encode(earlier), new IPEndPoint(Host, 50000), Peer, 12));
        transport.Push(new((valid with { OfferNonce = Guid.NewGuid() }).Encode(now), new IPEndPoint(Host, 50000), Peer, 12));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (run.GetPairingDiagnostics().Observed < 3 && DateTime.UtcNow < deadline) await Task.Delay(10);
        var result = run.GetPairingDiagnostics();
        Assert.Equal((3, 3, 0), (result.Observed, result.Rejected, result.Accepted));
        Assert.Null(run.GetPendingPairingRequest());
    }

    [Fact]
    public async Task ConcurrentSnapshotsAreConsistentAndStopFreezesCounters()
    {
        var binding = Binding(); var offer = Offer(); var transport = new FakeTransport();
        var session = DiscoveryResponderSession.ForTest(binding, offer, () => DateTimeOffset.UtcNow);
        await using var run = CandidateDiscoveryRun.ForResponderWithPairingTest(session, binding, offer, transport, () => true, TimeSpan.FromSeconds(10));
        for (var i = 0; i < 200; i++) transport.Push(new([1, 2, 3], new IPEndPoint(Host, 50000), Peer, 12));
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                var value = run.GetPairingDiagnostics();
                Assert.Equal(value.Observed, value.Accepted + value.Rejected);
                Assert.InRange(value.Observed, 0, 999);
            }
        });
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (run.GetPairingDiagnostics().Observed < 200 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await reader;
        Assert.Equal((200, 200, 0), (run.GetPairingDiagnostics().Observed, run.GetPairingDiagnostics().Rejected, run.GetPairingDiagnostics().Accepted));
        await run.DisposeAsync();
        var stopped = run.GetPairingDiagnostics();
        await Task.Delay(50);
        Assert.Equal(stopped, run.GetPairingDiagnostics());
    }

    [Fact]
    public async Task SaturatedRejectedCountStillRecordsLaterAcceptedRequest()
    {
        var binding = Binding(); var offer = Offer(); var transport = new FakeTransport();
        var session = DiscoveryResponderSession.ForTest(binding, offer, () => DateTimeOffset.UtcNow);
        await using var run = CandidateDiscoveryRun.ForResponderWithPairingTest(session, binding, offer, transport, () => true, TimeSpan.FromSeconds(10));
        for (var i = 0; i < 1000; i++) transport.Push(new([1, 2, 3], new IPEndPoint(Host, 50000), Peer, 12));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (run.GetPairingDiagnostics().Observed < 999 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(999, run.GetPairingDiagnostics().Rejected);
        await Task.Delay(1200); // Let the existing per-second limiter expire; do not change protocol policy.
        var now = DateTimeOffset.UtcNow;
        var valid = new PairingDiscoveryNotice("host", offer.Name, new string('B', 64), Guid.ParseExact(offer.Nonce, "N"),
            Guid.NewGuid(), Guid.NewGuid(), 53123, now.AddSeconds(20), now.AddSeconds(45));
        transport.Push(new(valid.Encode(now), new IPEndPoint(Host, 50000), Peer, 12));
        deadline = DateTime.UtcNow.AddSeconds(3);
        while (run.GetPairingDiagnostics().Accepted == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        var result = run.GetPairingDiagnostics();
        Assert.Equal((999, 999, 1), (result.Observed, result.Rejected, result.Accepted));
        Assert.NotNull(run.GetPendingPairingRequest());
    }
#endif

    [Fact]
    public async Task ActualResponderRejectsInvalidAndReplayWhileAcceptingValidInEitherBuild()
    {
        var binding = Binding(); var offer = Offer(); var transport = new FakeTransport();
        var session = DiscoveryResponderSession.ForTest(binding, offer, () => DateTimeOffset.UtcNow);
        await using var run = CandidateDiscoveryRun.ForResponderWithPairingTest(session, binding, offer, transport, () => true, TimeSpan.FromSeconds(10));
        var now = DateTimeOffset.UtcNow;
        var valid = new PairingDiscoveryNotice("host", offer.Name, new string('B', 64), Guid.ParseExact(offer.Nonce, "N"),
            Guid.NewGuid(), Guid.NewGuid(), 53123, now.AddSeconds(20), now.AddSeconds(45));
        var packet = valid.Encode(now);
        transport.Push(new([1, 2, 3], new IPEndPoint(Host, 50000), Peer, 12));
        await transport.WaitProcessedAsync(1);
        Assert.Null(run.GetPendingPairingRequest());
        transport.Push(new(packet, new IPEndPoint(Host, 50000), Peer, 13));
        await transport.WaitProcessedAsync(2);
        Assert.Null(run.GetPendingPairingRequest());
        transport.Push(new((valid with { OfferNonce = Guid.NewGuid() }).Encode(now), new IPEndPoint(Host, 50000), Peer, 12));
        await transport.WaitProcessedAsync(3);
        Assert.Null(run.GetPendingPairingRequest());
        transport.Push(new(packet, new IPEndPoint(Host, 50000), Peer, 12));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (run.GetPendingPairingRequest() is null && DateTime.UtcNow < deadline) await Task.Delay(10);
        var accepted = Assert.IsType<PairingIncomingRequest>(run.GetPendingPairingRequest());
        Assert.True(run.RejectPairingRequest(accepted.Id));
        transport.Push(new(packet, new IPEndPoint(Host, 50000), Peer, 12));
        await transport.WaitProcessedAsync(5);
        Assert.Null(run.GetPendingPairingRequest());
    }

    private sealed class FakeTransport : ICandidateDiscoveryTransport
    {
        private readonly Channel<CandidateDiscoveryDatagram> incoming = Channel.CreateUnbounded<CandidateDiscoveryDatagram>();
        private int delivered, processed;
        public void Push(CandidateDiscoveryDatagram packet) => incoming.Writer.TryWrite(packet);
        public async Task WaitProcessedAsync(int packets)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (Volatile.Read(ref processed) < packets && DateTime.UtcNow < deadline) await Task.Delay(5);
            Assert.True(Volatile.Read(ref processed) >= packets, "Receive loop did not finish processing packets.");
        }
        public Task SendQueryAsync(DiscoveryScanSession scan, CancellationToken ct) => Task.CompletedTask;
        public Task ReplyToReceivedQueryAsync(DiscoveryResponderSession responder, CandidateDiscoveryDatagram packet, CancellationToken ct) => Task.CompletedTask;
        public async Task<CandidateDiscoveryDatagram> ReceiveAsync(CancellationToken ct)
        {
            Interlocked.Exchange(ref processed, Volatile.Read(ref delivered));
            var packet = await incoming.Reader.ReadAsync(ct);
            Interlocked.Increment(ref delivered);
            return packet;
        }
        public void Dispose() => incoming.Writer.TryComplete();
    }
}
