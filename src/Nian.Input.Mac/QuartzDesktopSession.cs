using System.Runtime.InteropServices;

namespace Nian.Input.Mac;

/// <summary>
/// Combines documented session/console identity, display power, Secure Event Input
/// and foreground identity with an additional legacy lock signal. macOS has no
/// documented general isScreenLocked API. CGSSessionScreenIsLocked is NOT treated
/// as such a guarantee: true rejects, absence alone never establishes safety.
/// Real lock/unlock and authorization-panel acceptance remain mandatory OS tests.
/// </summary>
internal sealed class QuartzDesktopSession : IDisposable
{
    private readonly QuartzDesktopNotifications notifications;
    private readonly MacDesktopOwnUiPolicy ownUiPolicy;
    private bool disposed;
    private static readonly string[] SecurityProcesses =
    [
        "com.apple.loginwindow", "com.apple.SecurityAgent", "com.apple.securityagent",
        "com.apple.LocalAuthentication.UIAgent", "com.apple.LocalAuthentication.RemoteUIHost",
        "com.apple.AuthKitUIService", "com.apple.authd", "com.apple.CoreServicesUIAgent",
        "com.apple.UserNotificationCenter", "com.apple.ScreenSaver.Engine", "com.apple.ScreenSaver",
        "com.apple.systempreferences", "com.apple.SystemSettings", "com.apple.SystemUIServer"
    ];

    internal QuartzDesktopSession(Action unsafeTransition, MacDesktopOwnUiPolicy? ownUiPolicy = null)
    { this.ownUiPolicy = ownUiPolicy ?? MacDesktopOwnUiPolicy.Experimental; notifications = new(unsafeTransition); }

    internal MacDesktopEnvironment Read(bool preview = false)
    {
        if (disposed || !OperatingSystem.IsMacOS()) return MacDesktopEnvironment.Unknown;
        var pool = QuartzDesktopNative.objc_autoreleasePoolPush();
        nint dictionary = 0, consoleUser = 0;
        try
        {
            var accessibility = QuartzDesktopNative.AXIsProcessTrusted() ? MacDesktopGate.Allowed : MacDesktopGate.Blocked;
            dictionary = QuartzDesktopNative.CGSessionCopyCurrentDictionary();
            if (dictionary == 0) return MacDesktopEnvironment.Unknown with { Accessibility = accessibility };
            // CGSession.h defines these as CFSTR macros. Older documented SDKs
            // use the kCGSSession spelling in several values; accept either known
            // spelling, but never infer missing values as an active user session.
            var onConsole = Boolean(dictionary, "kCGSSessionOnConsoleKey", "kCGSessionOnConsoleKey");
            var loginDone = Boolean(dictionary, "kCGSessionLoginDoneKey");
            var sessionUid = Number(dictionary, "kCGSSessionUserIDKey", "kCGSessionUserIDKey");
            consoleUser = QuartzDesktopNative.SCDynamicStoreCopyConsoleUser(0, out var consoleUid, out _);
            var consoleName = QuartzDesktopNative.ReadString(consoleUser);
            var expectedUid = QuartzDesktopNative.geteuid();
            var interactive = onConsole == true && loginDone == true && expectedUid != 0 &&
                sessionUid is >= 0 && (uint)sessionUid == expectedUid && consoleUid == expectedUid &&
                consoleName is { Length: > 0 } && consoleName is not "loginwindow" and not "_mbsetupuser" &&
                notifications.SessionActive ? MacDesktopGate.Allowed : MacDesktopGate.Blocked;

            var secureInput = QuartzDesktopNative.IsSecureEventInputEnabled();
            var foreground = ReadForeground(out var receiverUi, preview);
            var awake = ReadAwake();
            var legacyLock = Boolean(dictionary, "CGSSessionScreenIsLocked");
            var invalidLegacyLock = HasValue(dictionary, "CGSSessionScreenIsLocked") && legacyLock is null;
            var unlocked = legacyLock == true || invalidLegacyLock ? MacDesktopGate.Blocked :
                interactive == MacDesktopGate.Allowed && awake == MacDesktopGate.Allowed &&
                !secureInput && foreground == MacDesktopGate.Allowed ? MacDesktopGate.Allowed : MacDesktopGate.Unknown;
            return new(accessibility, interactive, unlocked, awake,
                secureInput ? MacDesktopGate.Blocked : MacDesktopGate.Allowed, foreground, receiverUi);
        }
        catch { return MacDesktopEnvironment.Unknown; }
        finally
        {
            if (dictionary != 0) QuartzDesktopNative.CFRelease(dictionary);
            if (consoleUser != 0) QuartzDesktopNative.CFRelease(consoleUser);
            QuartzDesktopNative.objc_autoreleasePoolPop(pool);
        }
    }

    private MacDesktopGate ReadAwake()
    {
        if (!notifications.IsAwake) return MacDesktopGate.Blocked;
        var ids = new uint[64];
        if (QuartzDesktopNative.CGGetActiveDisplayList((uint)ids.Length, ids, out var count) != 0 || count == 0 || count > ids.Length)
            return MacDesktopGate.Unknown;
        return ids.Take((int)count).Any(id => QuartzDesktopNative.CGDisplayIsAsleep(id) == 0)
            ? MacDesktopGate.Allowed : MacDesktopGate.Blocked;
    }

    private MacDesktopGate ReadForeground(out bool receiverUi, bool preview)
    {
        receiverUi = false;
        var workspace = QuartzDesktopNative.Send(QuartzDesktopNative.objc_getClass("NSWorkspace"), "sharedWorkspace");
        var app = QuartzDesktopNative.Send(workspace, "frontmostApplication");
        if (app == 0) return MacDesktopGate.Unknown;
        var pid = QuartzDesktopNative.SendInt(app, "processIdentifier");
        if (pid <= 0) return MacDesktopGate.Unknown;
        var bundle = QuartzDesktopNative.ReadString(QuartzDesktopNative.Send(app, "bundleIdentifier"));
        if (string.IsNullOrWhiteSpace(bundle)) return MacDesktopGate.Unknown;
        receiverUi = pid == Environment.ProcessId && ownUiPolicy.ProtectsOwnForeground;
        if (preview && ownUiPolicy.ExcludesPreviewOwner(bundle, pid, Environment.ProcessId)) receiverUi = true;
        if (IsSecurityApplication(bundle)) return MacDesktopGate.Blocked;
        // Password/system consent agents can own a visible sheet while the
        // originating app stays frontmost. Examine owner identity only, no titles,
        // window images, text contents or accessibility element values.
        var windows = QuartzDesktopNative.CGWindowListCopyWindowInfo(1U | 16U, 0);
        if (windows == 0) return MacDesktopGate.Unknown;
        try
        {
            var count = QuartzDesktopNative.CFArrayGetCount(windows);
            if (count < 0 || count > 8192) return MacDesktopGate.Unknown;
            for (nint i = 0; i < count; i++)
            {
                var window = QuartzDesktopNative.CFArrayGetValueAtIndex(windows, i);
                var ownerPid = Number(window, "kCGWindowOwnerPID");
                if (ownerPid is null or <= 0) return MacDesktopGate.Unknown;
                var owner = QuartzDesktopNative.SendWithInt(QuartzDesktopNative.objc_getClass("NSRunningApplication"),
                    QuartzDesktopNative.sel_registerName("runningApplicationWithProcessIdentifier:"), ownerPid.Value);
                var ownerBundle = owner == 0 ? null : QuartzDesktopNative.ReadString(QuartzDesktopNative.Send(owner, "bundleIdentifier"));
                // SystemUIServer's permanent menu bar is expected; foreground
                // SystemUIServer is still denied above. Other security agents block.
                if (ownerBundle is not null && ownerBundle != "com.apple.SystemUIServer" && IsSecurityApplication(ownerBundle))
                    return MacDesktopGate.Blocked;
            }
            return MacDesktopGate.Allowed;
        }
        finally { QuartzDesktopNative.CFRelease(windows); }
    }

    internal MacDesktopGate ReadPointerGate(MacDesktopPoint point, bool preview = false)
    {
        if (disposed || !point.IsValid) return MacDesktopGate.Unknown;
        var windows = QuartzDesktopNative.CGWindowListCopyWindowInfo(1U | 16U, 0);
        if (windows == 0) return MacDesktopGate.Unknown;
        try
        {
            var count = QuartzDesktopNative.CFArrayGetCount(windows);
            if (count < 0 || count > 8192) return MacDesktopGate.Unknown;
            for (nint i = 0; i < count; i++)
            {
                var window = QuartzDesktopNative.CFArrayGetValueAtIndex(windows, i);
                var boundsValue = ReadValue(window, "kCGWindowBounds");
                if (boundsValue == 0 || !QuartzDesktopNative.CGRectMakeWithDictionaryRepresentation(boundsValue, out var bounds))
                    return MacDesktopGate.Unknown;
                var ownerPid = Number(window, "kCGWindowOwnerPID");
                var ownerBundle = preview && ownerPid is > 0 ? ReadOwnerBundle(ownerPid.Value) : null;
                var info = new MacDesktopWindowInfo(ownerPid,
                    new(bounds.Origin.X, bounds.Origin.Y, bounds.Size.Width, bounds.Size.Height),
                    RealNumber(window, "kCGWindowAlpha"), Number(window, "kCGWindowNumber"), ownerBundle);
                if (MacDesktopWindowGate.ResolveWindow(point, Environment.ProcessId, info, ownUiPolicy, preview) is { } gate) return gate;
            }
            return MacDesktopGate.Allowed;
        }
        catch { return MacDesktopGate.Unknown; }
        finally { QuartzDesktopNative.CFRelease(windows); }
    }

    internal static bool IsSecurityApplication(string bundle) => SecurityProcesses.Contains(bundle, StringComparer.OrdinalIgnoreCase) ||
        bundle.StartsWith("com.apple.LocalAuthentication.", StringComparison.OrdinalIgnoreCase) ||
        bundle.StartsWith("com.apple.Authorization", StringComparison.OrdinalIgnoreCase);

    private static string? ReadOwnerBundle(int pid)
    {
        var owner = QuartzDesktopNative.SendWithInt(QuartzDesktopNative.objc_getClass("NSRunningApplication"),
            QuartzDesktopNative.sel_registerName("runningApplicationWithProcessIdentifier:"), pid);
        return owner == 0 ? null : QuartzDesktopNative.ReadString(QuartzDesktopNative.Send(owner, "bundleIdentifier"));
    }

    private static nint ReadValue(nint dictionary, string key)
    {
        var native = QuartzDesktopNative.CFStringCreateWithCString(0, key, 0x08000100);
        if (native == 0) return 0;
        try { return QuartzDesktopNative.CFDictionaryGetValue(dictionary, native); }
        finally { QuartzDesktopNative.CFRelease(native); }
    }

    private static bool HasValue(nint dictionary, string key) => ReadValue(dictionary, key) != 0;
    private static bool? Boolean(nint dictionary, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = ReadValue(dictionary, key);
            if (value == 0) continue;
            return QuartzDesktopNative.CFGetTypeID(value) == QuartzDesktopNative.CFBooleanGetTypeID()
                ? QuartzDesktopNative.CFBooleanGetValue(value) : null;
        }
        return null;
    }

    private static int? Number(nint dictionary, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = ReadValue(dictionary, key);
            if (value == 0) continue;
            if (QuartzDesktopNative.CFGetTypeID(value) != QuartzDesktopNative.CFNumberGetTypeID()) return null;
            return QuartzDesktopNative.CFNumberGetValue(value, 3, out var number) ? number : null;
        }
        return null;
    }

    private static double? RealNumber(nint dictionary, string key)
    {
        var value = ReadValue(dictionary, key);
        if (value == 0 || QuartzDesktopNative.CFGetTypeID(value) != QuartzDesktopNative.CFNumberGetTypeID()) return null;
        return QuartzDesktopNative.CFNumberGetDoubleValue(value, 6, out var number) ? number : null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        notifications.Dispose();
    }
}
