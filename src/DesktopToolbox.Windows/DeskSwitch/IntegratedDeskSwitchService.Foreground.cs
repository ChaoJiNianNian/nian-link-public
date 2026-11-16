using System.Runtime.InteropServices;
using System.Text;

namespace DesktopToolbox.Windows.DeskSwitch;

public sealed partial class IntegratedDeskSwitchService : IDeskflowPreviewForegroundState
{
    public bool PreviewInputOwnsForeground
    {
        get
        {
            if (!OperatingSystem.IsWindows() || Volatile.Read(ref stopping) != 0 || Volatile.Read(ref disposed) != 0)
                return false;
            var owned = session; var handoff = coordinator; var configured = settings;
            if (owned is null || handoff is null || configured is null) return false;
            var core = handoff.DecorateState(CurrentSessionState);
            if (core.ProcessId is not > 0 || !owned.OwnsLiveProcess(core.ProcessId.Value)) return false;
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || GetWindowThreadProcessId(foreground, out var processId) == 0) return false;
            var windowClass = new StringBuilder(64);
            if (GetClassName(foreground, windowClass, windowClass.Capacity) == 0) return false;
            return DeskflowPreviewForegroundPolicy.IsTrusted(core, configured.LocalName, core.ProcessId.Value,
                owned.OwnsLiveProcess(core.ProcessId.Value), processId, windowClass.ToString());
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);
}
