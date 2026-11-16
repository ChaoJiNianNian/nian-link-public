using System.Windows.Input;
namespace Nian.Input.Windows;

public static class WindowsKeyMap
{
    public static bool TryMap(Key key, Key systemKey, out InputKey mapped)
    {
        if(key==Key.System)key=systemKey;
        if(key>=Key.A&&key<=Key.Z){mapped=(InputKey)((int)InputKey.A+(int)key-(int)Key.A);return true;}
        if(key>=Key.D1&&key<=Key.D9){mapped=(InputKey)((int)InputKey.Digit1+(int)key-(int)Key.D1);return true;}
        if(key>=Key.F1&&key<=Key.F12){mapped=(InputKey)((int)InputKey.F1+(int)key-(int)Key.F1);return true;}
        mapped=key switch
        {
            Key.D0=>InputKey.Digit0,Key.Enter=>InputKey.Enter,Key.Escape=>InputKey.Escape,
            Key.Back=>InputKey.Backspace,Key.Tab=>InputKey.Tab,Key.Space=>InputKey.Space,
            Key.OemMinus=>InputKey.Minus,Key.OemPlus=>InputKey.Equal,
            Key.OemOpenBrackets=>InputKey.LeftBracket,Key.OemCloseBrackets=>InputKey.RightBracket,
            Key.OemPipe=>InputKey.Backslash,Key.OemSemicolon=>InputKey.Semicolon,
            Key.OemQuotes=>InputKey.Quote,Key.OemTilde=>InputKey.Grave,
            Key.OemComma=>InputKey.Comma,Key.OemPeriod=>InputKey.Period,Key.OemQuestion=>InputKey.Slash,
            Key.CapsLock=>InputKey.CapsLock,Key.Insert=>InputKey.Insert,Key.Home=>InputKey.Home,
            Key.PageUp=>InputKey.PageUp,Key.Delete=>InputKey.Delete,Key.End=>InputKey.End,
            Key.PageDown=>InputKey.PageDown,Key.Right=>InputKey.Right,Key.Left=>InputKey.Left,
            Key.Down=>InputKey.Down,Key.Up=>InputKey.Up,
            Key.LeftCtrl=>InputKey.LeftControl,Key.RightCtrl=>InputKey.RightControl,
            Key.LeftShift=>InputKey.LeftShift,Key.RightShift=>InputKey.RightShift,
            Key.LeftAlt=>InputKey.LeftAlt,Key.RightAlt=>InputKey.RightAlt,
            Key.LWin=>InputKey.LeftSuper,Key.RWin=>InputKey.RightSuper,
            _=>default
        };
        return (int)mapped!=0;
    }
}
