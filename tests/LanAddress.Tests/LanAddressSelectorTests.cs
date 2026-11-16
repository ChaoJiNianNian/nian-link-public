using System.Net;
using System.Net.NetworkInformation;
using DesktopToolbox.Windows.DeskSwitch;

public sealed class LanAddressSelectorTests
{
    private static LanAdapterSnapshot Adapter(string name, string address, NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        OperationalStatus status = OperationalStatus.Up, string description = "physical") =>
        new(name, description, type, status, [IPAddress.Parse(address)]);

    [Fact]
    public void SelectsPhysicalLanEvenWhenGatewayVirtualAdapterIsEnumeratedFirst()
    {
        var items = new[] { Adapter("Meta", "198.18.84.2"), Adapter("Ethernet", "192.168.84.2") };
        Assert.Equal(IPAddress.Parse("192.168.84.2"), LanAddressSelector.Select(items));
    }

    [Theory]
    [InlineData("198.18.84.2")]
    [InlineData("169.254.2.1")]
    [InlineData("127.0.0.1")]
    [InlineData("8.8.8.8")]
    public void RejectsNonPrivateAddresses(string address) => Assert.Null(LanAddressSelector.Select([Adapter("Ethernet", address)]));

    [Theory]
    [InlineData("Meta", "198.18.84.2")]
    [InlineData("WireGuard Tunnel", "10.0.0.7")]
    [InlineData("vEthernet (Hyper-V)", "172.20.1.1")]
    [InlineData("VMware Network Adapter", "192.168.5.1")]
    public void RejectsVirtualAdaptersEvenIfAddressIsPrivate(string name, string address) =>
        Assert.Null(LanAddressSelector.Select([Adapter(name, address)]));

    [Fact]
    public void SelectsOnlyUpPhysicalEthernetOrWifi()
    {
        LanAdapterSnapshot[] items = [Adapter("Offline", "10.1.1.1", status: OperationalStatus.Down),
            Adapter("Tunnel", "10.2.1.1", NetworkInterfaceType.Tunnel), Adapter("Wi-Fi", "172.31.1.4", NetworkInterfaceType.Wireless80211)];
        Assert.Equal(IPAddress.Parse("172.31.1.4"), LanAddressSelector.Select(items));
    }

    [Fact]
    public void ActiveControlAddressWinsEvenWhenAnotherPhysicalAdapterComesFirst()
    {
        LanAdapterSnapshot[] items = [Adapter("Ethernet", "192.168.1.10"), Adapter("Wi-Fi", "10.0.0.6", NetworkInterfaceType.Wireless80211)];
        Assert.Equal(IPAddress.Parse("10.0.0.6"), LanAddressSelector.Select(items, "10.0.0.6"));
    }

    [Fact]
    public void MissingControlAddressDoesNotFallBackToAnotherNetwork()
    {
        LanAdapterSnapshot[] items = [Adapter("Ethernet", "192.168.1.10")];
        Assert.Null(LanAddressSelector.Select(items, "10.0.0.6"));
    }

    [Fact]
    public void MissingOrInvalidControlAddressCannotSelectVirtualAdapterOrWildcard()
    {
        LanAdapterSnapshot[] items = [Adapter("Meta", "10.10.0.1"), Adapter("Ethernet", "192.168.84.2")];
        Assert.Null(LanAddressSelector.Select(items, "10.10.0.1"));
        Assert.Null(LanAddressSelector.Select(items, "0.0.0.0"));
        Assert.Null(LanAddressSelector.Select(items, "not-an-address"));
    }

    [Fact]
    public void InterfaceEnumerationFailureProducesNoBindableAddress()
    {
        static IEnumerable<LanAdapterSnapshot> Failed()
        {
            yield return Adapter("Ethernet", "192.168.84.2");
            throw new NetworkInformationException();
        }
        Assert.Null(LanAddressSelector.SelectAvailable(Failed, "192.168.84.2"));
    }
}
