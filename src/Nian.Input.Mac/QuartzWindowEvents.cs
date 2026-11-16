namespace Nian.Input.Mac;

public sealed record MacPointerEvidence(uint Type, int WindowNumber, double LocalX, double LocalY,
    double ScreenX, double ScreenY, double WheelDelta, long EventNumber, double Pressure);

/// <summary>Public AppKit factories attach the receiving window before PID-only delivery.</summary>
internal static class QuartzWindowEvents
{
    private static readonly uint[] WheelFields = [11, 12, 13, 93, 94, 95, 96, 97, 98, 88];

    internal static nint Create(MacInputAction action, double x, double y, MacInputWindow window,
        nint source, int eventNumber, out MacPointerEvidence evidence)
    {
        var type = action.Kind switch
        {
            InputKind.MoveRelative => action.Button switch { 0 => 6U, 1 => 7U, 2 => 27U, _ => 5U },
            InputKind.ButtonDown => action.Button switch { 0 => 1U, 1 => 3U, _ => 25U },
            InputKind.ButtonUp => action.Button switch { 0 => 2U, 1 => 4U, _ => 26U },
            InputKind.Wheel => 22U,
            _ => throw new ArgumentException("Expected a pointer event.")
        };
        var local = new QuartzNative.CgPoint(x - window.X, window.Height - (y - window.Y));
        var processInfo = QuartzNative.Send(QuartzNative.objc_getClass("NSProcessInfo"), "processInfo");
        var stamp = QuartzNative.SendDouble(processInfo, "systemUptime");
        var seed = QuartzNative.CreateMouseEvent(type == 22 ? 5U : type, local, action.Flags, stamp,
            window.Number, eventNumber, action.Kind is InputKind.ButtonDown or InputKind.ButtonUp ? 1 : 0,
            action.Kind == InputKind.ButtonDown || action.Kind == InputKind.MoveRelative && action.Button >= 0 ? 1 : 0);
        if (seed == 0) throw new InvalidOperationException("AppKit could not create a window mouse event.");
        var borrowed = QuartzNative.Send(seed, "CGEvent");
        var input = borrowed == 0 ? 0 : QuartzNative.CGEventCreateCopy(borrowed);
        if (input == 0) throw new InvalidOperationException("Could not copy the window event.");
        try
        {
            QuartzNative.CGEventSetSource(input, source);
            if (action.Kind == InputKind.Wheel)
            {
                // A typed wheel template supplies the public line/fixed/point delta
                // fields. The AppKit seed retains the receiving window metadata.
                var wheel = QuartzNative.CGEventCreateScrollWheelEvent2(source, 1, 1, action.Lines, 0, 0);
                if (wheel == 0) throw new InvalidOperationException("Could not create wheel metadata.");
                try
                {
                    QuartzNative.CGEventSetType(input, type);
                    foreach (var field in WheelFields)
                        QuartzNative.CGEventSetIntegerValueField(input, field,
                            QuartzNative.CGEventGetIntegerValueField(wheel, field));
                }
                finally { QuartzNative.CFRelease(wheel); }
            }
            else
            {
                QuartzNative.CGEventSetIntegerValueField(input, 3, Math.Max(0, action.Button));
                if (action.Kind == InputKind.MoveRelative)
                {
                    QuartzNative.CGEventSetIntegerValueField(input, 4, (long)Math.Round(action.X));
                    QuartzNative.CGEventSetIntegerValueField(input, 5, (long)Math.Round(action.Y));
                }
            }
            QuartzNative.CGEventSetFlags(input, action.Flags);
            var converted = QuartzNative.EventFromCg(input);
            if (converted == 0 || QuartzNative.SendInt(converted, "windowNumber") != window.Number ||
                QuartzNative.SendInt(converted, "type") != type || QuartzNative.Send(converted, "window") == 0)
                throw new InvalidOperationException("Native pointer event lost its own-window route.");
            var actual = QuartzNative.ReadPoint(converted, "locationInWindow");
            if (Math.Abs(actual.X - local.X) > 0.5 || Math.Abs(actual.Y - local.Y) > 0.5)
                throw new InvalidOperationException("Native pointer coordinates do not match the experiment window.");
            evidence = new(type, window.Number, actual.X, actual.Y, x, y,
                type == 22 ? QuartzNative.SendDouble(converted, "scrollingDeltaY") : 0,
                QuartzNative.CGEventGetIntegerValueField(input, 0), QuartzNative.CGEventGetDoubleValueField(input, 2));
            return input;
        }
        catch { QuartzNative.CFRelease(input); throw; }
    }
}
