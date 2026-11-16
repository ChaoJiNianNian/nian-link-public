namespace Nian.Input.Mac;

public sealed record MacDesktopLocalEventGuardCheck(bool Success, bool TaggedEventRejected,
    bool LocalEventAllowed, int PostedEvents);

/// <summary>
/// Filters only input being dispatched to this application. Our own desktop
/// events cannot activate its pairing/permission controls after a focus race.
/// This is not a global event tap and does not collect keys or text.
/// </summary>
public sealed partial class MacDesktopLocalEventGuard : IDisposable
{
    public const long EventTag = 0x4e49414e4445534b; // NIANDESK; physical mode may use an ordinary mini window.
    public const long PreviewEventTag = 0x4e49414e50525657; // NIANPRVW; never dispatched into any window of this app.
    internal const ulong InputMask = (1UL << 1) | (1UL << 2) | (1UL << 3) | (1UL << 4) | (1UL << 5) |
        (1UL << 6) | (1UL << 7) | (1UL << 10) | (1UL << 11) | (1UL << 12) |
        (1UL << 22) | (1UL << 25) | (1UL << 26) | (1UL << 27);
    private static MacDesktopLocalEventGuard? active;
    private QuartzDesktopLocalEventMonitor? monitor;
    private string? failure;
    private long rejectedCount;
    private int disposed;
    private readonly MacDesktopOwnUiPolicy ownUiPolicy;

    private MacDesktopLocalEventGuard(MacDesktopOwnUiPolicy? ownUiPolicy) => this.ownUiPolicy = ownUiPolicy ?? MacDesktopOwnUiPolicy.Experimental;

    public static bool IsInstalled => Volatile.Read(ref active)?.monitor?.IsInstalled == true;
    public bool IsHealthy => Volatile.Read(ref disposed) == 0 && monitor?.IsInstalled == true && Volatile.Read(ref failure) is null;
    public string? Failure => Volatile.Read(ref failure);
    public long RejectedCount => Interlocked.Read(ref rejectedCount);

    /// <summary>Call once on AppKit's GUI thread, before enabling any local Arm UI.</summary>
    public static MacDesktopLocalEventGuard Install(MacDesktopOwnUiPolicy? ownUiPolicy = null)
    {
        QuartzDesktopLocalEventMonitor.RequireGuiThread();
        if (Volatile.Read(ref active) is not null) throw new InvalidOperationException("The local input guard is already installed.");
        var guard = new MacDesktopLocalEventGuard(ownUiPolicy);
        guard.monitor = QuartzDesktopLocalEventMonitor.Install(guard.FilterEvent);
        Volatile.Write(ref active, guard);
        return guard;
    }

    // A missing/unreadable tag never reaches security controls. Unmarked physical
    // input and other readable tags pass; this does not identify all remote tools.
    internal static bool ShouldReject(long? userData) => userData is null || userData == EventTag || userData == PreviewEventTag;
    internal static bool ShouldReject(long? userData, int? windowId, MacDesktopOwnUiPolicy ownUiPolicy)
    {
        if (userData is null || userData == PreviewEventTag) return true;
        return userData == EventTag && ownUiPolicy.ProtectsOwnWindow(windowId);
    }
    internal static bool IsInputEventType(ulong type) => type < 64 && (InputMask & (1UL << (int)type)) != 0;

    /// <summary>Pair/Grant handlers must call this at the final local action boundary too: AppKit local monitors do not cover nested tracking loops.</summary>
    public static bool IsCurrentEventEligibleForLocalApproval()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            QuartzDesktopLocalEventMonitor.RequireGuiThread();
            var app = QuartzDesktopNative.Send(QuartzDesktopNative.objc_getClass("NSApplication"), "sharedApplication");
            var current = QuartzDesktopNative.Send(app, "currentEvent");
            return !ShouldReject(QuartzDesktopLocalEventMonitor.ReadUserData(current));
        }
        catch { return false; }
    }

    private nint FilterEvent(nint input)
    {
        try
        {
            if (input != 0 && !IsInputEventType(QuartzDesktopLocalEventMonitor.ReadEventType(input))) return input;
            var tag = QuartzDesktopLocalEventMonitor.ReadUserData(input);
            if (tag is null) SetFailure("NativeEventUnavailable");
            var windowId = tag == EventTag ? QuartzDesktopLocalEventMonitor.ReadWindowNumber(input) : null;
            if (!ShouldReject(tag, windowId, ownUiPolicy)) return input;
            Interlocked.Increment(ref rejectedCount);
            return 0;
        }
        catch (Exception error)
        {
            SetFailure(error.GetType().Name);
            Interlocked.Increment(ref rejectedCount);
            return 0; // Exceptions never cross the native Block callback.
        }
    }

    private void SetFailure(string errorType) => Interlocked.CompareExchange(ref failure,
        $"本机输入保护失效（{errorType}）。", null);

    /// <summary>
    /// Checks native monitor registration and the same filter with newly created
    /// marked/unmarked native mouse and keyboard objects. It posts NO events to
    /// the system OR the app queue; real dispatch is a separate acceptance test.
    /// </summary>
    public MacDesktopLocalEventGuardCheck SelfCheck()
    {
        QuartzDesktopLocalEventMonitor.RequireGuiThread();
        if (!IsHealthy) return new(false, false, false, 0);
        try
        {
            var result = monitor!.SelfCheck();
            if (!result.Success) SetFailure("NativeFilterSelfCheckFailed");
            return result with { Success = result.Success && IsHealthy };
        }
        catch (Exception error)
        {
            SetFailure(error.GetType().Name);
            return new(false, false, false, 0);
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        QuartzDesktopLocalEventMonitor.RequireGuiThread();
        // If removal fails, retain the monitor and its Block rather than freeing
        // memory that AppKit may still dispatch through; the guard stays unhealthy.
        try { monitor?.Dispose(); }
        catch (Exception error) { SetFailure(error.GetType().Name); throw; }
        Interlocked.Exchange(ref disposed, 1);
        Interlocked.CompareExchange(ref active, null, this);
    }
}
