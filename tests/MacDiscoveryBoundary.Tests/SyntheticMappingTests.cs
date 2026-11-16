using System.Net;
using System.Net.NetworkInformation;
using NianLink.MacDiscoveryBoundary;

namespace MacDiscoveryBoundary.Tests;

public sealed class SyntheticMappingTests
{
    private static readonly IPAddress Address = IPAddress.Parse("192.168.20.10");
    private static SyntheticInterface Good() => new("en0", 12, NetworkInterfaceType.Wireless80211, OperationalStatus.Up,
        Address, IPAddress.Parse("255.255.255.0"));
    private static SyntheticHardware Evidence() => new("en0", 12, true);

    [Fact]
    public void ValidSyntheticMappingNeverProducesLiveAuthorization()
    {
        var binding = SyntheticMacMapping.Create(Address, () => [Good()], () => [Evidence()]);
        Assert.True(binding.IsStillValid());
        Assert.False(binding.HasLiveNetworkOrigin);
        Assert.Equal(49473, SyntheticMacMapping.ProtocolPort);
        Assert.Equal(IPAddress.Parse("239.255.248.82"), SyntheticMacMapping.ProtocolGroup);
    }

    [Fact]
    public void NamesAndNetworkTypesDoNotProveHardware()
    {
        Assert.Throws<InvalidOperationException>(() => SyntheticMacMapping.Create(Address, () => [Good()], () => []));
        Assert.Throws<InvalidOperationException>(() => SyntheticMacMapping.Create(Address, () => [Good()], () => [Evidence() with { Physical = false }]));
        Assert.Throws<InvalidOperationException>(() => SyntheticMacMapping.Create(Address, () => [Good()], () => [Evidence(), Evidence()]));
        Assert.Throws<InvalidOperationException>(() => SyntheticMacMapping.Create(Address, () => [Good()], () => [Evidence() with { Index = 13 }]));
        Assert.Throws<InvalidOperationException>(() => SyntheticMacMapping.Create(Address, () => [Good()], () => [Evidence(), Evidence() with { Name = "en1" }]));
    }

    [Fact]
    public void MutatingCallerAddressDoesNotRewriteFrozenBaseline()
    {
        var address = IPAddress.Parse("192.168.20.10");
        var item = Good() with { Address = address };
        var binding = SyntheticMacMapping.Create(address, () => [item], () => [Evidence()]);
#pragma warning disable CS0618
        address.Address = IPAddress.Parse("192.168.20.11").Address;
#pragma warning restore CS0618
        Assert.False(binding.IsStillValid());
    }

    [Fact]
    public void InvalidAndAmbiguousMappingsAreRejected()
    {
        var good = Good();
        Assert.Throws<InvalidOperationException>(() => SyntheticMacMapping.Create(Address, () => [good, good], () => [Evidence()]));
        Assert.Throws<InvalidOperationException>(() => SyntheticMacMapping.Create(Address, () => [good, good with { Name = "en1", Address = IPAddress.Parse("192.168.20.11") }], () => [Evidence()]));
        Assert.Throws<InvalidOperationException>(() => SyntheticMacMapping.Create(Address, () => [good with { Mask = IPAddress.Parse("255.0.255.0") }], () => [Evidence()]));
        Assert.Throws<InvalidOperationException>(() => SyntheticMacMapping.Create(Address, () => [good with { Status = OperationalStatus.Down }], () => [Evidence()]));
    }

    [Theory]
    [InlineData("8.8.8.8", "255.255.255.0")]
    [InlineData("127.0.0.1", "255.0.0.0")]
    [InlineData("192.168.20.0", "255.255.255.0")]
    [InlineData("192.168.20.255", "255.255.255.0")]
    [InlineData("192.168.20.10", "255.255.255.254")]
    [InlineData("::1", "255.255.255.0")]
    public void InvalidSubnetNeverCreatesMapping(string address, string mask)
    {
        var item = Good() with { Address = IPAddress.Parse(address), Mask = IPAddress.Parse(mask) };
        Assert.Throws<InvalidOperationException>(() => SyntheticMacMapping.Create(item.Address, () => [item], () => [Evidence()]));
    }

    [Fact]
    public void HardwareReaderFailurePermanentlyInvalidates()
    {
        var fail = false;
        var binding = SyntheticMacMapping.Create(Address, () => [Good()], () => fail ? throw new IOException() : [Evidence()]);
        fail = true; Assert.False(binding.IsStillValid());
        fail = false; Assert.False(binding.IsStillValid());
    }

    [Fact]
    public void BoundaryExposesNoPublicTypes()
        => Assert.Empty(typeof(SyntheticMacMapping).Assembly.GetExportedTypes());

    [Theory]
    [InlineData("name")][InlineData("index")][InlineData("type")][InlineData("address")][InlineData("mask")][InlineData("status")][InlineData("hardware")][InlineData("error")]
    public void AnyChangeOrReadFailurePermanentlyInvalidates(string change)
    {
        var current = Good(); var hardware = Evidence(); var fail = false;
        var binding = SyntheticMacMapping.Create(Address, () => fail ? throw new InvalidOperationException() : [current], () => [hardware]);
        switch (change)
        {
            case "name": current = current with { Name = "en1" }; break;
            case "index": current = current with { Index = 13 }; break;
            case "type": current = current with { Type = NetworkInterfaceType.Ethernet }; break;
            case "address": current = current with { Address = IPAddress.Parse("192.168.20.11") }; break;
            case "mask": current = current with { Mask = IPAddress.Parse("255.255.0.0") }; break;
            case "status": current = current with { Status = OperationalStatus.Down }; break;
            case "hardware": hardware = hardware with { Physical = false }; break;
            case "error": fail = true; break;
        }
        Assert.False(binding.IsStillValid());
        current = Good(); hardware = Evidence(); fail = false;
        Assert.False(binding.IsStillValid());
    }
}
