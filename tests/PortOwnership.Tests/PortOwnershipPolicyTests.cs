using System.Buffers.Binary;
using DesktopToolbox.Windows.DeskSwitch;

namespace PortOwnership.Tests;

public sealed class PortOwnershipPolicyTests
{
    [Fact]
    public void ReadsActualWindowsListenerOwnerWithoutChangingTheListener()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Contains(TcpListenerOwners.ReadAll(), entry => entry.Port == port && entry.ProcessId == (uint)Environment.ProcessId);
    }

    [Fact]
    public void OwnCollaborationListenerDoesNotPreventRestart()
    {
        Assert.True(PortOwnershipPolicy.IsSafe([new(24802, 42)], 42));
        Assert.True(PortOwnershipPolicy.IsSafe([new(99, 7)], 42));
    }

    [Theory]
    [InlineData(24800, 42)]
    [InlineData(24801, 42)]
    [InlineData(24800, 9)]
    [InlineData(24801, 9)]
    [InlineData(24802, 9)]
    [InlineData(24802, 0)]
    public void UnexpectedOrUnknownOwnersAreRejected(int port, uint owner)
    {
        Assert.False(PortOwnershipPolicy.IsSafe([new(port, owner)], 42));
    }

    [Fact]
    public void ASecondForeignListenerIsRejectedEvenIfOwnListenerIsPresent()
    {
        Assert.False(PortOwnershipPolicy.IsSafe([new(24802, 42), new(24802, 7)], 42));
        Assert.False(PortOwnershipPolicy.IsSafe([], 0));
    }

    [Theory]
    [InlineData(2, 24, 8, 20)]
    [InlineData(23, 56, 20, 52)]
    public void CorrectlyReadsNetworkOrderPortAndLittleEndianOwner(int family, int rowSize, int portOffset, int ownerOffset)
    {
        var table = new byte[4 + rowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4 + portOffset, 2), 24802);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4 + ownerOffset, 4), 123456);
        Assert.Equal(new ListenerOwner(24802, 123456), Assert.Single(PortOwnershipPolicy.ParseTable(table, family)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(23)]
    public void TruncatedAndOverstatedTablesAreRejected(int family)
    {
        Assert.Throws<InvalidDataException>(() => PortOwnershipPolicy.ParseTable([0, 0, 0], family));
        Assert.Throws<InvalidDataException>(() => PortOwnershipPolicy.ParseTable([1, 0, 0, 0], family));
        Assert.Throws<InvalidDataException>(() => PortOwnershipPolicy.ParseTable([255, 255, 255, 255], family));
    }

    [Fact]
    public void UnsupportedFamilyIsRejected() => Assert.Throws<ArgumentException>(() => PortOwnershipPolicy.ParseTable([0, 0, 0, 0], 99));
}
