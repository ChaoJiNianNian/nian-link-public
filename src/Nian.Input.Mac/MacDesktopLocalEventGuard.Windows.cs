using System.Runtime.InteropServices;

namespace Nian.Input.Mac;

public sealed partial class MacDesktopLocalEventGuard
{
    /// <summary>GUI-thread only. The caller must own a live NSWindow; this never accepts a remote/native pointer.</summary>
    public static int? GetWindowNumber(nint window) => GetWindowNumber(window, "NSWindow");

    /// <summary>Resolves only Avalonia's known NSWindow/NSView handles, with Objective-C class checks before reading its window.</summary>
    public static int? GetWindowNumber(nint handle, string? descriptor)
    {
        if (!OperatingSystem.IsMacOS() || handle == 0 || descriptor is not ("NSWindow" or "NSView")) return null;
        try
        {
            QuartzDesktopLocalEventMonitor.RequireGuiThread();
            var expectedClass = QuartzDesktopNative.objc_getClass(descriptor);
            if (expectedClass == 0 || !NativeIsKindOfClass(handle, QuartzDesktopNative.sel_registerName("isKindOfClass:"), expectedClass)) return null;
            var window = descriptor == "NSView" ? QuartzDesktopNative.Send(handle, "window") : handle;
            var windowClass = QuartzDesktopNative.objc_getClass("NSWindow");
            if (window == 0 || windowClass == 0 || !NativeIsKindOfClass(window, QuartzDesktopNative.sel_registerName("isKindOfClass:"), windowClass)) return null;
            var number = NativeWindowNumber(window, QuartzDesktopNative.sel_registerName("windowNumber"));
            return number > 0 && number <= int.MaxValue ? (int)number : null;
        }
        catch { return null; }
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool NativeIsKindOfClass(nint value, nint selector, nint expectedClass);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint NativeWindowNumber(nint window, nint selector);
}
