using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>Reads the OS listener table without probing or changing another process.</summary>
internal static class TcpListenerOwners
{
    private const int AfInet = 2, AfInet6 = 23, ListenerOwnerTable = 3;
    private const uint InsufficientBuffer = 122;
    private const int MaximumTableBytes = 16 * 1024 * 1024;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);

    public static ListenerOwner[] ReadAll()
    {
        // Both families must succeed. Failure or uncertainty must never silently
        // treat a possibly occupied listener as a safe local collaboration socket.
        try { return ReadFamily(AfInet).Concat(ReadFamily(AfInet6)).ToArray(); }
        catch (Exception error) when (error is Win32Exception or InvalidDataException or OverflowException or DllNotFoundException or EntryPointNotFoundException)
        { throw new InvalidOperationException("无法确认互联端口的占用进程，请检查系统网络状态后重试。", error); }
    }

    private static ListenerOwner[] ReadFamily(int family)
    {
        var size = 0;
        var result = GetExtendedTcpTable(0, ref size, false, family, ListenerOwnerTable, 0);
        if (result != InsufficientBuffer && result != 0) throw new Win32Exception((int)result);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size is < 4 or > MaximumTableBytes) throw new InvalidDataException("TCP owner table has an invalid size.");
            var capacity = size;
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, family, ListenerOwnerTable, 0);
                if (result == InsufficientBuffer) continue; // Table grew during capture; retry boundedly.
                if (result != 0) throw new Win32Exception((int)result);
                if (size is < 4 || size > capacity) throw new InvalidDataException("TCP owner table reported an invalid length.");
                var bytes = new byte[size]; Marshal.Copy(buffer, bytes, 0, size);
                return PortOwnershipPolicy.ParseTable(bytes, family);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidDataException("TCP owner table changed repeatedly while reading.");
    }
}
