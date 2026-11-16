using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Nian.Input.Mac;

public sealed record MacSurfaceEvent(uint Type, double X, double Y, bool LeftPressed,
    double WheelDelta, int WindowNumber, long SourceState);

/// <summary>
/// A transparent NSView that observes only events actually dispatched to this
/// owned view. Create, host, and dispose it on the AppKit UI thread.
/// </summary>
public sealed class MacMouseSurface : IDisposable
{
    private static readonly ConcurrentDictionary<nint, Action<MacSurfaceEvent>> Receivers = new();
    private static readonly Native.EventMethod OnEvent = ReceiveEvent;
    private static readonly Native.BooleanMethod Flipped = (_, _) => true;
    private static readonly Native.BooleanMethod Transparent = (_, _) => false;
    private static readonly Native.BooleanMethod CannotTakeFocus = (_, _) => false;
    private static readonly Native.FirstMouseMethod FirstMouse = (_, _, _) => true;
    private static readonly Lazy<nint> ViewClass = new(CreateViewClass);
    private static string? lastCallbackError;
    private nint handle;

    public nint Handle => handle;
    public static string? LastCallbackError => Volatile.Read(ref lastCallbackError);

    public MacMouseSurface(Action<MacSurfaceEvent> receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("NSView requires macOS.");
        var allocated = Native.Send(ViewClass.Value, "alloc");
        handle = Native.Send(allocated, "init");
        if (handle == 0) throw new InvalidOperationException("Could not create the native mouse test surface.");
        Receivers[handle] = receiver;
        try
        {
            // InVisibleRect follows the host's layout without rebuilding the
            // tracking area. All tracking remains inside this own NSView.
            var tracking = Native.CreateTrackingArea(handle, 0x002 | 0x020 | 0x200 | 0x400);
            if (tracking == 0) throw new InvalidOperationException("Could not create native mouse tracking.");
            try { Native.SendObject(handle, "addTrackingArea:", tracking); }
            finally { Native.Send(tracking, "release"); }
            MacPrivateMouseRouter.EnsureInstalled();
        }
        catch { Dispose(); throw; }
    }

    private static nint CreateViewClass()
    {
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        var type = Native.objc_allocateClassPair(Native.objc_getClass("NSView"), "NianInputMouseSurface", 0);
        if (type == 0) throw new InvalidOperationException("Could not register the native mouse test view.");
        try
        {
            Add(type, "isFlipped", Flipped, "B@:");
            Add(type, "isOpaque", Transparent, "B@:");
            Add(type, "acceptsFirstResponder", CannotTakeFocus, "B@:");
            Add(type, "acceptsFirstMouse:", FirstMouse, "B@:@");
            foreach (var method in new[] { "mouseDown:", "mouseUp:", "mouseMoved:", "mouseDragged:",
                "rightMouseDown:", "rightMouseUp:", "rightMouseDragged:", "otherMouseDown:",
                "otherMouseUp:", "otherMouseDragged:", "scrollWheel:" })
                Add(type, method, OnEvent, "v@:@");
            Native.objc_registerClassPair(type);
            return type;
        }
        catch { Native.objc_disposeClassPair(type); throw; }
    }

    private static void Add(nint type, string method, Delegate callback, string encoding)
    {
        if (!Native.class_addMethod(type, Native.sel_registerName(method),
            Marshal.GetFunctionPointerForDelegate(callback), encoding))
            throw new InvalidOperationException($"Could not register native method {method}.");
    }

    private static void ReceiveEvent(nint view, nint selector, nint input)
    {
        // Never allow a managed callback failure to unwind across AppKit.
        try
        {
            if (!Receivers.TryGetValue(view, out var receiver)) return;
            var type = (uint)Native.Send(input, "type");
            var point = Native.ConvertPoint(view, Native.ReadPoint(input, "locationInWindow"));
            var window = (int)Native.Send(input, "windowNumber");
            var wheel = type == 22 ? Native.SendDouble(input, "scrollingDeltaY") : 0;
            var cgEvent = Native.Send(input, "CGEvent");
            var source = cgEvent == 0 ? 0 : Native.CGEventCreateSourceFromEvent(cgEvent);
            long sourceState = 0;
            var held = false;
            if (source != 0)
            {
                try
                {
                    sourceState = Native.CGEventSourceGetSourceStateID(source);
                    held = Native.CGEventSourceButtonState((int)sourceState, 0);
                }
                finally { Native.CFRelease(source); }
            }
            receiver(new(type, point.X, point.Y, held, wheel, window, sourceState));
        }
        catch (Exception error) { ReportCallbackError(error); }
    }

    // AppKit's normal mouse-moved routing follows the hardware pointer. A
    // PID-targeted private move already received by this application instead
    // goes to this experiment's own visible NSView, using the original event.
    internal static nint RoutePrivateMove(nint input)
    {
        if (input == 0 || Receivers.IsEmpty || (uint)Native.Send(input, "type") != 5) return input;
        var cgEvent = Native.Send(input, "CGEvent");
        var source = cgEvent == 0 ? 0 : Native.CGEventCreateSourceFromEvent(cgEvent);
        if (source == 0) return input;
        int sourceState;
        try { sourceState = Native.CGEventSourceGetSourceStateID(source); }
        finally { Native.CFRelease(source); }
        if (sourceState is 0 or 1) return input;

        var eventWindow = (int)Native.Send(input, "windowNumber");
        if (eventWindow <= 0) return input;
        var pointInWindow = Native.ReadPoint(input, "locationInWindow");
        foreach (var view in Receivers.Keys)
        {
            var window = Native.Send(view, "window");
            if (window == 0 || (int)Native.Send(window, "windowNumber") != eventWindow ||
                Native.Send(view, "isHiddenOrHasHiddenAncestor") != 0) continue;
            var point = Native.ConvertPoint(view, pointInWindow);
            var visible = Native.ReadRect(view, "visibleRect");
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                !double.IsFinite(visible.Origin.X) || !double.IsFinite(visible.Origin.Y) ||
                !double.IsFinite(visible.Size.X) || !double.IsFinite(visible.Size.Y) ||
                !(visible.Size.X > 0 && visible.Size.Y > 0) ||
                point.X < visible.Origin.X || point.Y < visible.Origin.Y ||
                point.X >= visible.Origin.X + visible.Size.X ||
                point.Y >= visible.Origin.Y + visible.Size.Y) continue;
            Native.SendObject(view, "mouseMoved:", input);
            return 0;
        }
        return input;
    }

    internal static void ReportCallbackError(Exception error) =>
        // Exception messages may contain caller data; evidence keeps only the
        // failure category and code, never any input text.
        Volatile.Write(ref lastCallbackError, $"{error.GetType().Name} (0x{error.HResult:X8})");

    public void Dispose()
    {
        var view = Interlocked.Exchange(ref handle, 0);
        if (view == 0) return;
        Receivers.TryRemove(view, out _);
        Native.Send(view, "release");
    }

    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string Quartz = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct Point { public readonly double X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct Rect { public readonly Point Origin, Size; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void EventMethod(nint view, nint selector, nint input);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal delegate bool BooleanMethod(nint view, nint selector);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal delegate bool FirstMouseMethod(nint view, nint selector, nint input);

        [DllImport(ObjC)] internal static extern nint objc_getClass(string name);
        [DllImport(ObjC)] internal static extern nint sel_registerName(string name);
        [DllImport(ObjC)] internal static extern nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);
        [DllImport(ObjC)] internal static extern void objc_registerClassPair(nint type);
        [DllImport(ObjC)] internal static extern void objc_disposeClassPair(nint type);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool class_addMethod(nint type, nint selector, nint implementation, string encoding);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Message(nint target, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MessageObject(nint target, nint selector, nint value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Point MessagePoint(nint target, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Rect MessageRect(nint target, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void MessageRectX64(out Rect result, nint target, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Point MessageConvertPoint(nint target, nint selector, Point value, nint fromView);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double MessageDouble(nint target, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint MessageTracking(nint target, nint selector, Rect rect, nuint options, nint owner, nint userInfo);
        [DllImport(Quartz)] internal static extern nint CGEventCreateSourceFromEvent(nint input);
        [DllImport(Quartz)] internal static extern int CGEventSourceGetSourceStateID(nint source);
        [DllImport(Quartz)] [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CGEventSourceButtonState(int stateId, uint button);
        [DllImport(Foundation)] internal static extern void CFRelease(nint source);

        internal static nint Send(nint target, string method) => Message(target, sel_registerName(method));
        internal static void SendObject(nint target, string method, nint value) => MessageObject(target, sel_registerName(method), value);
        internal static Point ReadPoint(nint target, string method) => MessagePoint(target, sel_registerName(method));
        internal static Rect ReadRect(nint target, string method)
        {
            var selector = sel_registerName(method);
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return MessageRect(target, selector);
            MessageRectX64(out var result, target, selector);
            return result;
        }
        internal static double SendDouble(nint target, string method) => MessageDouble(target, sel_registerName(method));
        internal static Point ConvertPoint(nint target, Point value) => MessageConvertPoint(target,
            sel_registerName("convertPoint:fromView:"), value, 0);
        internal static nint CreateTrackingArea(nint owner, nuint options) => MessageTracking(
            Send(objc_getClass("NSTrackingArea"), "alloc"),
            sel_registerName("initWithRect:options:owner:userInfo:"), default, options, owner, 0);
    }
}
