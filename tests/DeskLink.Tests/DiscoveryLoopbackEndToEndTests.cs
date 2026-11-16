using System.Net;
using System.Net.NetworkInformation;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class DiscoveryLoopbackEndToEndTests
{
    [Fact]
    public async Task ActualUdpQueryAndReplyPassStateChecksAndStopClearsCandidate()
    {
        var index = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback && x.OperationalStatus == OperationalStatus.Up)
            .Select(x => x.GetIPProperties().GetIPv4Properties()?.Index ?? 0).First(x => x > 0);
        var listenAddress = IPAddress.Parse("127.0.0.1"); var scanAddress = IPAddress.Parse("127.0.0.2");
        var listenSocket = DiscoveryUdpSocket.OpenForLoopbackTest(index, 0, responder: true, local: listenAddress);
        var port = listenSocket.Port;
        var scanSocket = DiscoveryUdpSocket.OpenForLoopbackTest(index, 0, responder: false, local: scanAddress);
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "test", Platform = "windows", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddSeconds(30) };
        var responder = new DiscoveryResponderState(offer, DiscoveryUdpSocket.Group, index, DiscoverySubnet.LoopbackForTest(listenAddress));
        var scan = DiscoveryScanState.ForLoopbackTest(scanAddress, index, TimeSpan.FromSeconds(10), port);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var responderTask = DiscoverySessionRunner.RunResponderAsync(listenSocket, responder, stop.Token);
        var scanTask = DiscoverySessionRunner.RunScannerAsync(scanSocket, scan, stop.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (scan.GetCandidates().Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
            var candidate = Assert.Single(scan.GetCandidates());
            Assert.Equal(offer, candidate.Offer); Assert.Equal(listenAddress, candidate.Source);
        }
        finally { stop.Cancel(); await Task.WhenAll(responderTask, scanTask).WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Empty(scan.GetCandidates());
    }
}
