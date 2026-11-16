using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopToolbox.Windows.DeskSwitch;

// DDC/CI calls are kept in a separate helper process so a display driver cannot hang the workbench.
public sealed class NativeMonitorControl
{
    public DisplayMonitorInfo[] Enumerate()
    {
        var results = new List<DisplayMonitorInfo>();
        WithMonitors((id, name, handle) =>
        {
            if (!handle.HasValue)
            {
                results.Add(new(id, name, null, [], false, "当前连接没有提供可控制的物理显示器。"));
                return true;
            }
            var readable = GetVCPFeatureAndVCPFeatureReply(handle.Value, 0x60, out _, out var current, out _);
            var error = readable ? 0 : Marshal.GetLastWin32Error();
            var inputs = ReadInputs(handle.Value);
            results.Add(new(id, name, readable ? current : null, inputs, readable,
                readable ? "已读到当前输入；输入名称以显示器报告为准，切换需单独测试。" :
                "无法读取输入源，请检查显示器 DDC/CI、视频线与转接器。错误 " + error));
            return true;
        });
        return results.ToArray();
    }

    public void SetInput(string monitorId, uint input)
    {
        if (string.IsNullOrWhiteSpace(monitorId) || input is 0 or > 255)
            throw new ArgumentException("请选择显示器和有效输入源。");
        var found = false;
        WithMonitors((id, _, handle) =>
        {
            if (!string.Equals(id, monitorId, StringComparison.Ordinal)) return true;
            found = true;
            if (!handle.HasValue) throw new InvalidOperationException("此连接不支持物理显示器控制。");
            if (!SetVCPFeature(handle.Value, 0x60, input))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "显示器未接受切换输入源命令。");
            return false;
        }, includeNames: false);
        if (!found) throw new InvalidOperationException("保存的显示器当前不在此连接上，请重新检测并选择。");
    }

    private static MonitorInput[] ReadInputs(IntPtr handle)
    {
        if (!GetCapabilitiesStringLength(handle, out var size) || size is 0 or > 65536) return [];
        var text = new StringBuilder((int)size);
        return CapabilitiesRequestAndCapabilitiesReply(handle, text, size) ? MonitorInputCapabilities.Parse(text.ToString()) : [];
    }

    // null means no physical monitor was obtained; a successful API result may contain handle 0.
    private static void WithMonitors(Func<string, string, IntPtr?, bool> visit, bool includeNames = true)
    {
        Exception? failure = null;
        var stopped = false;
        var count = 0;
        MonitorEnum callback = (IntPtr monitor, IntPtr _, ref Rect rectangle, IntPtr data) =>
        {
            try
            {
                if (++count > 32) throw new InvalidOperationException("显示器数量超出本工具支持范围。");
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
                if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var identified = TryGetActiveDisplayDevice(info.Device, out var device);
                var identity = identified && !string.IsNullOrWhiteSpace(device.DeviceId) ? device.DeviceId : info.Device;
                var description = identified && !string.IsNullOrWhiteSpace(device.DeviceString) ? device.DeviceString : info.Device;
                var edid = includeNames && identified ? MonitorDeviceNameReader.ReadEdid(identity) : null;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var length) || length is 0 or > 16)
                {
                    var keepGoing = visit(identity + "|0", MonitorDisplayName.Resolve(edid, description), null);
                    stopped = !keepGoing;
                    return keepGoing;
                }
                var physical = new PhysicalMonitor[length];
                if (!GetPhysicalMonitorsFromHMONITOR(monitor, length, physical))
                {
                    var keepGoing = visit(identity + "|0", MonitorDisplayName.Resolve(edid, description), null);
                    stopped = !keepGoing;
                    return keepGoing;
                }
                try
                {
                    for (var index = 0; index < physical.Length; index++)
                    {
                        var fallback = string.IsNullOrWhiteSpace(physical[index].Description) ? description : physical[index].Description;
                        var name = physical.Length == 1 ? MonitorDisplayName.Resolve(edid, fallback) : fallback;
                        if (!visit(identity + "|" + index, name, physical[index].Handle))
                        { stopped = true; return false; }
                    }
                }
                finally { DestroyPhysicalMonitors(length, physical); }
                return true;
            }
            catch (Exception error) { failure = error; return false; }
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) && failure is null && !stopped)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法检测当前显示器。");
        if (failure is not null) throw failure;
        GC.KeepAlive(callback);
    }

    // A display source can retain inactive monitor children (for example a laptop panel).
    // Use a device identity only when exactly one active child can be matched unambiguously.
    private static bool TryGetActiveDisplayDevice(string source, out DisplayDevice active)
    {
        active = default;
        var found = false;
        for (uint index = 0; index < 32; index++)
        {
            var candidate = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(source, index, ref candidate, 1)) break;
            if ((candidate.StateFlags & 1) == 0) continue; // DISPLAY_DEVICE_ACTIVE
            if (found) { active = default; return false; }
            active = candidate;
            found = true;
        }
        return found;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    private delegate bool MonitorEnum(IntPtr monitor, IntPtr device, ref Rect rectangle, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr device, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint number, ref DisplayDevice display, uint flags);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint size, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint size, [In] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out uint type, out uint current, out uint maximum);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCapabilitiesStringLength(IntPtr monitor, out uint length);
    [DllImport("dxva2.dll", CharSet = CharSet.Ansi, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr monitor, StringBuilder capabilities, uint length);
}
