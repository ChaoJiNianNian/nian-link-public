using System.Runtime.InteropServices;

namespace DesktopToolbox.Windows.DeskSwitch;

// Reads metadata for exactly the supplied monitor interface. Names are optional;
// failure here must not prevent the independent DDC/CI capability check.
public static class MonitorDeviceNameReader
{
    private const int MaximumInterfacePathLength = 32767;
    private const uint MaximumInterfaceDetailLength = 65536;
    private const uint MinimumEdidLength = 128;
    private const uint MaximumEdidLength = 32768;
    private const int ErrorInsufficientBuffer = 122;
    private const uint RegistryBinary = 3;
    private const uint DeviceRegistryGlobal = 1; // DICS_FLAG_GLOBAL
    private const uint DeviceRegistryHardware = 1; // DIREG_DEV
    private const uint RegistryQueryValue = 1; // KEY_QUERY_VALUE
    private static readonly IntPtr InvalidHandle = new(-1);
    private static readonly Guid MonitorInterfaceClass = new("e6f07b5f-ee97-4a90-b076-33f57bf4eaa7");

    public static byte[]? ReadEdid(string deviceInterfacePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(deviceInterfacePath)
            || deviceInterfacePath.Length > MaximumInterfacePathLength || deviceInterfacePath.Contains('\0'))
            return null;

        var devices = IntPtr.Zero;
        var registry = IntPtr.Zero;
        var openedInterface = false;
        var monitorInterface = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
        try
        {
            devices = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (devices == IntPtr.Zero || devices == InvalidHandle) return null;
            if (!SetupDiOpenDeviceInterface(devices, deviceInterfacePath, 0, ref monitorInterface)) return null;
            openedInterface = true;
            if (monitorInterface.InterfaceClass != MonitorInterfaceClass) return null;

            var device = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
            // The documented size-query form also fills DeviceInfoData. It avoids
            // parsing or copying the symbolic path returned by SetupAPI.
            var detailsAvailable = SetupDiGetDeviceInterfaceDetail(devices, ref monitorInterface,
                IntPtr.Zero, 0, out var detailLength, ref device);
            var detailError = Marshal.GetLastWin32Error();
            if (detailsAvailable || detailError != ErrorInsufficientBuffer
                || detailLength < 6 || detailLength > MaximumInterfaceDetailLength)
                return null;

            // DIREG_DEV opens the device parameters hardware key itself, so EDID
            // is read directly from it; no registry path is guessed or constructed.
            registry = SetupDiOpenDevRegKey(devices, ref device, DeviceRegistryGlobal, 0,
                DeviceRegistryHardware, RegistryQueryValue);
            if (registry == IntPtr.Zero || registry == InvalidHandle) return null;

            uint length = 0;
            if (RegQueryValueEx(registry, "EDID", IntPtr.Zero, out var kind, null, ref length) != 0
                || kind != RegistryBinary || length < MinimumEdidLength || length > MaximumEdidLength)
                return null;
            var edid = new byte[(int)length];
            var actualLength = length;
            if (RegQueryValueEx(registry, "EDID", IntPtr.Zero, out kind, edid, ref actualLength) != 0
                || kind != RegistryBinary || actualLength != length)
                return null;
            return edid;
        }
        catch (Exception error) when (error is ArgumentException or ExternalException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        finally
        {
            if (registry != IntPtr.Zero && registry != InvalidHandle) RegCloseKey(registry);
            if (devices != IntPtr.Zero && devices != InvalidHandle)
            {
                if (openedInterface) SetupDiDeleteDeviceInterfaceData(devices, ref monitorInterface);
                SetupDiDestroyDeviceInfoList(devices);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClass;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public uint Size;
        public Guid Class;
        public uint DeviceInstance;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr window);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInterfaceW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInterface(IntPtr devices, string devicePath, uint flags,
        ref DeviceInterfaceData monitorInterface);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices,
        ref DeviceInterfaceData monitorInterface, IntPtr detail, uint detailLength,
        out uint requiredLength, ref DeviceInfoData device);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr devices, ref DeviceInfoData device,
        uint scope, uint hardwareProfile, uint keyType, uint access);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDeleteDeviceInterfaceData(IntPtr devices,
        ref DeviceInterfaceData monitorInterface);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", ExactSpelling = true,
        CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(IntPtr key, string valueName, IntPtr reserved,
        out uint kind, [Out] byte[]? value, ref uint length);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegCloseKey(IntPtr key);
}
