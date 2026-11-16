using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace DesktopToolbox.App.Hosting;
public static class NativeMaterial
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, int message, nint wParam, nint lParam);
    public static void Drag(Window window)
    {
        if ((GetAsyncKeyState(1) & 0x8000) == 0) return;
        ReleaseCapture(); SendMessageW(new WindowInteropHelper(window).Handle, 0xA1, 2, 0);
    }
}
