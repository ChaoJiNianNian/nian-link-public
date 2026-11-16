using System.Runtime.InteropServices;

namespace Nian.Input.Mac;

/// <summary>Posts only to this process. This lab has no global CGEventPost entry point.</summary>
internal sealed class QuartzEventPlatform : IMacEventPlatform, IDisposable
{
    private nint source;
    private readonly int ownPid = Environment.ProcessId;
    private readonly int[] buttonNumbers = new int[3];
    private int eventNumber;
    private readonly Queue<MacPointerEvidence> pointerEvidence = new();
    public MacPointerEvidence? LastPointerEvidence { get; private set; }
    internal MacPointerEvidence[] PointerEvidence => pointerEvidence.ToArray();
    public bool AccessibilityAllowed => MacWindowGeometry.AccessibilityAllowed;
    public bool OwnProcessFrontmost => MacWindowGeometry.OwnProcessFrontmost;

    public void Post(MacInputAction action, double x, double y, MacInputWindow window)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Quartz requires macOS.");
        if (source == 0)
        {
            // A private event source avoids sharing synthetic key/button state with
            // the user's existing input tools. No global HID event tap is installed.
            source = QuartzNative.CGEventSourceCreate(-1);
            if (source == 0) throw new InvalidOperationException("Could not create private Quartz event source.");
        }
        var pool = QuartzNative.objc_autoreleasePoolPush();
        nint input = 0;
        try
        {
            switch (action.Kind)
            {
                case InputKind.KeyDown:
                case InputKind.KeyUp:
                    input = QuartzNative.CGEventCreateKeyboardEvent(source, action.KeyCode, action.Kind == InputKind.KeyDown);
                    if (input != 0)
                    {
                        if (action.KeyCode is 54 or 55 or 56 or 58 or 59 or 60 or 61 or 62)
                            QuartzNative.CGEventSetType(input, 12); // kCGEventFlagsChanged
                        QuartzNative.CGEventSetIntegerValueField(input, 8, action.IsRepeat ? 1 : 0);
                    }
                    break;
                case InputKind.MoveRelative:
                case InputKind.ButtonDown:
                case InputKind.ButtonUp:
                case InputKind.Wheel:
                    if (action.Kind == InputKind.ButtonDown) buttonNumbers[action.Button] = ++eventNumber;
                    var number = action.Button >= 0 ? buttonNumbers[action.Button] : ++eventNumber;
                    input = QuartzWindowEvents.Create(action, x, y, window, source, number, out var pointer);
                    LastPointerEvidence = pointer;
                    if (pointerEvidence.Count == 16) pointerEvidence.Dequeue();
                    pointerEvidence.Enqueue(pointer);
                    break;
                default:
                    throw new ArgumentException("Unsupported Quartz event kind.");
            }
            if (input == 0) throw new InvalidOperationException("Quartz could not create input event.");
            QuartzNative.CGEventSetFlags(input, action.Flags);
            QuartzNative.CGEventPostToPid(ownPid, input);
        }
        finally
        {
            if (input != 0) QuartzNative.CFRelease(input);
            QuartzNative.objc_autoreleasePoolPop(pool);
        }
    }

    public void Dispose()
    {
        if (source == 0) return;
        QuartzNative.CFRelease(source);
        source = 0;
    }
}

/// <summary>Only native geometry/identity for the current application is queried.</summary>
public static class MacWindowGeometry
{
    public static bool AccessibilityAllowed
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return false;
            try { return QuartzNative.AXIsProcessTrusted(); }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    internal static bool OwnProcessFrontmost
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return false;
            var pool = QuartzNative.objc_autoreleasePoolPush();
            try
            {
                var workspace = QuartzNative.Send(QuartzNative.objc_getClass("NSWorkspace"), "sharedWorkspace");
                var front = QuartzNative.Send(workspace, "frontmostApplication");
                return front != 0 && QuartzNative.SendInt(front, "processIdentifier") == Environment.ProcessId;
            }
            finally { QuartzNative.objc_autoreleasePoolPop(pool); }
        }
    }

    // Call on the UI thread. Cocoa screen coordinates start at the primary
    // display's bottom-left; Quartz starts at its top-left. NSWindow supplies
    // points directly, so Retina scaling never divides global screen offsets.
    public static bool TryGetClientBounds(out MacInputBounds bounds)
    {
        bounds = new(0, 0, 0, 0);
        if (!OperatingSystem.IsMacOS()) return false;
        var pool = QuartzNative.objc_autoreleasePoolPush();
        try
        {
            var application = QuartzNative.Send(QuartzNative.objc_getClass("NSApplication"), "sharedApplication");
            var window = QuartzNative.Send(application, "keyWindow");
            if (window == 0) return false;
            var frame = QuartzNative.ReadRect(window, "frame");
            var client = QuartzNative.ConvertFrameRect(window, frame);
            var main = QuartzNative.CGDisplayBounds(QuartzNative.CGMainDisplayID());
            var identity = new MacInputWindow(QuartzNative.SendInt(window, "windowNumber"), client.Origin.X,
                main.Size.Height - client.Origin.Y - client.Size.Height, client.Size.Width, client.Size.Height);
            bounds = new(identity.X, identity.Y, identity.Width, identity.Height, identity);
            return bounds.IsValid;
        }
        finally { QuartzNative.objc_autoreleasePoolPop(pool); }
    }
}

internal static class QuartzNative
{
    private const string Quartz = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    static QuartzNative()
    {
        if (OperatingSystem.IsMacOS()) NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct CgPoint(double x, double y) { public readonly double X = x; public readonly double Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct CgSize { public readonly double Width; public readonly double Height; }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct CgRect { public readonly CgPoint Origin; public readonly CgSize Size; }

    [DllImport(AppServices)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool AXIsProcessTrusted();
    [DllImport(Quartz)] internal static extern nint CGEventSourceCreate(int stateId);
    [DllImport(Quartz)] internal static extern nint CGEventCreateKeyboardEvent(nint source, ushort keyCode, [MarshalAs(UnmanagedType.I1)] bool down);
    [DllImport(Quartz)] internal static extern nint CGEventCreateCopy(nint input);
    [DllImport(Quartz)] internal static extern void CGEventSetSource(nint input, nint source);
    [DllImport(Quartz)] internal static extern nint CGEventCreateScrollWheelEvent2(nint source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);
    [DllImport(Quartz)] internal static extern void CGEventSetType(nint input, uint type);
    [DllImport(Quartz)] internal static extern void CGEventSetFlags(nint input, ulong flags);
    [DllImport(Quartz)] internal static extern void CGEventSetIntegerValueField(nint input, uint field, long value);
    [DllImport(Quartz)] internal static extern long CGEventGetIntegerValueField(nint input, uint field);
    [DllImport(Quartz)] internal static extern double CGEventGetDoubleValueField(nint input, uint field);
    [DllImport(Quartz)] internal static extern void CGEventPostToPid(int pid, nint input);
    [DllImport(Quartz)] internal static extern uint CGMainDisplayID();
    [DllImport(Quartz)] internal static extern CgRect CGDisplayBounds(uint display);
    [DllImport(CoreFoundation)] internal static extern void CFRelease(nint value);
    [DllImport(ObjC)] internal static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] internal static extern void objc_autoreleasePoolPop(nint value);
    [DllImport(ObjC)] internal static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendPtr(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern int SendInteger(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendNumber(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CgPoint SendPoint(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendCgEvent(nint target, nint selector, nint input);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendMouseEvent(nint target, nint selector,
        nuint type, CgPoint point, ulong flags, double timestamp, nint window, nint context,
        nint eventNumber, nint clickCount, float pressure);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CgRect SendRect(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CgRect SendRectArg(nint target, nint selector, CgRect value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void SendRectX64(out CgRect result, nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void SendRectArgX64(out CgRect result, nint target, nint selector, CgRect value);
    internal static nint Send(nint target, string selector) => SendPtr(target, sel_registerName(selector));
    internal static int SendInt(nint target, string selector) => SendInteger(target, sel_registerName(selector));
    internal static double SendDouble(nint target, string selector) => SendNumber(target, sel_registerName(selector));
    internal static CgPoint ReadPoint(nint target, string selector) => SendPoint(target, sel_registerName(selector));
    internal static nint EventFromCg(nint input) => SendCgEvent(objc_getClass("NSEvent"), sel_registerName("eventWithCGEvent:"), input);
    internal static nint CreateMouseEvent(uint type, CgPoint point, ulong flags, double timestamp,
        int window, int eventNumber, int clickCount, float pressure) => SendMouseEvent(objc_getClass("NSEvent"),
        sel_registerName("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
        type, point, flags, timestamp, window, 0, eventNumber, clickCount, pressure);
    internal static CgRect ReadRect(nint target, string selector)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return SendRect(target, sel_registerName(selector));
        SendRectX64(out var result, target, sel_registerName(selector));
        return result;
    }
    internal static CgRect ConvertFrameRect(nint window, CgRect frame)
    {
        var selector = sel_registerName("contentRectForFrameRect:");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return SendRectArg(window, selector, frame);
        SendRectArgX64(out var result, window, selector, frame);
        return result;
    }
}
