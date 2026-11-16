namespace Nian.Input.Mac;

public sealed record MacInputAction(InputKind Kind, ushort KeyCode = 0, int Button = -1,
    ulong Flags = 0, double X = 0, double Y = 0, int Lines = 0, bool IsRepeat = false,
    bool Preview = false, Nian.Input.Control.PreviewSourceGeometry? ExpectedSource = null);

/// <summary>Pure bookkeeping; no operating-system event injection.</summary>
public sealed class MacInputState
{
    private readonly Dictionary<InputKey, bool> keys = [];
    private readonly Dictionary<int, bool> buttons = [];
    private int wheelRemainder;
    internal bool HasHeldMouseButtons => buttons.Count != 0;

    public ulong Flags =>
        (Held(InputKey.LeftShift, InputKey.RightShift) ? 1UL << 17 : 0) |
        (Held(InputKey.LeftControl, InputKey.RightControl) ? 1UL << 18 : 0) |
        (Held(InputKey.LeftAlt, InputKey.RightAlt) ? 1UL << 19 : 0) |
        (Held(InputKey.LeftSuper, InputKey.RightSuper) ? 1UL << 20 : 0);

    public MacInputAction? Apply(InputPacket packet, bool preview = false)
    {
        switch (packet.Kind)
        {
            case InputKind.KeyDown:
            case InputKind.KeyUp:
                var key = (InputKey)packet.Code;
                if (!MacKeyMap.TryGetVirtualKey(key, out var keyCode)) throw new ArgumentException("Unsupported key usage.");
                if (packet.Kind == InputKind.KeyUp && !keys.Remove(key, out preview)) return null;
                var repeat = packet.Kind == InputKind.KeyDown && !keys.TryAdd(key, preview);
                if (repeat) preview = keys[key];
                return new(packet.Kind, KeyCode: keyCode, Flags: Flags, IsRepeat: repeat, Preview: preview);
            case InputKind.ButtonDown:
            case InputKind.ButtonUp:
                if (packet.Code is < 0 or > 2) throw new ArgumentException("Unsupported mouse button.");
                if (packet.Kind == InputKind.ButtonDown && !buttons.TryAdd(packet.Code, preview)) return null;
                if (packet.Kind == InputKind.ButtonUp && !buttons.Remove(packet.Code, out preview)) return null;
                return new(packet.Kind, Button: packet.Code, Flags: Flags, Preview: preview);
            case InputKind.MoveRelative:
                if (!double.IsFinite(packet.X) || !double.IsFinite(packet.Y) || Math.Abs(packet.X) > 32767 || Math.Abs(packet.Y) > 32767)
                    throw new ArgumentException("Invalid relative movement.");
                return new(packet.Kind, Button: buttons.ContainsKey(0) ? 0 : buttons.ContainsKey(1) ? 1 : buttons.ContainsKey(2) ? 2 : -1,
                    Flags: Flags, X: packet.X, Y: packet.Y, Preview: preview);
            case InputKind.Wheel:
                if (Math.Abs((long)packet.Delta) > 12000) throw new ArgumentException("Invalid wheel movement.");
                wheelRemainder += packet.Delta;
                var lines = wheelRemainder / 120;
                wheelRemainder %= 120;
                return lines == 0 ? null : new(packet.Kind, Flags: Flags, Lines: lines, Preview: preview);
            default:
                throw new ArgumentException("Unsupported event kind.");
        }
    }

    public IReadOnlyList<MacInputAction> ReleaseAll()
    {
        var release = new List<MacInputAction>();
        foreach (var key in keys.Keys.OrderBy(MacKeyMap.IsModifier).ThenBy(x => (int)x).ToArray())
        {
            keys.Remove(key, out var preview);
            MacKeyMap.TryGetVirtualKey(key, out var code);
            release.Add(new(InputKind.KeyUp, KeyCode: code, Flags: Flags, Preview: preview));
        }
        foreach (var button in buttons.OrderBy(value => value.Key).ToArray())
            release.Add(new(InputKind.ButtonUp, Button: button.Key, Flags: Flags, Preview: button.Value));
        buttons.Clear();
        wheelRemainder = 0;
        return release;
    }

    private bool Held(InputKey left, InputKey right) => keys.ContainsKey(left) || keys.ContainsKey(right);
}
