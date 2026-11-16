using System.Net.NetworkInformation;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class DiscoveryInterfacePinTests
{
    private sealed class FakeRaw : IDiscoveryRawSocket
    {
        public byte[] SetBytes = []; public int Level, Option; public byte[] ReturnBytes = BitConverter.GetBytes(258); public bool Disposed;
        public void Set(int level, int option, byte[] bytes) { Level = level; Option = option; SetBytes = bytes; }
        public int Get(int level, int option, byte[] buffer) { ReturnBytes.CopyTo(buffer, 0); return ReturnBytes.Length; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void WindowsWritesNetworkByteOrderAndRequiresHostOrderReadback()
    {
        var socket = new FakeRaw(); DiscoveryInterfacePin.Configure(socket, DiscoveryNativePlatform.Windows, 258);
        Assert.Equal(new byte[] { 0, 0, 1, 2 }, socket.SetBytes); Assert.Equal(31, socket.Option); Assert.Equal(0, socket.Level); Assert.False(socket.Disposed);
    }
    [Fact]
    public void MacUsesNativeByteOrderAndDifferentNativeOption()
    {
        var socket = new FakeRaw(); DiscoveryInterfacePin.Configure(socket, DiscoveryNativePlatform.MacOS, 258);
        Assert.Equal(BitConverter.GetBytes(258), socket.SetBytes); Assert.Equal(25, socket.Option); Assert.False(socket.Disposed);
    }
    [Fact]
    public void IncorrectReadbackAndUnknownPlatformDisposeSocket()
    {
        var socket = new FakeRaw { ReturnBytes = BitConverter.GetBytes(259) };
        Assert.Throws<InvalidOperationException>(() => DiscoveryInterfacePin.Configure(socket, DiscoveryNativePlatform.Windows, 258)); Assert.True(socket.Disposed);
        var unsupported = new FakeRaw();
        Assert.Throws<NotSupportedException>(() => DiscoveryInterfacePin.Configure(unsupported, (DiscoveryNativePlatform)99, 1)); Assert.True(unsupported.Disposed);
    }
    [Fact]
    public void WindowsLoopbackSocketOptionIsSetAndReadBackWithoutNetworkTraffic()
    {
        if (!OperatingSystem.IsWindows()) return;
        var index = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback && x.OperationalStatus == OperationalStatus.Up).Select(x => x.GetIPProperties().GetIPv4Properties()?.Index ?? 0).First(x => x > 0);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        DiscoveryInterfacePin.Configure(socket, index);
        Assert.False(socket.SafeHandle.IsClosed);
    }
}
