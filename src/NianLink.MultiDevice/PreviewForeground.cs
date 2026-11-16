using System.Runtime.InteropServices;
using System.Text;

namespace NianLink.MultiDevice;

internal static class PreviewForeground
{
    public static bool IsOwnedCaptureWindow(int? processId, Func<int, bool> ownsProcess)
    {
        if (processId is not > 0 || !ownsProcess(processId.Value)) return false;
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero || GetWindowThreadProcessId(handle, out var actual) == 0 || actual != (uint)processId.Value) return false;
        var name = new StringBuilder(64);
        return GetClassName(handle, name, name.Capacity) > 0 && name.ToString() == "DeskflowDesk" && ownsProcess(processId.Value);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
}
