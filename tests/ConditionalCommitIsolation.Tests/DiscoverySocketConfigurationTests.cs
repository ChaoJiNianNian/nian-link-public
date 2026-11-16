using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace ConditionalCommitIsolation.Tests;

public sealed class DiscoverySocketConfigurationTests
{
    [Fact]
    public void SyntheticMappingCannotOpenPrivateSocket()
    {
        var address = IPAddress.Parse("192.168.20.10");
        var selected = new DiscoveryNetworkInterface("synthetic", "fake", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, 7, address, IPAddress.Parse("255.255.255.0"));
        var binding = DiscoveryNetworkBinding.FromSnapshotsForTest(address, () => [selected]);
        Assert.Throws<InvalidOperationException>(() => DiscoveryUdpSocket.OpenForWindowsConfigurationTest(binding, true));
    }

    [LocalConfigurationFact]
    public async Task RealApprovedInterfaceCanBindFixedPortWithoutSendingOrJoiningGroup()
    {
        var addresses = VerifiedLanBinding.GetEligibleAddresses();
        var address = Assert.Single(addresses);
        var approved = VerifiedLanBinding.VerifyCurrentNetwork(address, userApproved: true);
        var binding = DiscoveryNetworkBinding.CreateForCurrentNetwork(approved);
        Assert.True(binding.IsLive);
        Assert.True(binding.IsStillAvailable());
        Assert.Throws<NotSupportedException>(() => DiscoveryUdpSocket.Open(approved, true));
        Assert.Throws<InvalidOperationException>(() => DiscoveryUdpSocket.OpenForWindowsConfigurationTest(binding, false));

        using (var socket = DiscoveryUdpSocket.OpenForWindowsConfigurationTest(binding, true))
        {
            Assert.Equal(DiscoveryUdpSocket.DiscoveryPort, socket.Port);
            using var competing = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            competing.ExclusiveAddressUse = true;
            var bindConflict = Assert.Throws<SocketException>(() => competing.Bind(new IPEndPoint(address, DiscoveryUdpSocket.DiscoveryPort)));
            Assert.Contains(bindConflict.SocketErrorCode, new[] { SocketError.AddressAlreadyInUse, SocketError.AccessDenied });
            var conflict = Assert.Throws<SocketException>(() => DiscoveryUdpSocket.OpenForWindowsConfigurationTest(binding, true));
            Assert.Contains(conflict.SocketErrorCode, new[] { SocketError.AddressAlreadyInUse, SocketError.AccessDenied });
            await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SendQueryAsync([1], DiscoveryUdpSocket.DiscoveryPort, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SendReplyAsync([1], new IPEndPoint(address, 9), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => socket.ReceiveAsync(CancellationToken.None));
        }
        using var replacement = DiscoveryUdpSocket.OpenForWindowsConfigurationTest(binding, true);
        Assert.Equal(DiscoveryUdpSocket.DiscoveryPort, replacement.Port);
    }
}

public sealed class LocalConfigurationFactAttribute : FactAttribute
{
    public LocalConfigurationFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("NIAN_SOCKET_CONFIGURATION_TEST") != "1")
            Skip = "Windows selected-interface configuration requires explicit local test opt-in.";
    }
}
