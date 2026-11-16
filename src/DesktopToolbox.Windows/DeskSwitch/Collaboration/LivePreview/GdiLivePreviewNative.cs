using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopToolbox.Windows.DeskSwitch.Collaboration.LivePreview;

internal static class GdiLivePreviewNative
{
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint GetDC(nint window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint SelectObject(nint dc, nint bitmap);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetBrushOrgEx(nint dc, int x, int y, out Point previous);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool StretchBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GdiFlush();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorInfo(ref CursorInfo cursor);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetIconInfo(nint cursor, out IconInfo info);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] internal static extern int GetBitmapInfo(nint bitmap, int size, out BitmapObject info);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DrawIconEx(nint dc, int x, int y, nint cursor, int width, int height, uint animationStep, nint brush, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);

    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct CursorInfo { public uint Size, Flags; public nint Cursor; public Point Position; }
    [StructLayout(LayoutKind.Sequential)] internal struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint HotspotX, HotspotY; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapObject { public int Type, Width, Height, Stride; public ushort Planes, BitsPerPixel; public nint Bits; }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage;
        public int XResolution, YResolution; public uint ColorsUsed, ImportantColors;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo { public BitmapInfoHeader Header; public uint FirstColor; }

    internal static bool IsInteractiveDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == 0) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformationW(desktop, 2, name, name.Capacity * 2, out _) &&
                string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    internal sealed class PhysicalPixelScope : IDisposable
    {
        private readonly nint previous = SetThreadDpiAwarenessContext(-4);
        private bool disposed;
        public PhysicalPixelScope() { if (previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public void Dispose() { if (!disposed) { disposed = true; SetThreadDpiAwarenessContext(previous); } }
    }
}
