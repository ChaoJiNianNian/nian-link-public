using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms=System.Windows.Forms;

namespace DesktopToolbox.App.DeskLink;

internal static class MiniNativeWindow
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd,int command);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd,int attribute,ref int value,int size);

    public static void Configure(Window window)
    {
        window.WindowStyle=WindowStyle.None; window.ResizeMode=ResizeMode.NoResize;
        window.AllowsTransparency=true; window.Background=System.Windows.Media.Brushes.Transparent;
        window.ShowInTaskbar=false; window.ShowActivated=false; window.Topmost=false;
        window.UseLayoutRounding=true; window.SnapsToDevicePixels=true;
    }
    public static void Initialize(nint hwnd)
    {
        var style=GetWindowLongPtr(hwnd,-20).ToInt64();
        SetWindowLongPtr(hwnd,-20,new nint((style|0x80L)&~0x40000L));
        var border=unchecked((int)0xfffffffe); DwmSetWindowAttribute(hwnd,34,ref border,sizeof(int));
        if(HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor=System.Windows.Media.Colors.Transparent;
        if(!SetWindowPos(hwnd,0,0,0,0,0,0x0037)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static double Dpi(nint hwnd) { var dpi=GetDpiForWindow(hwnd); return dpi is >=48 and <=768 ? dpi : 96; }
    public static MiniRect Bounds(nint hwnd)
    {
        if(!GetWindowRect(hwnd,out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(bounds.Left,bounds.Top,bounds.Right-bounds.Left,bounds.Bottom-bounds.Top);
    }
    public static Forms.Screen Screen(nint hwnd,string display) => Forms.Screen.AllScreens.FirstOrDefault(screen=>string.Equals(screen.DeviceName,display,StringComparison.OrdinalIgnoreCase)) ?? Forms.Screen.FromHandle(hwnd);
    public static MiniRect WorkArea(Forms.Screen screen) => new(screen.WorkingArea.X,screen.WorkingArea.Y,screen.WorkingArea.Width,screen.WorkingArea.Height);
    public static void Place(nint hwnd,double width,double height,MiniWindowPreferences preferences)
    {
        var screen=Screen(hwnd,preferences.Display);
        var bounds=MiniWindowPolicy.Place(WorkArea(screen),Dpi(hwnd),width,height,preferences.DockSide,preferences.X,preferences.Y);
        if(!SetWindowPos(hwnd,0,bounds.Left,bounds.Top,bounds.Width,bounds.Height,0x0014)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static MiniWindowPreferences Remember(nint hwnd,MiniWindowPreferences preferences)
    {
        var bounds=Bounds(hwnd); var screen=Forms.Screen.FromRectangle(new(bounds.Left,bounds.Top,bounds.Width,bounds.Height));
        var position=MiniWindowPolicy.Remember(WorkArea(screen),Dpi(hwnd),bounds);
        return preferences with { Display=screen.DeviceName,DockSide=position.Side,X=position.X,Y=position.Y };
    }
    public static void Restore(nint hwnd)
    {
        if(IsIconic(hwnd)) ShowWindow(hwnd,4);
    }
}
