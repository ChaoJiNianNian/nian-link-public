using System.Runtime.InteropServices;

namespace Nian.Input.Mac;

/// <summary>Public system interfaces, isolated from the own-PID Quartz factory.</summary>
internal static class QuartzDesktopNative
{
    internal const string Quartz = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    internal const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string SystemConfiguration = "/System/Library/Frameworks/SystemConfiguration.framework/SystemConfiguration";
    private const string HiToolbox = "/System/Library/Frameworks/Carbon.framework/Frameworks/HIToolbox.framework/HIToolbox";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Point(double x, double y) { public readonly double X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Size { public readonly double Width, Height; }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Rect { public readonly Point Origin; public readonly Size Size; }

    [DllImport(AppServices)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool AXIsProcessTrusted();
    [DllImport(Quartz)] internal static extern nint CGSessionCopyCurrentDictionary();
    [DllImport(Quartz)] internal static extern uint CGGetActiveDisplayList(uint maximum, [Out] uint[] displays, out uint count);
    [DllImport(Quartz)] internal static extern Rect CGDisplayBounds(uint display);
    [DllImport(Quartz)] internal static extern uint CGMainDisplayID();
    [DllImport(Quartz)] internal static extern double CGDisplayRotation(uint display);
    [DllImport(Quartz)] internal static extern uint CGDisplayIsAsleep(uint display); // boolean_t is a 32-bit integer.
    [DllImport(Quartz)] internal static extern nint CGEventCreate(nint source);
    [DllImport(Quartz)] internal static extern Point CGEventGetLocation(nint input);
    [DllImport(Quartz)] internal static extern nint CGEventSourceCreate(int stateId);
    [DllImport(Quartz)] internal static extern void CGEventSourceSetUserData(nint source, long value);
    [DllImport(Quartz)] internal static extern void CGEventSourceSetLocalEventsSuppressionInterval(nint source, double seconds);
    [DllImport(Quartz)] internal static extern nint CGEventCreateKeyboardEvent(nint source, ushort code, [MarshalAs(UnmanagedType.I1)] bool down);
    [DllImport(Quartz)] internal static extern nint CGEventCreateMouseEvent(nint source, uint type, Point point, uint button);
    [DllImport(Quartz)] internal static extern nint CGEventCreateScrollWheelEvent2(nint source, uint units, uint count, int wheel1, int wheel2, int wheel3);
    [DllImport(Quartz)] internal static extern void CGEventSetLocation(nint input, Point location);
    [DllImport(Quartz)] internal static extern void CGEventSetFlags(nint input, ulong flags);
    [DllImport(Quartz)] internal static extern void CGEventSetType(nint input, uint type);
    [DllImport(Quartz)] internal static extern void CGEventSetIntegerValueField(nint input, uint field, long value);
    [DllImport(Quartz)] internal static extern void CGEventSetDoubleValueField(nint input, uint field, double value);
    [DllImport(Quartz)] internal static extern void CGEventPost(uint tap, nint input);
    [DllImport(Quartz)] internal static extern uint CGWarpMouseCursorPosition(Point point);
    [DllImport(Quartz)] internal static extern nint CGWindowListCopyWindowInfo(uint options, uint relativeToWindow);
    [DllImport(Quartz)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CGRectMakeWithDictionaryRepresentation(nint dictionary, out Rect rect);

    [DllImport(CoreFoundation)] internal static extern void CFRelease(nint value);
    [DllImport(CoreFoundation)] internal static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFBooleanGetValue(nint value);
    [DllImport(CoreFoundation)] internal static extern nuint CFGetTypeID(nint value);
    [DllImport(CoreFoundation)] internal static extern nuint CFBooleanGetTypeID();
    [DllImport(CoreFoundation)] internal static extern nuint CFNumberGetTypeID();
    [DllImport(CoreFoundation)] internal static extern nint CFArrayGetCount(nint array);
    [DllImport(CoreFoundation)] internal static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFNumberGetValue(nint value, int type, out int number);
    [DllImport(CoreFoundation, EntryPoint = "CFNumberGetValue")] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CFNumberGetDoubleValue(nint value, int type, out double number);
    [DllImport(CoreFoundation)] internal static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(SystemConfiguration)] internal static extern nint SCDynamicStoreCopyConsoleUser(nint store, out uint uid, out uint gid);
    [DllImport(HiToolbox)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool IsSecureEventInputEnabled();
    [DllImport("/usr/lib/libSystem.B.dylib")] internal static extern uint geteuid();

    [DllImport(ObjC)] internal static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] internal static extern void objc_autoreleasePoolPop(nint value);
    [DllImport(ObjC)] internal static extern nint objc_getClass(string name);
    [DllImport(ObjC)] internal static extern nint sel_registerName(string name);
    [DllImport(ObjC)] internal static extern nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);
    [DllImport(ObjC)] internal static extern void objc_registerClassPair(nint type);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool class_addMethod(nint type, nint selector, nint implementation, string encoding);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendPtr(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern int SendInteger(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendNumber(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint SendWithInt(nint target, nint selector, int argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void AddObserver(nint center, nint selector, nint observer, nint callback, nint name, nint filter);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void RemoveObserver(nint center, nint selector, nint observer);

    internal static nint Send(nint target, string selector) => SendPtr(target, sel_registerName(selector));
    internal static int SendInt(nint target, string selector) => SendInteger(target, sel_registerName(selector));
    internal static double SendDouble(nint target, string selector) => SendNumber(target, sel_registerName(selector));
    internal static string? ReadString(nint value)
    {
        if (value == 0) return null;
        return Marshal.PtrToStringUTF8(Send(value, "UTF8String"));
    }

    internal static nint Constant(nint library, string symbol) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, symbol));
}
