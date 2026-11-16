using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;
using NianLink.MacResponderCandidate;

public sealed class DatagramFilterTests
{
    [Fact]
    public void RequiresActualGroupInterfaceAndCompleteFrame()
    {
        var src = new IPEndPoint(IPAddress.Parse("192.168.8.3"), 49001);
        var data = new byte[] { 1, 2, 3 };
        Assert.True(MacResponderSocket.Accept(data.Length, SocketFlags.None, src, DiscoveryProtocol.MulticastGroup, 7, 7));
        Assert.False(MacResponderSocket.Accept(0, SocketFlags.None, src, DiscoveryProtocol.MulticastGroup, 7, 7));
        Assert.False(MacResponderSocket.Accept(DiscoveryAnnouncement.MaximumBytes + 1, SocketFlags.None, src, DiscoveryProtocol.MulticastGroup, 7, 7));
        Assert.False(MacResponderSocket.Accept(data.Length, SocketFlags.Truncated, src, DiscoveryProtocol.MulticastGroup, 7, 7));
        Assert.False(MacResponderSocket.Accept(data.Length, SocketFlags.ControlDataTruncated, src, DiscoveryProtocol.MulticastGroup, 7, 7));
        Assert.False(MacResponderSocket.Accept(data.Length, SocketFlags.None, src, null, 7, 7));
        Assert.False(MacResponderSocket.Accept(data.Length, SocketFlags.None, src, IPAddress.Loopback, 7, 7));
        Assert.False(MacResponderSocket.Accept(data.Length, SocketFlags.None, src, DiscoveryProtocol.MulticastGroup, 8, 7));
        Assert.False(MacResponderSocket.Accept(data.Length, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Loopback, 49001), DiscoveryProtocol.MulticastGroup, 7, 7));
    }

    [Fact]
    public void UnicastOnlyAcceptsTheApprovedLocalAddressAndInterface()
    {
        var src = new IPEndPoint(IPAddress.Parse("192.168.8.3"), 49001);
        var local = IPAddress.Parse("192.168.8.4");
        Assert.True(MacResponderSocket.Accept(128, SocketFlags.None, src, local, 7, 7, local));
        Assert.False(MacResponderSocket.Accept(128, SocketFlags.None, src, IPAddress.Parse("192.168.8.5"), 7, 7, local));
        Assert.False(MacResponderSocket.Accept(128, SocketFlags.None, src, local, 8, 7, local));
        Assert.False(MacResponderSocket.Accept(128, SocketFlags.ControlDataTruncated, src, local, 7, 7, local));
    }
}
