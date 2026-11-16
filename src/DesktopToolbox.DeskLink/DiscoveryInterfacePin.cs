using System.Buffers.Binary;
using System.Net.Sockets;

namespace DesktopToolbox.DeskLink;

internal enum DiscoveryNativePlatform { Windows, MacOS }

/// <summary>Small injectable boundary for platform-native IPv4 socket options.</summary>
internal interface IDiscoveryRawSocket : IDisposable
{
    void Set(int level, int option, byte[] value);
    int Get(int level, int option, byte[] value);
}

/// <summary>
/// Pins unicast output to one interface and verifies the platform reports the
/// selected index. It does not prove packet routing or grant discovery access.
/// </summary>
internal static class DiscoveryInterfacePin
{
    private const int IpProtoIp = 0;
    private const int WindowsIpUnicastIf = 31;
    private const int MacIpBoundIf = 25;

    internal static void Configure(Socket socket, int interfaceIndex)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var raw = new SocketOptions(socket);
        if (OperatingSystem.IsWindows()) Configure(raw, DiscoveryNativePlatform.Windows, interfaceIndex);
        else if (OperatingSystem.IsMacOS()) Configure(raw, DiscoveryNativePlatform.MacOS, interfaceIndex);
        else { raw.Dispose(); throw new PlatformNotSupportedException("Unicast interface pinning is not supported on this platform."); }
    }

    internal static void Configure(IDiscoveryRawSocket socket, DiscoveryNativePlatform platform, int interfaceIndex)
    {
        ArgumentNullException.ThrowIfNull(socket);
        try
        {
            if (interfaceIndex <= 0) throw new ArgumentOutOfRangeException(nameof(interfaceIndex));
            int option;
            byte[] value;
            switch (platform)
            {
                case DiscoveryNativePlatform.Windows:
                    option = WindowsIpUnicastIf;
                    value = new byte[sizeof(int)];
                    BinaryPrimitives.WriteInt32BigEndian(value, interfaceIndex);
                    break;
                case DiscoveryNativePlatform.MacOS:
                    option = MacIpBoundIf;
                    value = BitConverter.GetBytes(interfaceIndex);
                    break;
                default:
                    throw new NotSupportedException("Unicast interface pinning is not supported on this platform.");
            }
            socket.Set(IpProtoIp, option, value);
            var actual = new byte[sizeof(int)];
            if (socket.Get(IpProtoIp, option, actual) != actual.Length || BitConverter.ToInt32(actual) != interfaceIndex)
                throw new InvalidOperationException("The selected unicast interface could not be verified.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class SocketOptions(Socket socket) : IDiscoveryRawSocket
    {
        public void Set(int level, int option, byte[] value) => socket.SetRawSocketOption(level, option, value);
        public int Get(int level, int option, byte[] value) => socket.GetRawSocketOption(level, option, value);
        public void Dispose() => socket.Dispose();
    }
}
