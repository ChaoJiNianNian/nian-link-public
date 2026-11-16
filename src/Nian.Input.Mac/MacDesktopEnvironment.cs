namespace Nian.Input.Mac;

internal enum MacDesktopGate { Unknown, Allowed, Blocked }

/// <summary>No input data is included in a native safety snapshot.</summary>
internal sealed record MacDesktopEnvironment(MacDesktopGate Accessibility, MacDesktopGate InteractiveSession,
    MacDesktopGate ScreenUnlocked, MacDesktopGate SystemAwake, MacDesktopGate SecureInputDisabled,
    MacDesktopGate SafeForeground, bool ReceiverUiActive = false)
{
    internal static MacDesktopEnvironment Unknown { get; } = new(MacDesktopGate.Unknown, MacDesktopGate.Unknown,
        MacDesktopGate.Unknown, MacDesktopGate.Unknown, MacDesktopGate.Unknown, MacDesktopGate.Unknown);

    internal string Failure => Accessibility != MacDesktopGate.Allowed ? "尚未获得辅助功能权限。" :
        InteractiveSession != MacDesktopGate.Allowed ? "当前不是本机用户的交互桌面。" :
        SystemAwake != MacDesktopGate.Allowed ? "系统正在休眠或尚未确认唤醒。" :
        SecureInputDisabled != MacDesktopGate.Allowed ? "安全输入已开启或状态不明，已停止桌面控制。" :
        SafeForeground != MacDesktopGate.Allowed ? "当前是登录、系统授权界面或无法确认前台应用。" :
        ScreenUnlocked != MacDesktopGate.Allowed ? "桌面已锁定或无法确认解锁状态。" : "";

    internal MacDesktopEnvironment WithGate(int index, MacDesktopGate value) => index switch
    {
        0 => this with { Accessibility = value }, 1 => this with { InteractiveSession = value },
        2 => this with { ScreenUnlocked = value }, 3 => this with { SystemAwake = value },
        4 => this with { SecureInputDisabled = value }, 5 => this with { SafeForeground = value },
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}

internal readonly record struct MacDesktopPoint(double X, double Y)
{
    internal bool IsValid => double.IsFinite(X) && double.IsFinite(Y);
}

internal readonly record struct MacDesktopDisplay(double X, double Y, double Width, double Height, string? SourceId = null, string? Revision = null)
{
    internal bool Matches(Nian.Input.Control.PreviewSourceGeometry geometry) => SourceId == geometry.SourceId && Revision == geometry.Revision &&
        X == geometry.Left && Y == geometry.Top && Width == geometry.Width && Height == geometry.Height;
    internal bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) &&
        double.IsFinite(Height) && Width > 1 && Height > 1 && double.IsFinite(X + Width) &&
        double.IsFinite(Y + Height) && X + Width > X && Y + Height > Y;

    internal MacDesktopPoint Clamp(MacDesktopPoint point) => new(
        Math.Clamp(point.X, X, X + Width - 0.5), Math.Clamp(point.Y, Y, Y + Height - 0.5));
}

internal interface IMacDesktopPlatform : IDisposable
{
    event Action? SafetyChanged;
    MacDesktopEnvironment ReadEnvironment();
    MacDesktopEnvironment ReadEnvironment(bool preview) => ReadEnvironment();
    IReadOnlyList<MacDesktopDisplay> ReadDisplays();
    MacDesktopPoint ReadPointer();
    MacDesktopGate ReadPointerGate(MacDesktopPoint position);
    MacDesktopGate ReadPointerGate(MacDesktopPoint position, bool preview) => ReadPointerGate(position);
    void ResetSequence();
    void Post(MacInputAction action, MacDesktopPoint position);
}
