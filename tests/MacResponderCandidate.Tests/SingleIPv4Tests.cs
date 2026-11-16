using System.Net;
using System.Net.NetworkInformation;
using NianLink.MacResponderCandidate;

public sealed class SingleIPv4Tests
{
    private static readonly IPAddress Address = IPAddress.Parse("192.168.8.2");
    private static InterfaceIPv4Snapshot Selected(params IPAddress[] addresses) => new("en1", 7, OperationalStatus.Up, addresses);

    [Fact]
    public void RequiresOneAddressOnUniqueApprovedInterface()
    {
        Assert.True(SingleIPv4Policy.Matches([Selected(Address)], "en1", 7, Address));
        Assert.False(SingleIPv4Policy.Matches([Selected(Address, IPAddress.Parse("192.168.8.9"))], "en1", 7, Address));
        Assert.False(SingleIPv4Policy.Matches([Selected(IPAddress.Parse("192.168.8.9"))], "en1", 7, Address));
        Assert.False(SingleIPv4Policy.Matches([Selected(Address), new("en2", 7, OperationalStatus.Up, [IPAddress.Parse("192.168.9.2")])], "en1", 7, Address));
        Assert.False(SingleIPv4Policy.Matches([Selected(Address), Selected(Address)], "en1", 7, Address));
        Assert.False(SingleIPv4Policy.Matches([new("en1", 7, OperationalStatus.Down, [Address])], "en1", 7, Address));
        Assert.False(SingleIPv4Policy.Matches([], "en1", 7, Address));
    }
}
