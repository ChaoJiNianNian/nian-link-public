using System.Runtime.InteropServices;

namespace Nian.Input.Mac;

/// <summary>
/// Routes only original, private mouse-moved events already delivered to this
/// application. It neither observes other processes nor posts input events.
/// </summary>
internal static class MacPrivateMouseRouter
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly object Gate = new();
    private static readonly BlockInvoke Callback = Route;
    private static nint monitor, block, descriptor, runtime;

    internal static void EnsureInstalled()
    {
        lock (Gate)
        {
            if (monitor != 0) return;
            if (!OperatingSystem.IsMacOS() || IntPtr.Size != 8)
                throw new PlatformNotSupportedException("The local mouse router requires 64-bit macOS.");
            try
            {
                // Fixed process-lifetime, capture-free global Block. Layout and
                // ownership follow https://clang.llvm.org/docs/Block-ABI-Apple.html.
                runtime = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
                descriptor = Marshal.AllocHGlobal(16);
                Marshal.WriteInt64(descriptor, 0, 0);
                Marshal.WriteInt64(descriptor, 8, 32);
                block = Marshal.AllocHGlobal(32);
                Marshal.WriteIntPtr(block, 0, NativeLibrary.GetExport(runtime, "_NSConcreteGlobalBlock"));
                Marshal.WriteInt32(block, 8, 1 << 28); // BLOCK_IS_GLOBAL; no captured objects/signature.
                Marshal.WriteInt32(block, 12, 0);
                Marshal.WriteIntPtr(block, 16, Marshal.GetFunctionPointerForDelegate(Callback));
                Marshal.WriteIntPtr(block, 24, descriptor);
                monitor = AddMonitor(objc_getClass("NSEvent"),
                    sel_registerName("addLocalMonitorForEventsMatchingMask:handler:"), 1UL << 5, block);
                if (monitor == 0) throw new InvalidOperationException("Could not install the application's private mouse router.");
                // The monitor, block, descriptor, delegate and runtime remain
                // valid until process exit, including while no view is hosted.
                Message(monitor, sel_registerName("retain"));
            }
            catch
            {
                // Once AppKit holds the block, its storage must remain valid
                // even if retaining the returned token encounters an error.
                if (monitor == 0)
                {
                    if (block != 0) { Marshal.FreeHGlobal(block); block = 0; }
                    if (descriptor != 0) { Marshal.FreeHGlobal(descriptor); descriptor = 0; }
                    if (runtime != 0) { NativeLibrary.Free(runtime); runtime = 0; }
                }
                throw;
            }
        }
    }

    private static nint Route(nint ownBlock, nint input)
    {
        try { return MacMouseSurface.RoutePrivateMove(input); }
        catch (Exception error) { MacMouseSurface.ReportCallbackError(error); return input; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint BlockInvoke(nint ownBlock, nint input);
    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint AddMonitor(nint target, nint selector, ulong mask, nint block);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Message(nint target, nint selector);
}
