namespace Nian.Input.Mac;

/// <summary>Maps USB keyboard usages to Apple's physical ANSI virtual-key positions.</summary>
public static class MacKeyMap
{
    // Published macOS SDK HIToolbox/Events.h kVK_* constants; these are physical
    // ANSI positions. The operating system's selected layout produces characters.
    public static bool TryGetVirtualKey(InputKey key, out ushort code)
    {
        var value = key switch
        {
            InputKey.A => 0, InputKey.B => 11, InputKey.C => 8, InputKey.D => 2,
            InputKey.E => 14, InputKey.F => 3, InputKey.G => 5, InputKey.H => 4,
            InputKey.I => 34, InputKey.J => 38, InputKey.K => 40, InputKey.L => 37,
            InputKey.M => 46, InputKey.N => 45, InputKey.O => 31, InputKey.P => 35,
            InputKey.Q => 12, InputKey.R => 15, InputKey.S => 1, InputKey.T => 17,
            InputKey.U => 32, InputKey.V => 9, InputKey.W => 13, InputKey.X => 7,
            InputKey.Y => 16, InputKey.Z => 6,
            InputKey.Digit1 => 18, InputKey.Digit2 => 19, InputKey.Digit3 => 20,
            InputKey.Digit4 => 21, InputKey.Digit5 => 23, InputKey.Digit6 => 22,
            InputKey.Digit7 => 26, InputKey.Digit8 => 28, InputKey.Digit9 => 25, InputKey.Digit0 => 29,
            InputKey.Enter => 36, InputKey.Escape => 53, InputKey.Backspace => 51,
            InputKey.Tab => 48, InputKey.Space => 49, InputKey.Minus => 27, InputKey.Equal => 24,
            InputKey.LeftBracket => 33, InputKey.RightBracket => 30, InputKey.Backslash => 42,
            InputKey.Semicolon => 41, InputKey.Quote => 39, InputKey.Grave => 50,
            InputKey.Comma => 43, InputKey.Period => 47, InputKey.Slash => 44, InputKey.CapsLock => 57,
            InputKey.F1 => 122, InputKey.F2 => 120, InputKey.F3 => 99, InputKey.F4 => 118,
            InputKey.F5 => 96, InputKey.F6 => 97, InputKey.F7 => 98, InputKey.F8 => 100,
            InputKey.F9 => 101, InputKey.F10 => 109, InputKey.F11 => 103, InputKey.F12 => 111,
            InputKey.Insert => 114, // Mac Help position; not a universal Insert command.
            InputKey.Home => 115, InputKey.PageUp => 116, InputKey.Delete => 117,
            InputKey.End => 119, InputKey.PageDown => 121, InputKey.Right => 124,
            InputKey.Left => 123, InputKey.Down => 125, InputKey.Up => 126,
            InputKey.LeftControl => 59, InputKey.LeftShift => 56, InputKey.LeftAlt => 58,
            InputKey.LeftSuper => 55, InputKey.RightControl => 62, InputKey.RightShift => 60,
            InputKey.RightAlt => 61, InputKey.RightSuper => 54,
            _ => -1
        };
        code = value < 0 ? (ushort)0 : (ushort)value;
        return value >= 0;
    }
    public static bool IsModifier(InputKey key) => key is >= InputKey.LeftControl and <= InputKey.RightSuper;
}
