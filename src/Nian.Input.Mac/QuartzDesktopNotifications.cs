using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Nian.Input.Mac;

/// <summary>Public NSWorkspace lifecycle notifications; no global input observer.</summary>
internal sealed class QuartzDesktopNotifications : IDisposable
{
    private static readonly ConcurrentDictionary<nint, QuartzDesktopNotifications> Receivers = new();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NotificationCallback(nint receiver, nint selector, nint notification);
    private static readonly NotificationCallback Callback = Receive;
    private static readonly object ClassGate = new();
    private static nint observerClass;
    private readonly Action unsafeTransition;
    private readonly nint center, observer;
    private int awake = 1;
    private int sessionActive = 1;
    private bool disposed;

    internal bool IsAwake => Volatile.Read(ref awake) != 0;
    internal bool SessionActive => Volatile.Read(ref sessionActive) != 0;

    // Constructed on AppKit's application thread by the target's public constructor.
    internal QuartzDesktopNotifications(Action unsafeTransition)
    {
        this.unsafeTransition = unsafeTransition;
        var appKit = NativeLibrary.Load(QuartzDesktopNative.AppKit);
        var pool = QuartzDesktopNative.objc_autoreleasePoolPush();
        try
        {
            var workspace = QuartzDesktopNative.Send(QuartzDesktopNative.objc_getClass("NSWorkspace"), "sharedWorkspace");
            center = QuartzDesktopNative.Send(workspace, "notificationCenter");
            if (center == 0) throw new InvalidOperationException("NSWorkspace notification center unavailable.");
            observer = QuartzDesktopNative.Send(QuartzDesktopNative.Send(GetObserverClass(), "alloc"), "init");
            if (observer == 0) throw new InvalidOperationException("Unable to create workspace observer.");
            Receivers[observer] = this;
            var selector = QuartzDesktopNative.sel_registerName("observeDesktopSafety:");
            foreach (var name in new[] { "NSWorkspaceWillSleepNotification", "NSWorkspaceDidWakeNotification",
                "NSWorkspaceScreensDidSleepNotification", "NSWorkspaceScreensDidWakeNotification",
                "NSWorkspaceSessionDidResignActiveNotification", "NSWorkspaceSessionDidBecomeActiveNotification",
                "NSWorkspaceWillPowerOffNotification" })
                QuartzDesktopNative.AddObserver(center, QuartzDesktopNative.sel_registerName("addObserver:selector:name:object:"),
                    observer, selector, QuartzDesktopNative.Constant(appKit, name), 0);
        }
        catch
        {
            if (observer != 0)
            {
                Receivers.TryRemove(observer, out _);
                if (center != 0) QuartzDesktopNative.RemoveObserver(center, QuartzDesktopNative.sel_registerName("removeObserver:"), observer);
                QuartzDesktopNative.Send(observer, "release");
            }
            throw;
        }
        finally { QuartzDesktopNative.objc_autoreleasePoolPop(pool); NativeLibrary.Free(appKit); }
    }

    private static nint GetObserverClass()
    {
        lock (ClassGate)
        {
            if (observerClass != 0) return observerClass;
            var candidate = QuartzDesktopNative.objc_allocateClassPair(QuartzDesktopNative.objc_getClass("NSObject"),
                "NianDesktopSafetyObserver", 0);
            if (candidate == 0) throw new InvalidOperationException("Unable to register desktop safety observer.");
            if (!QuartzDesktopNative.class_addMethod(candidate, QuartzDesktopNative.sel_registerName("observeDesktopSafety:"),
                Marshal.GetFunctionPointerForDelegate(Callback), "v@:@"))
                throw new InvalidOperationException("Unable to register desktop safety callback.");
            QuartzDesktopNative.objc_registerClassPair(candidate);
            observerClass = candidate;
            return candidate;
        }
    }

    private static void Receive(nint receiver, nint selector, nint notification)
    {
        if (!Receivers.TryGetValue(receiver, out var target) || target.disposed) return;
        try
        {
            var name = QuartzDesktopNative.ReadString(QuartzDesktopNative.Send(notification, "name"));
            switch (name)
            {
                case "NSWorkspaceWillSleepNotification":
                case "NSWorkspaceScreensDidSleepNotification":
                case "NSWorkspaceWillPowerOffNotification":
                    Volatile.Write(ref target.awake, 0);
                    target.unsafeTransition();
                    break;
                case "NSWorkspaceSessionDidResignActiveNotification":
                    Volatile.Write(ref target.sessionActive, 0);
                    target.unsafeTransition();
                    break;
                case "NSWorkspaceDidWakeNotification":
                case "NSWorkspaceScreensDidWakeNotification":
                    // The target remains disarmed; wake never grants consent.
                    Volatile.Write(ref target.awake, 1);
                    target.unsafeTransition();
                    break;
                case "NSWorkspaceSessionDidBecomeActiveNotification":
                    Volatile.Write(ref target.sessionActive, 1);
                    target.unsafeTransition();
                    break;
            }
        }
        catch
        {
            Volatile.Write(ref target.awake, 0);
            Volatile.Write(ref target.sessionActive, 0);
            try { target.unsafeTransition(); } catch { /* Never cross a native callback boundary. */ }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Receivers.TryRemove(observer, out _);
        QuartzDesktopNative.RemoveObserver(center, QuartzDesktopNative.sel_registerName("removeObserver:"), observer);
        QuartzDesktopNative.Send(observer, "release");
    }
}
