namespace Nian.Input.Windows;

internal static class WindowsDesktopCoordinateMap
{
    internal static (int X, int Y) Normalize(WindowsDesktopPoint point, WindowsDesktopBounds desktop)
    {
        if (!desktop.Contains(point)) throw new ArgumentException("Pointer is outside the physical virtual desktop.");
        return ((int)Math.Round((point.X - desktop.Left) * 65535 / (desktop.Width - 1)),
            (int)Math.Round((point.Y - desktop.Top) * 65535 / (desktop.Height - 1)));
    }
}

internal sealed record WindowsDesktopEncodedInput(bool Keyboard, ushort ScanCode, uint Flags, int X, int Y, uint MouseData, nuint ExtraInfo);

/// <summary>Pure Win32 encoding. No native calls, Unicode text translation or keyboard-layout mutation.</summary>
internal static class WindowsDesktopInputEncoding
{
    internal static readonly nuint EventMarker = 0x4e49414e; // NIAN, fits both Win32 and Win64 ULONG_PTR.
    internal static readonly nuint PreviewEventMarker = 0x4e505256; // NPRV; never eligible to dispatch into our UI.
    private static readonly ushort[] Letters = [0x1e, 0x30, 0x2e, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1f, 0x14, 0x16, 0x2f, 0x11, 0x2d, 0x15, 0x2c];

    internal static WindowsDesktopEncodedInput[] Encode(WindowsDesktopAction action, WindowsDesktopPoint position, WindowsDesktopBounds desktop)
    {
        var marker = action.Preview || action.ExpectedSource is not null ? PreviewEventMarker : EventMarker;
        if (action.Kind is InputKind.KeyDown or InputKind.KeyUp)
        {
            var (scan, extended) = Scan((InputKey)action.Code);
            return [new(true, scan, 0x8U | (extended ? 0x1U : 0) | (action.Kind == InputKind.KeyUp ? 0x2U : 0), 0, 0, 0, marker)];
        }
        var normalized = WindowsDesktopCoordinateMap.Normalize(position, desktop);
        var move = new WindowsDesktopEncodedInput(false, 0, 0xC001, normalized.X, normalized.Y, 0, marker);
        if (action.Kind == InputKind.MoveRelative) return [move];
        uint flags;
        if (action.Kind == InputKind.Wheel) flags = 0x800;
        else if (action.Kind is InputKind.ButtonDown or InputKind.ButtonUp)
            flags = (action.Code, action.Kind == InputKind.ButtonDown) switch
            {
                (0, true) => 0x2U, (0, false) => 0x4U,
                (1, true) => 0x8U, (1, false) => 0x10U,
                (2, true) => 0x20U, (2, false) => 0x40U,
                _ => throw new ArgumentException("Unsupported mouse button.")
            };
        else throw new ArgumentException("Unsupported native input kind.");
        // The absolute move and following click/wheel enter the same SendInput batch.
        return [move, new(false, 0, flags, 0, 0, action.Kind == InputKind.Wheel ? unchecked((uint)action.Delta) : 0, marker)];
    }

    private static (ushort Scan, bool Extended) Scan(InputKey key)
    {
        var usage = (int)key;
        if (usage is >= (int)InputKey.A and <= (int)InputKey.Z) return (Letters[usage - (int)InputKey.A], false);
        if (usage is >= (int)InputKey.Digit1 and <= (int)InputKey.Digit0) return ((ushort)(0x2 + usage - (int)InputKey.Digit1), false);
        if (usage is >= (int)InputKey.F1 and <= (int)InputKey.F10) return ((ushort)(0x3b + usage - (int)InputKey.F1), false);
        return key switch
        {
            InputKey.Enter => (0x1c, false), InputKey.Escape => (0x1, false), InputKey.Backspace => (0x0e, false),
            InputKey.Tab => (0x0f, false), InputKey.Space => (0x39, false), InputKey.Minus => (0x0c, false),
            InputKey.Equal => (0x0d, false), InputKey.LeftBracket => (0x1a, false), InputKey.RightBracket => (0x1b, false),
            InputKey.Backslash => (0x2b, false), InputKey.Semicolon => (0x27, false), InputKey.Quote => (0x28, false),
            InputKey.Grave => (0x29, false), InputKey.Comma => (0x33, false), InputKey.Period => (0x34, false),
            InputKey.Slash => (0x35, false), InputKey.CapsLock => (0x3a, false), InputKey.F11 => (0x57, false), InputKey.F12 => (0x58, false),
            InputKey.Insert => (0x52, true), InputKey.Home => (0x47, true), InputKey.PageUp => (0x49, true),
            InputKey.Delete => (0x53, true), InputKey.End => (0x4f, true), InputKey.PageDown => (0x51, true),
            InputKey.Right => (0x4d, true), InputKey.Left => (0x4b, true), InputKey.Down => (0x50, true), InputKey.Up => (0x48, true),
            InputKey.LeftControl => (0x1d, false), InputKey.LeftShift => (0x2a, false), InputKey.LeftAlt => (0x38, false), InputKey.LeftSuper => (0x5b, true),
            InputKey.RightControl => (0x1d, true), InputKey.RightShift => (0x36, false), InputKey.RightAlt => (0x38, true), InputKey.RightSuper => (0x5c, true),
            _ => throw new ArgumentException("Unsupported physical key usage.")
        };
    }
}
