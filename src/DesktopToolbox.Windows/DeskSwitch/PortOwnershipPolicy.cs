using System.Buffers.Binary;

namespace DesktopToolbox.Windows.DeskSwitch;

internal readonly record struct ListenerOwner(int Port, uint ProcessId);

/// <summary>Fail closed: only this process's collaboration listener may already exist.</summary>
internal static class PortOwnershipPolicy
{
    public static bool IsSafe(IEnumerable<ListenerOwner> listeners, uint ownProcessId)
    {
        if (ownProcessId == 0) return false;
        foreach (var listener in listeners)
        {
            if (listener.Port is 24800 or 24801) return false;
            if (listener.Port == 24802 && (listener.ProcessId == 0 || listener.ProcessId != ownProcessId)) return false;
        }
        return true;
    }

    // GetExtendedTcpTable returns a DWORD count followed by native MIB_TCPROW_OWNER_PID
    // or MIB_TCP6ROW_OWNER_PID entries. Port bytes are always in network order.
    public static ListenerOwner[] ParseTable(ReadOnlySpan<byte> data, int addressFamily)
    {
        var (rowSize, localPortOffset, ownerOffset) = addressFamily switch
        {
            2 => (24, 8, 20), // AF_INET
            23 => (56, 20, 52), // AF_INET6
            _ => throw new ArgumentException("Unsupported TCP address family.", nameof(addressFamily))
        };
        if (data.Length < sizeof(uint)) throw new InvalidDataException("TCP owner table is truncated.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count > (data.Length - sizeof(uint)) / rowSize) throw new InvalidDataException("TCP owner table length is invalid.");
        var output = new ListenerOwner[(int)count];
        for (var index = 0; index < output.Length; index++)
        {
            var row = data.Slice(sizeof(uint) + index * rowSize, rowSize);
            var port = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(localPortOffset, sizeof(ushort)));
            var owner = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(ownerOffset, sizeof(uint)));
            output[index] = new ListenerOwner(port, owner);
        }
        return output;
    }
}
