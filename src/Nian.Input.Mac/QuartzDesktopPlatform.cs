using System.Diagnostics;

namespace Nian.Input.Mac;

/// <summary>Only the separately armed desktop target owns this global posting path.</summary>
internal sealed class QuartzDesktopPlatform : IMacDesktopPlatform
{
    private readonly QuartzDesktopSession session;
    private nint source;
    private bool disposed;
    private readonly int[] buttonNumbers = new int[3];
    private readonly int[] buttonClickCounts = new int[3];
    private readonly MacDesktopClickState clicks = new();
    private int eventNumber;
    public event Action? SafetyChanged;

    internal QuartzDesktopPlatform(MacDesktopOwnUiPolicy? ownUiPolicy = null)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Desktop Quartz requires macOS.");
        session = new(() => SafetyChanged?.Invoke(), ownUiPolicy);
    }

    public MacDesktopEnvironment ReadEnvironment() => disposed ? MacDesktopEnvironment.Unknown : session.Read();
    public MacDesktopEnvironment ReadEnvironment(bool preview) => disposed ? MacDesktopEnvironment.Unknown : session.Read(preview);
    public MacDesktopGate ReadPointerGate(MacDesktopPoint position) => disposed ? MacDesktopGate.Unknown : session.ReadPointerGate(position);
    public MacDesktopGate ReadPointerGate(MacDesktopPoint position, bool preview) => disposed ? MacDesktopGate.Unknown : session.ReadPointerGate(position, preview);

    public IReadOnlyList<MacDesktopDisplay> ReadDisplays()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return MacDesktopSource.ReadNativeDisplays();
    }

    public MacDesktopPoint ReadPointer()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var input = QuartzDesktopNative.CGEventCreate(0);
        if (input == 0) throw new InvalidOperationException("Unable to read Quartz pointer.");
        try
        {
            var p = QuartzDesktopNative.CGEventGetLocation(input);
            return new(p.X, p.Y);
        }
        finally { QuartzDesktopNative.CFRelease(input); }
    }

    public void ResetSequence()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // No native input is emitted here. A fresh local consent owns fresh
        // private source state and cannot continue an old click/drag sequence.
        if (source != 0) { QuartzDesktopNative.CFRelease(source); source = 0; }
        clicks.Reset();
        Array.Clear(buttonNumbers);
        Array.Clear(buttonClickCounts);
        eventNumber = 0;
    }

    public void Post(MacInputAction action, MacDesktopPoint position)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var eventIdentifier = action.Preview ? MacDesktopLocalEventGuard.PreviewEventTag : MacDesktopLocalEventGuard.EventTag;
        if (source == 0)
        {
            source = QuartzDesktopNative.CGEventSourceCreate(-1); // private state, distinct from other input tools.
            if (source == 0) throw new InvalidOperationException("Unable to create private Quartz source.");
            QuartzDesktopNative.CGEventSourceSetUserData(source, MacDesktopLocalEventGuard.EventTag);
            QuartzDesktopNative.CGEventSourceSetLocalEventsSuppressionInterval(source, 0);
        }
        var point = new QuartzDesktopNative.Point(position.X, position.Y);
        nint input = 0;
        try
        {
            switch (action.Kind)
            {
                case InputKind.KeyDown:
                case InputKind.KeyUp:
                    input = QuartzDesktopNative.CGEventCreateKeyboardEvent(source, action.KeyCode, action.Kind == InputKind.KeyDown);
                    if (input != 0)
                    {
                        if (action.KeyCode is 54 or 55 or 56 or 58 or 59 or 60 or 61 or 62)
                            QuartzDesktopNative.CGEventSetType(input, 12); // flagsChanged, physical left/right key code retained.
                        QuartzDesktopNative.CGEventSetIntegerValueField(input, 8, action.IsRepeat ? 1 : 0);
                    }
                    break;
                case InputKind.MoveRelative:
                    var type = action.Button switch { 0 => 6U, 1 => 7U, 2 => 27U, _ => 5U };
                    input = QuartzDesktopNative.CGEventCreateMouseEvent(source, type, point, (uint)Math.Max(0, action.Button));
                    if (input != 0)
                    {
                        var before = ReadPointer();
                        QuartzDesktopNative.CGEventSetIntegerValueField(input, 4, (long)Math.Round(position.X - before.X));
                        QuartzDesktopNative.CGEventSetIntegerValueField(input, 5, (long)Math.Round(position.Y - before.Y));
                        QuartzDesktopNative.CGEventSetDoubleValueField(input, 2, action.Button >= 0 ? 1 : 0);
                        if (action.Button >= 0)
                        {
                            clicks.MovedWhilePressed(position);
                            QuartzDesktopNative.CGEventSetIntegerValueField(input, 0, buttonNumbers[action.Button]);
                            QuartzDesktopNative.CGEventSetIntegerValueField(input, 1, buttonClickCounts[action.Button]);
                        }
                    }
                    break;
                case InputKind.ButtonDown:
                case InputKind.ButtonUp:
                    var down = action.Kind == InputKind.ButtonDown;
                    var buttonType = action.Button switch { 0 => down ? 1U : 2U, 1 => down ? 3U : 4U, 2 => down ? 25U : 26U,
                        _ => throw new ArgumentException("Unsupported mouse button.") };
                    if (down)
                    {
                        buttonNumbers[action.Button] = ++eventNumber;
                        var seconds = QuartzDesktopNative.SendDouble(QuartzDesktopNative.objc_getClass("NSEvent"), "doubleClickInterval");
                        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 10)
                            throw new InvalidOperationException("Unable to read system double-click interval.");
                        buttonClickCounts[action.Button] = clicks.Press(action.Button, position,
                            Stopwatch.GetElapsedTime(0), TimeSpan.FromSeconds(seconds));
                    }
                    input = QuartzDesktopNative.CGEventCreateMouseEvent(source, buttonType, point, (uint)action.Button);
                    if (input != 0)
                    {
                        QuartzDesktopNative.CGEventSetIntegerValueField(input, 0, buttonNumbers[action.Button]);
                        QuartzDesktopNative.CGEventSetIntegerValueField(input, 1, buttonClickCounts[action.Button]);
                        QuartzDesktopNative.CGEventSetDoubleValueField(input, 2, down ? 1 : 0);
                    }
                    break;
                case InputKind.Wheel:
                    input = QuartzDesktopNative.CGEventCreateScrollWheelEvent2(source, 1, 1, action.Lines, 0, 0);
                    if (input != 0) QuartzDesktopNative.CGEventSetLocation(input, point);
                    break;
                default:
                    throw new ArgumentException("Unsupported desktop event.");
            }
            if (input == 0) throw new InvalidOperationException("Unable to create desktop event.");
            QuartzDesktopNative.CGEventSetFlags(input, action.Flags);
            QuartzDesktopNative.CGEventSetIntegerValueField(input, 42, eventIdentifier);
            if (action.ExpectedSource is not null && !ReadDisplays().Any(d => d.Matches(action.ExpectedSource)))
                throw new InvalidOperationException("Preview source identity changed before posting input.");
            var environment = ReadEnvironment(action.Preview);
            if (environment.Failure is { Length: > 0 } changed) throw new InvalidOperationException(changed);
            if (environment.ReceiverUiActive) throw new InvalidOperationException("Never inject into the receiver's own pairing/control UI.");
            if ((action.Kind is InputKind.MoveRelative or InputKind.ButtonDown or InputKind.ButtonUp or InputKind.Wheel) &&
                ReadPointerGate(position, action.Preview) != MacDesktopGate.Allowed)
                throw new InvalidOperationException("Pointer target belongs to the receiver or cannot be verified.");
            // Apple documents that CGWarp changes the visible cursor WITHOUT
            // posting an event. Post exactly one move/drag event after the warp;
            // the runtime desktop acceptance test must still verify delivery.
            if (action.Kind == InputKind.MoveRelative && QuartzDesktopNative.CGWarpMouseCursorPosition(point) != 0)
                throw new InvalidOperationException("Unable to move visible desktop pointer.");
            QuartzDesktopNative.CGEventPost(0, input); // public kCGHIDEventTap; never targets a login session intentionally.
        }
        finally { if (input != 0) QuartzDesktopNative.CFRelease(input); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        session.Dispose();
        if (source != 0) { QuartzDesktopNative.CFRelease(source); source = 0; }
    }
}
