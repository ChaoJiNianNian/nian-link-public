using System.Runtime.InteropServices;

namespace Nian.Input.Mac;

/// <summary>AppKit local event monitor and the documented Apple Block ABI.</summary>
internal sealed class QuartzDesktopLocalEventMonitor : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string System = "/usr/lib/libSystem.B.dylib";
    // Every type emitted by QuartzDesktopPlatform: buttons, movement, drags,
    // keyboard, modifier changes and vertical wheel. No app-defined notifications.
    private const ulong Mask = MacDesktopLocalEventGuard.InputMask;
    private static readonly BlockInvoke Callback = Invoke;
    private static Func<nint, nint>? handler;
    // Fixed process-lifetime type metadata, not a per-install allocation. Native
    // heap Block copies may outlive removeMonitor's call stack; their descriptor,
    // signature, native runtime and reverse-P/Invoke delegate must remain valid.
    private static nint descriptor, signature, runtime, appKit;
    private nint monitor, heapBlock;

    private QuartzDesktopLocalEventMonitor() { }
    internal bool IsInstalled => Volatile.Read(ref monitor) != 0;

    internal static void RequireGuiThread()
    {
        if (!OperatingSystem.IsMacOS() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("The local desktop input guard requires 64-bit macOS.");
        if (pthread_main_np() != 1) throw new InvalidOperationException("Install, self-check and remove the guard on AppKit's GUI thread.");
    }

    internal static QuartzDesktopLocalEventMonitor Install(Func<nint, nint> filter)
    {
        RequireGuiThread();
        if (handler is not null) throw new InvalidOperationException("A desktop local monitor is already registered.");
        EnsureMetadata();
        var owned = new QuartzDesktopLocalEventMonitor();
        var literal = Marshal.AllocHGlobal(32);
        try
        {
            // Stack-layout capture-free literal is copied by the Blocks runtime
            // into a reference-counted heap Block. AppKit may independently copy
            // it; _Block_release below frees only our own retained reference.
            Marshal.WriteIntPtr(literal, 0, NativeLibrary.GetExport(runtime, "_NSConcreteStackBlock"));
            Marshal.WriteInt32(literal, 8, 1 << 30); // BLOCK_HAS_SIGNATURE; no captures/copy-dispose helpers.
            Marshal.WriteInt32(literal, 12, 0);
            Marshal.WriteIntPtr(literal, 16, Marshal.GetFunctionPointerForDelegate(Callback));
            Marshal.WriteIntPtr(literal, 24, descriptor);
            owned.heapBlock = BlockCopy(literal);
            if (owned.heapBlock == 0) throw new InvalidOperationException("Could not retain the local event filter Block.");
            Volatile.Write(ref handler, filter);
            owned.monitor = AddMonitor(QuartzDesktopNative.objc_getClass("NSEvent"),
                QuartzDesktopNative.sel_registerName("addLocalMonitorForEventsMatchingMask:handler:"), Mask, owned.heapBlock);
            if (owned.monitor == 0) throw new InvalidOperationException("Could not install the local event filter.");
            // Apple owns the returned monitor token. Store it, remove it explicitly;
            // do not release this unowned object. The owned heap Block is separate.
            return owned;
        }
        catch
        {
            if (owned.monitor == 0)
            {
                Volatile.Write(ref handler, null);
                if (owned.heapBlock != 0) { BlockRelease(owned.heapBlock); owned.heapBlock = 0; }
            }
            throw;
        }
        finally { Marshal.FreeHGlobal(literal); }
    }

    private static void EnsureMetadata()
    {
        if (descriptor != 0) return;
        nint newRuntime = 0, newAppKit = 0, newSignature = 0, newDescriptor = 0;
        try
        {
            newRuntime = NativeLibrary.Load(System);
            newAppKit = NativeLibrary.Load(QuartzDesktopNative.AppKit);
            newSignature = Marshal.StringToHGlobalAnsi("@16@?0@8");
            newDescriptor = Marshal.AllocHGlobal(24);
            Marshal.WriteInt64(newDescriptor, 0, 0);
            Marshal.WriteInt64(newDescriptor, 8, 32);
            Marshal.WriteIntPtr(newDescriptor, 16, newSignature);
            runtime = newRuntime; appKit = newAppKit; signature = newSignature; descriptor = newDescriptor;
        }
        catch
        {
            if (newDescriptor != 0) Marshal.FreeHGlobal(newDescriptor);
            if (newSignature != 0) Marshal.FreeHGlobal(newSignature);
            if (newAppKit != 0) NativeLibrary.Free(newAppKit);
            if (newRuntime != 0) NativeLibrary.Free(newRuntime);
            throw;
        }
    }

    private static nint Invoke(nint block, nint input)
    {
        try
        {
            if (Volatile.Read(ref handler) is { } filter) return filter(input);
            // A retained native copy already queued during removal still rejects
            // our tagged events, even when its managed owner has been disposed.
            if (input != 0 && !MacDesktopLocalEventGuard.IsInputEventType(ReadEventType(input))) return input;
            return MacDesktopLocalEventGuard.ShouldReject(ReadUserData(input)) ? 0 : input;
        }
        catch { return 0; } // Never unwind managed exceptions into AppKit.
    }

    internal static long? ReadUserData(nint input)
    {
        if (input == 0) return null;
        var cg = QuartzDesktopNative.Send(input, "CGEvent"); // Borrowed; no text/key characters are accessed.
        return cg == 0 ? null : CGEventGetIntegerValueField(cg, 42); // kCGEventSourceUserData.
    }

    internal static ulong ReadEventType(nint input) => (ulong)EventType(input, QuartzDesktopNative.sel_registerName("type"));
    internal static int? ReadWindowNumber(nint input)
    {
        if (input == 0) return null;
        var number = QuartzDesktopNative.SendInt(input, "windowNumber");
        return number > 0 ? number : null;
    }

    internal MacDesktopLocalEventGuardCheck SelfCheck()
    {
        RequireGuiThread();
        if (!IsInstalled || heapBlock == 0) return new(false, false, false, 0);
        var pool = QuartzDesktopNative.objc_autoreleasePoolPush();
        nint source = 0;
        try
        {
            source = QuartzDesktopNative.CGEventSourceCreate(-1);
            if (source == 0) throw new InvalidOperationException("Could not create native self-check objects.");
            var tagged = true;
            var local = true;
            foreach (var keyboard in new[] { false, true })
            {
                tagged &= CheckObject(source, keyboard, MacDesktopLocalEventGuard.EventTag, expectRejected: true);
                local &= CheckObject(source, keyboard, 0, expectRejected: false);
            }
            return new(tagged && local && IsInstalled, tagged, local, 0);
        }
        finally
        {
            if (source != 0) QuartzDesktopNative.CFRelease(source);
            QuartzDesktopNative.objc_autoreleasePoolPop(pool);
        }
    }

    private bool CheckObject(nint source, bool keyboard, long tag, bool expectRejected)
    {
        var cg = keyboard ? QuartzDesktopNative.CGEventCreateKeyboardEvent(source, 0, true) :
            QuartzDesktopNative.CGEventCreateMouseEvent(source, 5, new(0, 0), 0);
        if (cg == 0) throw new InvalidOperationException("Native self-check object creation failed.");
        try
        {
            QuartzDesktopNative.CGEventSetIntegerValueField(cg, 42, tag);
            var input = EventFromCg(QuartzDesktopNative.objc_getClass("NSEvent"),
                QuartzDesktopNative.sel_registerName("eventWithCGEvent:"), cg);
            if (input == 0) throw new InvalidOperationException("Native self-check wrapping failed.");
            var invoke = Marshal.GetDelegateForFunctionPointer<BlockInvoke>(Marshal.ReadIntPtr(heapBlock, 16));
            var result = invoke(heapBlock, input);
            return expectRejected ? result == 0 : result == input;
        }
        finally { QuartzDesktopNative.CFRelease(cg); }
    }

    public void Dispose()
    {
        RequireGuiThread();
        if (monitor == 0) return;
        RemoveMonitor(QuartzDesktopNative.objc_getClass("NSEvent"), QuartzDesktopNative.sel_registerName("removeMonitor:"), monitor);
        monitor = 0;
        Volatile.Write(ref handler, null);
        if (heapBlock != 0) { BlockRelease(heapBlock); heapBlock = 0; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint BlockInvoke(nint block, nint input);
    [DllImport(System)] private static extern int pthread_main_np();
    [DllImport(System, EntryPoint = "_Block_copy")] private static extern nint BlockCopy(nint block);
    [DllImport(System, EntryPoint = "_Block_release")] private static extern void BlockRelease(nint block);
    [DllImport(QuartzDesktopNative.Quartz)] private static extern long CGEventGetIntegerValueField(nint input, uint field);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint AddMonitor(nint target, nint selector, ulong mask, nint block);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void RemoveMonitor(nint target, nint selector, nint monitor);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint EventFromCg(nint target, nint selector, nint input);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nuint EventType(nint target, nint selector);
}
