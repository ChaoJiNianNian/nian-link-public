using System.Runtime.InteropServices;
using System.Text;

namespace Nian.Input.Windows;

internal static class WindowsDesktopCaptureNativeMethods
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint HookProc(int code, nuint message, nint data);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect : IEquatable<Rect>
    {
        public int Left, Top, Right, Bottom;
        public readonly bool Equals(Rect other) => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHook { public Point Position; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardHook { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Position; public uint Private; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawDevice { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawHeader { public uint Type, Size; public nint Device; public nuint WParam; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public uint Buttons;
        [FieldOffset(12)] public int X;
        [FieldOffset(16)] public int Y;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        public uint Size, Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? Menu;
        public string Name;
        public nint SmallIcon;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ProcessIdToSessionId(uint process, out uint session);
    [DllImport("kernel32")] internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32", SetLastError = true)] internal static extern int GetMessage(out Message message, nint window, uint first, uint last);
    [DllImport("user32")] internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32")] internal static extern void PostQuitMessage(int code);
    [DllImport("user32", SetLastError = true)] internal static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32", SetLastError = true)] internal static extern nint SetWindowsHookEx(int type, HookProc callback, nint module, uint thread);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32")] internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterRawInputDevices([In] RawDevice[] devices, uint count, uint size);
    [DllImport("user32", SetLastError = true)] internal static extern uint GetRegisteredRawInputDevices([Out] RawDevice[]? devices, ref uint count, uint size);
    [DllImport("user32", SetLastError = true)] internal static extern uint GetRawInputData(nint input, uint command, nint buffer, ref uint size, uint headerSize);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClipCursor(ref Rect rect);
    [DllImport("user32", EntryPoint = "ClipCursor", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReleaseClipCursor(nint noRectangle);
    [DllImport("user32")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32", SetLastError = true)] internal static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetUserObjectInformation(nint item, int index, StringBuilder name, uint length, out uint needed);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("wtsapi32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSUnRegisterSessionNotification(nint window);
}
