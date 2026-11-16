using System.Runtime.InteropServices;
namespace DesktopToolbox.DeskLink;

// MIB_IF_ROW2 offsets and size for the verified Windows x64 ABI. All unspecified
// bytes remain reserved and are passed to the API in a full-size buffer.
[StructLayout(LayoutKind.Explicit, Size = 1352)]
internal struct MibIfRow2
{
    [FieldOffset(8)] internal uint InterfaceIndex;
    [FieldOffset(12)] internal Guid InterfaceGuid;
    [FieldOffset(1152)] internal byte Flags;
}

internal readonly record struct NativeHardwareInterfaceSnapshot(uint Status, uint Index, Guid Id, byte Flags);

/// <summary>Read-only Windows OS report, not proof against a driver or hypervisor impersonating hardware.</summary>
internal static class WindowsHardwareInterface
{
    private const byte HardwareFlag = 1;

    internal static bool Matches(int expectedIndex, string? expectedId, NativeHardwareInterfaceSnapshot result)
        => expectedIndex > 0 && Guid.TryParse(expectedId, out var id) && id != Guid.Empty &&
           result.Status == 0 && result.Index == (uint)expectedIndex && result.Id == id && (result.Flags & HardwareFlag) != 0;

    internal static bool IsHardware(DiscoveryNetworkBinding binding) => IsHardwareCore(binding
#if ISOLATED_SOCKET_CONFIGURATION_TEST
        , null
#endif
        );

#if ISOLATED_SOCKET_CONFIGURATION_TEST
    internal static bool IsHardware(DiscoveryNetworkBinding binding, Func<int, NativeHardwareInterfaceSnapshot> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return IsHardwareCore(binding, query);
    }
#endif

    private static bool IsHardwareCore(DiscoveryNetworkBinding binding
#if ISOLATED_SOCKET_CONFIGURATION_TEST
        , Func<int, NativeHardwareInterfaceSnapshot>? query
#endif
        )
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !LayoutMatches() || !binding.HasLiveNetworkOrigin || !binding.IsStillAvailable()) return false;
        try
        {
#if ISOLATED_SOCKET_CONFIGURATION_TEST
            var result = query is null ? Query(binding.InterfaceIndex) : query(binding.InterfaceIndex);
#else
            var result = Query(binding.InterfaceIndex);
#endif
            return Matches(binding.InterfaceIndex, binding.InterfaceId, result) && binding.IsStillAvailable();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or MarshalDirectiveException or OverflowException)
        { return false; }
    }

    private static NativeHardwareInterfaceSnapshot Query(int index)
    {
        var row = new MibIfRow2 { InterfaceIndex = checked((uint)index) };
        uint status = GetIfEntry2(ref row);
        return new(status, row.InterfaceIndex, row.InterfaceGuid, row.Flags);
    }

    private static bool LayoutMatches() => Marshal.SizeOf<MibIfRow2>() == 1352 &&
        Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.InterfaceIndex)).ToInt32() == 8 &&
        Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.InterfaceGuid)).ToInt32() == 12 &&
        Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.Flags)).ToInt32() == 1152;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2(ref MibIfRow2 row);
}
