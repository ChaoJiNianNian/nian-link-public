using System.Net;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class DiscoverySubnetTests
{
    [Fact]
    public void OnlyOtherHostsInSelectedSubnetMayReceiveOrSubmitPackets()
    {
        var subnet = new DiscoverySubnet(IPAddress.Parse("192.168.84.4"), IPAddress.Parse("255.255.255.0"));
        Assert.True(subnet.Allows(IPAddress.Parse("192.168.84.5")));
        Assert.False(subnet.Allows(IPAddress.Parse("192.168.84.4")));
        Assert.False(subnet.Allows(IPAddress.Parse("192.168.84.0")));
        Assert.False(subnet.Allows(IPAddress.Parse("192.168.84.255")));
        Assert.False(subnet.Allows(IPAddress.Parse("192.168.11.5")));
        Assert.False(subnet.Allows(IPAddress.Parse("10.0.0.1")));
        Assert.False(subnet.Allows(IPAddress.Parse("8.8.8.8")));
    }
    [Fact]
    public void RejectsInvalidAndAmbiguousMasksAndLocalAddresses()
    {
        foreach (var mask in new[] { "0.0.0.0", "255.255.255.255", "255.255.255.254", "255.0.255.0" })
            Assert.Throws<ArgumentException>(() => new DiscoverySubnet(IPAddress.Parse("192.168.84.4"), IPAddress.Parse(mask)));
        Assert.Throws<ArgumentException>(() => new DiscoverySubnet(IPAddress.Parse("127.0.0.1"), IPAddress.Parse("255.0.0.0")));
        Assert.Throws<ArgumentException>(() => new DiscoverySubnet(IPAddress.Parse("8.8.8.8"), IPAddress.Parse("255.255.255.0")));
        Assert.Throws<ArgumentException>(() => new DiscoverySubnet(IPAddress.Parse("192.168.84.0"), IPAddress.Parse("255.255.255.0")));
        Assert.Throws<ArgumentException>(() => new DiscoverySubnet(IPAddress.Parse("192.168.84.255"), IPAddress.Parse("255.255.255.0")));
    }
}
