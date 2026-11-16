using System.Net;
using System.Net.NetworkInformation;
using DesktopToolbox.DeskLink;
using NianLink.CandidateDiscovery;

namespace ConditionalCommitIsolation.Tests;

public sealed class HardwareInterfaceIsolationTests
{
    [Fact]
    public void SyntheticBindingCannotBePromotedEvenIfNativeQueryWouldMatch()
    {
        var id = Guid.NewGuid();
        var address = IPAddress.Parse("192.168.20.10");
        var adapter = new DiscoveryNetworkInterface(id.ToString(), "synthetic", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, 12, address, IPAddress.Parse("255.255.255.0"));
        var binding = DiscoveryNetworkBinding.FromSnapshotsForTest(address, () => [adapter]);
        bool queried = false;
        var result = WindowsHardwareInterface.IsHardware(binding, index =>
        {
            queried = true;
            return new NativeHardwareInterfaceSnapshot(0, (uint)index, id, 1);
        });
        Assert.False(result);
        Assert.False(queried);
    }
}
