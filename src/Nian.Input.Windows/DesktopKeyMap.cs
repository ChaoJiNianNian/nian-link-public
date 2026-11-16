namespace Nian.Input.Windows;

/// <summary>Physical Win32 virtual/scancode mapping; no character translation or text collection.</summary>
internal static class DesktopKeyMap
{
    public static bool TryMap(int key, int scan, bool extended, out InputKey mapped)
    {
        if (key is >= 0x41 and <= 0x5a) { mapped = InputKey.A + (key - 0x41); return true; }
        if (key is >= 0x31 and <= 0x39) { mapped = InputKey.Digit1 + (key - 0x31); return true; }
        if (key is >= 0x70 and <= 0x7b) { mapped = InputKey.F1 + (key - 0x70); return true; }
        mapped = key switch
        {
            0x30 => InputKey.Digit0, 0x0d => InputKey.Enter, 0x1b => InputKey.Escape,
            0x08 => InputKey.Backspace, 0x09 => InputKey.Tab, 0x20 => InputKey.Space,
            0xbd => InputKey.Minus, 0xbb => InputKey.Equal, 0xdb => InputKey.LeftBracket,
            0xdd => InputKey.RightBracket, 0xdc => InputKey.Backslash, 0xba => InputKey.Semicolon,
            0xde => InputKey.Quote, 0xc0 => InputKey.Grave, 0xbc => InputKey.Comma,
            0xbe => InputKey.Period, 0xbf => InputKey.Slash, 0x14 => InputKey.CapsLock,
            0x2d => InputKey.Insert, 0x24 => InputKey.Home, 0x21 => InputKey.PageUp,
            0x2e => InputKey.Delete, 0x23 => InputKey.End, 0x22 => InputKey.PageDown,
            0x27 => InputKey.Right, 0x25 => InputKey.Left, 0x28 => InputKey.Down, 0x26 => InputKey.Up,
            0x11 => extended ? InputKey.RightControl : InputKey.LeftControl,
            0x10 => scan == 0x36 ? InputKey.RightShift : InputKey.LeftShift,
            0x12 => extended ? InputKey.RightAlt : InputKey.LeftAlt,
            0xa2 => InputKey.LeftControl, 0xa3 => InputKey.RightControl,
            0xa0 => InputKey.LeftShift, 0xa1 => InputKey.RightShift,
            0xa4 => InputKey.LeftAlt, 0xa5 => InputKey.RightAlt,
            0x5b => InputKey.LeftSuper, 0x5c => InputKey.RightSuper,
            _ => default
        };
        return mapped != default;
    }
}
