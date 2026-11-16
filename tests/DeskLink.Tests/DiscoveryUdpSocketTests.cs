using System.Net;
using System.Net.NetworkInformation;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class DiscoveryUdpSocketTests
{
    private static int LoopbackIndex() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback && x.OperationalStatus == OperationalStatus.Up)
        .Select(x => x.GetIPProperties().GetIPv4Properties()?.Index ?? 0).First(x => x > 0);

    [Fact]
    public async Task LoopbackQueryAndUnicastReplyCarryDestinationAndInterfaceInformation()
    {
        var index = LoopbackIndex();
        using var listener = DiscoveryUdpSocket.OpenForLoopbackTest(index, 0, responder: true);
        using var scanner = DiscoveryUdpSocket.OpenForLoopbackTest(index, 0, responder: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var pending = listener.ReceiveAsync(deadline.Token);
        await scanner.SendQueryAsync(new byte[] { 1, 2 }, listener.Port, deadline.Token);
        var query = await pending;
        Assert.Equal(new byte[] { 1, 2 }, query.Data);
        Assert.Equal(IPAddress.Loopback, query.Source.Address);
        Assert.Equal(scanner.Port, query.Source.Port);
        Assert.Equal(DiscoveryUdpSocket.Group, query.Destination);
        Assert.Equal(index, query.InterfaceIndex);
        var replyPending = scanner.ReceiveAsync(deadline.Token);
        await listener.SendReplyAsync(new byte[] { 3, 4 }, query.Source, deadline.Token);
        var reply = await replyPending;
        Assert.Equal(new byte[] { 3, 4 }, reply.Data);
        Assert.Equal(listener.Port, reply.Source.Port);
        Assert.Equal(IPAddress.Loopback, reply.Destination);
        Assert.Equal(index, reply.InterfaceIndex);
    }

    [Fact]
    public async Task ClosingReleasesExclusivePortAndInterruptsPendingReceive()
    {
        var index = LoopbackIndex();
        using var listener = DiscoveryUdpSocket.OpenForLoopbackTest(index, 0, responder: true);
        var port = listener.Port;
        Assert.ThrowsAny<Exception>(() => DiscoveryUdpSocket.OpenForLoopbackTest(index, port, responder: true));
        var pending = listener.ReceiveAsync(CancellationToken.None);
        listener.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(TimeSpan.FromSeconds(4)));
        using var replacement = DiscoveryUdpSocket.OpenForLoopbackTest(index, port, responder: true);
        Assert.Equal(port, replacement.Port);
    }
}

public sealed class DiscoveryUdpAvailabilityTests
{
    [Fact]
    public async Task NetworkLossWhileWaitingFailsClosedAndReleasesSocket()
    {
        var index = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback && x.OperationalStatus == OperationalStatus.Up).Select(x => x.GetIPProperties().GetIPv4Properties()?.Index ?? 0).First(x => x > 0);
        var valid = true;
        using var listener = DiscoveryUdpSocket.OpenForLoopbackTest(index, 0, responder: true, active: () => valid);
        var port = listener.Port;
        var pending = listener.ReceiveAsync(CancellationToken.None);
        valid = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(4)));
        using var replacement = DiscoveryUdpSocket.OpenForLoopbackTest(index, port, responder: true);
        Assert.Equal(port, replacement.Port);
    }
}

public sealed class DiscoveryLoopbackBoundaryTests
{
    [Fact]
    public async Task TestSocketNeverSendsUnicastOutsideLoopback()
    {
        var index = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback && x.OperationalStatus == OperationalStatus.Up).Select(x => x.GetIPProperties().GetIPv4Properties()?.Index ?? 0).First(x => x > 0);
        using var socket = DiscoveryUdpSocket.OpenForLoopbackTest(index, 0, responder: true);
        await Assert.ThrowsAsync<ArgumentException>(() => socket.SendReplyAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Parse("192.168.1.1"), 12345), CancellationToken.None));
    }
}
