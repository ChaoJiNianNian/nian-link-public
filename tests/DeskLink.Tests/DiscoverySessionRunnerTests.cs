using System.Net;
using System.Threading.Channels;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class DiscoverySessionRunnerTests
{
    private sealed class FakeTransport : IDiscoveryDatagramTransport
    {
        internal readonly Channel<DiscoveryDatagram> Incoming = Channel.CreateUnbounded<DiscoveryDatagram>();
        internal readonly TaskCompletionSource<byte[]> Query = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<(byte[] Data, IPEndPoint Destination)> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public Task SendQueryAsync(byte[] data, int port, CancellationToken ct) { Assert.Equal(DiscoveryUdpSocket.DiscoveryPort, port); Query.TrySetResult(data); return Task.CompletedTask; }
        public Task SendReplyAsync(byte[] data, IPEndPoint destination, CancellationToken ct) { Reply.TrySetResult((data, destination)); return Task.CompletedTask; }
        public async Task<DiscoveryDatagram> ReceiveAsync(CancellationToken ct) => await Incoming.Reader.ReadAsync(ct);
        public void Dispose() { Disposed = true; Incoming.Writer.TryComplete(); }
    }

    [Fact]
    public async Task ScannerPublishesOnlyMatchingCandidateAndStopClearsIt()
    {
        var now = DateTimeOffset.UtcNow;
        var local = IPAddress.Parse("10.0.0.1"); var remote = IPAddress.Parse("10.0.0.2");
        var state = new DiscoveryScanState(new DiscoverySubnet(local, IPAddress.Parse("255.255.255.0")), 7, TimeSpan.FromSeconds(10), () => now);
        var wire = new FakeTransport(); using var stop = new CancellationTokenSource();
        var run = DiscoverySessionRunner.RunScannerAsync(wire, state, stop.Token);
        var query = await wire.Query.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(DiscoveryQuery.TryParse(query, now, out var parsed)); Assert.Equal(state.Nonce, parsed!.ScanNonce);
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(1) };
        var bytes = DiscoveryAnnouncement.Encode(offer, state.Nonce, now, now.AddSeconds(5));
        await wire.Incoming.Writer.WriteAsync(new(bytes, new(remote, DiscoveryUdpSocket.DiscoveryPort), local, 7));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (state.GetCandidates().Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.Single(state.GetCandidates());
        stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(state.GetCandidates()); Assert.True(wire.Disposed);
    }

    [Fact]
    public async Task ResponderRepliesOnlyToValidatedActualSourceAndStops()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 23456);
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(1) };
        var state = new DiscoveryResponderState(offer, DiscoveryUdpSocket.Group, 7, new DiscoverySubnet(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("255.255.255.0")), () => now);
        var wire = new FakeTransport(); using var stop = new CancellationTokenSource();
        var run = DiscoverySessionRunner.RunResponderAsync(wire, state, stop.Token);
        var query = DiscoveryQuery.Encode(Guid.NewGuid(), now, now.AddSeconds(10));
        await wire.Incoming.Writer.WriteAsync(new(query, source, DiscoveryUdpSocket.Group, 8));
        await wire.Incoming.Writer.WriteAsync(new(query, source, DiscoveryUdpSocket.Group, 7));
        var response = await wire.Reply.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(source, response.Destination);
        Assert.True(DiscoveryAnnouncement.TryParse(response.Data, now, out _));
        stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(wire.Disposed);
        Assert.Null(state.Reply(query, source, DiscoveryUdpSocket.Group, 7));
    }
}
