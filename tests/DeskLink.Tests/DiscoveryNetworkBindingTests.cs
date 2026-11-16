using System.Net;
using System.Net.NetworkInformation;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class DiscoveryNetworkBindingTests
{
    private static readonly IPAddress Address = IPAddress.Parse("192.168.20.10");
    private static readonly IPAddress Mask = IPAddress.Parse("255.255.255.0");
    private static DiscoveryNetworkInterface Good() => new("id-1", "wifi", NetworkInterfaceType.Wireless80211,
        OperationalStatus.Up, 12, Address, Mask);

    [Fact]
    public void AdapterWithoutIpv4DoesNotReadIndex()
    {
        var values = DiscoveryNetworkBinding.ProjectAdapter("", "wifi", NetworkInterfaceType.Wireless80211,
            OperationalStatus.Up, [(IPAddress.IPv6Loopback, IPAddress.Any)], () => throw new InvalidOperationException());
        Assert.Empty(values);
    }

    [Fact]
    public void AdapterWithIpv4RequiresIdAndIndex()
    {
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.ProjectAdapter("", "wifi", NetworkInterfaceType.Wireless80211,
            OperationalStatus.Up, [(Address, Mask)], () => 12));
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.ProjectAdapter("id", "wifi", NetworkInterfaceType.Wireless80211,
            OperationalStatus.Up, [(Address, Mask)], () => 0));
    }

    [Fact]
    public void RequiresExactlyOneUsablePrivateMapping()
    {
        var valid = Good();
        Assert.Equal(valid, DiscoveryNetworkBinding.Select(Address, [valid]));
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.Select(Address, []));
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.Select(Address, [valid, valid with { Id = "other" }]));
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.Select(Address, [valid with { Status = OperationalStatus.Down }]));
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.Select(Address, [valid with { Type = NetworkInterfaceType.Loopback }]));
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.Select(Address, [valid with { Mask = IPAddress.Parse("255.0.255.0") }]));
        var otherAddress = IPAddress.Parse("192.168.20.11");
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.Select(Address, [valid, valid with { Id = "other", Address = otherAddress }]));
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.Select(Address, [valid, valid with { Index = 13, Address = otherAddress }]));
        var external = valid with { Address = IPAddress.Parse("8.8.8.8") };
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.Select(external.Address, [external]));
    }

    [Fact]
    public void ApprovedAddressMovingToDifferentAdapterIsRejectedEvenWithSameName()
    {
        var approved = VerifiedLanBinding.FromSnapshots(Address, true,
            [new LanInterfaceSnapshot("wifi", "wireless", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, [Address], "id-1")]);
        Assert.Equal(Good(), DiscoveryNetworkBinding.SelectForApprovedNetwork(approved, [Good()]));
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.SelectForApprovedNetwork(approved, [Good() with { Name = "other" }]));
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.SelectForApprovedNetwork(approved, [Good() with { Id = "different-id" }]));
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.CreateForCurrentNetwork(approved));
    }

    [Fact]
    public void MissingIdAtApprovalCannotBeUpgradedToNetworkBinding()
    {
        var approved = VerifiedLanBinding.FromSnapshots(Address, true,
            [new LanInterfaceSnapshot("wifi", "wireless", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, [Address])]);
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.SelectForApprovedNetwork(approved, [Good()]));
    }

    [Fact]
    public void AmbiguousAtApprovalCannotBecomeValidWhenDuplicateDisappears()
    {
        var first = new LanInterfaceSnapshot("wifi", "wireless", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, [Address], "id-1");
        var duplicate = first with { Name = "other", Id = "id-2" };
        var approved = VerifiedLanBinding.FromSnapshots(Address, true, [first, duplicate]);
        Assert.Throws<InvalidOperationException>(() => DiscoveryNetworkBinding.SelectForApprovedNetwork(approved, [Good()]));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("index")]
    [InlineData("address")]
    [InlineData("mask")]
    [InlineData("status")]
    [InlineData("duplicate")]
    public void AnyMappingChangePermanentlyRevokes(string change)
    {
        var original = Good();
        var current = new[] { original };
        var binding = DiscoveryNetworkBinding.FromSnapshotsForTest(Address, () => current);
        Assert.False(binding.IsLive);
        Assert.True(binding.IsStillAvailable());
        var changed = change switch
        {
            "id" => original with { Id = "id-2" },
            "name" => original with { Name = "other" },
            "type" => original with { Type = NetworkInterfaceType.Ethernet },
            "index" => original with { Index = 13 },
            "address" => original with { Address = IPAddress.Parse("192.168.20.11") },
            "mask" => original with { Mask = IPAddress.Parse("255.255.0.0") },
            "status" => original with { Status = OperationalStatus.Down },
            _ => original
        };
        current = change == "duplicate" ? [original, changed] : [changed];
        Assert.False(binding.IsStillAvailable());
        current = [original];
        Assert.False(binding.IsStillAvailable());
    }

    [Fact]
    public void EnumerationFailurePermanentlyRevokes()
    {
        bool fail = false;
        var binding = DiscoveryNetworkBinding.FromSnapshotsForTest(Address, () => fail ? throw new NetworkInformationException() : [Good()]);
        Assert.True(binding.IsStillAvailable());
        fail = true;
        Assert.False(binding.IsStillAvailable());
        fail = false;
        Assert.False(binding.IsStillAvailable());
    }
}
