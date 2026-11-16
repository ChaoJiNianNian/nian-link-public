using System.Net;
using System.Net.NetworkInformation;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class VerifiedLanBindingTests
{
    [Fact]
    public void OnlyExplicitlyApprovedActivePhysicalPrivateIpv4IsAccepted()
    {
        var address = IPAddress.Parse("192.168.40.20");
        var good = new LanInterfaceSnapshot("wifi", "wireless", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, [address]);
        Assert.Equal(address, VerifiedLanBinding.FromSnapshots(address, true, [good]).Address);
        Assert.Throws<InvalidOperationException>(() => VerifiedLanBinding.FromSnapshots(address, false, [good]));
        Assert.Throws<InvalidOperationException>(() => VerifiedLanBinding.FromSnapshots(address, true, [good with { Status = OperationalStatus.Down }]));
        Assert.Throws<InvalidOperationException>(() => VerifiedLanBinding.FromSnapshots(address, true, [good with { Description = "WireGuard VPN" }]));
        Assert.Throws<InvalidOperationException>(() => VerifiedLanBinding.FromSnapshots(IPAddress.Any, true, [good]));
        Assert.Throws<InvalidOperationException>(() => VerifiedLanBinding.FromSnapshots(IPAddress.Parse("8.8.8.8"), true, [good]));
    }
}
