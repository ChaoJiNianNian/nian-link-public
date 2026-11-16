using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DesktopToolbox.Windows.Organizing;
using Forms = System.Windows.Forms;

namespace DesktopToolbox.App.Drawer;

internal static class DrawerDesktopEnvironment
{
    private const uint NoSize = 0x0001, NoMove = 0x0002, NoZOrder = 0x0004, NoActivate = 0x0010, Show = 0x0040;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetClassNameW(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("Shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, bool redraw);

    public static DrawerForeground Foreground()
    {
        var handle = GetForegroundWindow();
        if (handle == 0 || GetWindowThreadProcessId(handle, out var pid) == 0) return default;
        if (pid == Environment.ProcessId) return new(true, handle, (int)pid, null, false);
        var root = GetAncestor(handle, 2 /* GA_ROOT */); if (root == 0) root = handle;
        var desktop = GetShellWindow();
        var shell = desktop != 0 && (handle == desktop || root == desktop)
            || DrawerWindowPolicy.IsDesktopClass(WindowClass(handle)) || DrawerWindowPolicy.IsDesktopClass(WindowClass(root));
        // Explorer's desktop and its file windows share a process; only actual shell surfaces are exempt.
        if (shell) return new(true, handle, (int)pid, null, false);
        string? executable = null;
        try { using var process = Process.GetProcessById((int)pid); executable = process.ProcessName + ".exe"; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
        var bounds = Forms.Screen.FromHandle(root).Bounds;
        var full = GetWindowRect(root, out var rect) && rect.Left <= bounds.Left + 2 && rect.Top <= bounds.Top + 2
            && rect.Right >= bounds.Right - 2 && rect.Bottom >= bounds.Bottom - 2;
        return new(true, handle, (int)pid, executable, full);
    }
    private static string WindowClass(nint window)
    {
        var name = new StringBuilder(128); GetClassNameW(window, name, name.Capacity); return name.ToString();
    }
    public static void Place(nint handle, bool expanded, DrawerBehavior behavior)
    {
        var screen = Forms.Screen.AllScreens.FirstOrDefault(candidate => string.Equals(candidate.DeviceName, behavior.DockDisplay, StringComparison.OrdinalIgnoreCase))
            ?? Forms.Screen.FromHandle(handle);
        var work = screen.WorkingArea;
        var dpi = ScreenDpi(handle, screen);
        var area = DrawerWindowPolicy.Place(new(work.X, work.Y, work.Width, work.Height), dpi, expanded, behavior.DockSide, behavior.DockOffset);
        if (!SetWindowPos(handle, 0, area.Left, area.Top, area.Width, area.Height, NoZOrder | NoActivate)) throw new Win32Exception(Marshal.GetLastWin32Error());
        ClearHardClip(handle);
    }
    public static void Place(nint handle, bool expanded) => Place(handle, expanded, DrawerBehavior.Default());
    public static (string Side, double Offset, string Display) Snap(nint handle)
    {
        var area = GetBounds(handle);
        var screen = Forms.Screen.FromRectangle(new(area.Left, area.Top, area.Width, area.Height));
        var work = screen.WorkingArea;
        var dock = DrawerWindowPolicy.Snap(new(work.X, work.Y, work.Width, work.Height), ScreenDpi(handle, screen), area);
        return (dock.Side, dock.Offset, screen.DeviceName);
    }
    public static DrawerArea GetBounds(nint handle)
    {
        if (!GetWindowRect(handle, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    public static (int X, int Y) Cursor()
    {
        if (!GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (point.X, point.Y);
    }
    public static bool IsLeftButtonDown => (GetAsyncKeyState(1) & 0x8000) != 0;
    public static void Move(nint handle, int left, int top)
    {
        if (!SetWindowPos(handle, 0, left, top, 0, 0, NoSize | NoZOrder | NoActivate)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static bool IsMinimized(nint handle) => IsIconic(handle);
    public static void RestoreVisible(nint handle)
    {
        if (IsIconic(handle)) ShowWindow(handle, 4 /* SW_SHOWNOACTIVATE */);
        if (!SetWindowPos(handle, new nint(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, NoMove | NoSize | NoActivate | Show)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static double ScreenDpi(nint handle, Forms.Screen screen)
    {
        var bounds = screen.Bounds;
        var monitor = MonitorFromPoint(new Point { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 }, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor != 0 && GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var x, out _) == 0 && x is >= 48 and <= 768) return x;
        var fallback = GetDpiForWindow(handle); return fallback is >= 48 and <= 768 ? fallback : 96;
    }
    private static void ClearHardClip(nint handle)
    {
        // Layered-window alpha already defines both the outline and transparent hit-test areas.
        // A binary GDI region cuts away the CSS curve's partially transparent antialiasing pixels.
        if (SetWindowRgn(handle, 0, true) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "不能清除抽屉的硬边缘裁切。");
    }
    public static void RestoreForeground(long handle)
    {
        if (handle != 0 && IsWindow((nint)handle)) SetForegroundWindow((nint)handle);
    }
}
