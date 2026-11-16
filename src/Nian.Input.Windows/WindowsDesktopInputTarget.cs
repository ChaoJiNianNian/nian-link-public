using Nian.Input.Control;

namespace Nian.Input.Windows;

/// <summary>Explicit, bounded input into an unlocked local user desktop. Never elevates or installs a global hook.</summary>
public sealed partial class WindowsDesktopInputTarget : IPreviewInputTarget, IDisposable
{
    private static readonly TimeSpan MaximumLease = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly IWindowsDesktopInputPlatform platform;
    private readonly TimeProvider clock;
    private readonly ITimer? safetyTimer;
    private readonly Dictionary<InputKey, bool> keys = [];
    private readonly Dictionary<int, bool> buttons = [];
    private bool armed, disposed;
    private long consentVersion, startedAt, appliedCount, releaseCount;
    private TimeSpan lease;
    private WindowsDesktopPoint pointer;
    private string unavailable = "请先在 Windows 本机允许桌面控制。";
    private string? lastReleaseError;

    /// <param name="isProtectedOwnWindow">Optional thread-safe local policy for root HWNDs owned by this process.
    /// Null protects the whole application. Exceptions fail closed. This callback must never be set by a peer.</param>
    public WindowsDesktopInputTarget(Func<nint, bool>? isProtectedOwnWindow = null)
        : this(new Win32DesktopInputPlatform(isProtectedOwnWindow), TimeProvider.System, true) { }

    internal WindowsDesktopInputTarget(IWindowsDesktopInputPlatform platform, TimeProvider clock, bool monitorSafety)
    {
        this.platform = platform; this.clock = clock;
        platform.SafetyChanged += OnSafetyChanged;
        if (monitorSafety) safetyTimer = clock.CreateTimer(_ => RefreshSafety(), null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
    }

    /// <summary>Security controls must also reject this marker at local window dispatch to close focus races.</summary>
    public static nuint EventMarker => WindowsDesktopInputEncoding.EventMarker;
    public static nuint PreviewEventMarker => WindowsDesktopInputEncoding.PreviewEventMarker;
    public static bool IsPreviewInjectedEvent(nint extraInfo) => unchecked((nuint)extraInfo) == PreviewEventMarker;
    public static bool IsOwnInjectedEvent(nint extraInfo) => unchecked((nuint)extraInfo) == EventMarker || IsPreviewInjectedEvent(extraInfo);
    public bool Armed { get { lock (gate) { RefreshSafetyCore(); return armed; } } }
    public bool IsReady => Armed;
    public long ConsentVersion { get { lock (gate) { RefreshSafetyCore(); return consentVersion; } } }
    public string UnavailableReason { get { lock (gate) { RefreshSafetyCore(); return armed ? "" : unavailable; } } }
    public long AppliedCount => Interlocked.Read(ref appliedCount);
    public long ReleaseCount => Interlocked.Read(ref releaseCount);
    public string? LastReleaseError { get { lock (gate) return lastReleaseError; } }
    public TimeSpan RemainingLease
    {
        get { lock (gate) { RefreshSafetyCore(); return armed ? lease - clock.GetElapsedTime(startedAt) : TimeSpan.Zero; } }
    }

    /// <summary>Called by a local visible allow action or a host after its authenticated local policy checks.</summary>
    public void SetArmed(bool value, TimeSpan? lease = null)
    {
        lock (gate)
        {
            if (disposed) { if (value) throw new ObjectDisposedException(nameof(WindowsDesktopInputTarget)); return; }
            if (!value) { Disarm("桌面控制已停止。"); return; }
            var duration = CheckedLease(lease ?? MaximumLease);
            // A repeated allow action cannot silently renew the current session.
            if (armed) { RefreshSafetyCore(); return; }
            var failure = NativeFailure(out var displays);
            if (failure.Length != 0) { unavailable = failure; return; }
            try
            {
                pointer = ClosestPoint(platform.ReadPointer(), displays);
                startedAt = clock.GetTimestamp(); this.lease = duration;
                lastReleaseError = null; armed = true; consentVersion++; unavailable = "";
            }
            catch { unavailable = "无法读取本机指针，未允许桌面控制。"; }
        }
    }

    /// <summary>Explicit host renewal; preserves the consent generation and held-input ledger.</summary>
    public bool RenewLease(long expectedConsentVersion, TimeSpan lease)
    {
        var duration = CheckedLease(lease);
        lock (gate)
        {
            RefreshSafetyCore();
            if (disposed || !armed || consentVersion != expectedConsentVersion) return false;
            startedAt = clock.GetTimestamp(); this.lease = duration; return true;
        }
    }

    private static TimeSpan CheckedLease(TimeSpan duration) => duration > TimeSpan.Zero && duration <= MaximumLease
        ? duration : throw new ArgumentOutOfRangeException(nameof(duration), "Control lease must be within five minutes.");

    /// <summary>The source capture must compare this snapshot before and after capture, then attach actual encoded dimensions.</summary>
    public WindowsDesktopSourceGeometry? GetCurrentGeometry()
    {
        lock (gate)
        {
            if (disposed || NativeFailure(out var displays).Length != 0) return null;
            return displays.SingleOrDefault(d => d.Primary)?.Source;
        }
    }

    public PreviewSourceGeometry? ReadPreviewGeometry(int imageWidth, int imageHeight)
    {
        var source = GetCurrentGeometry();
        var geometry = source?.ToPreviewGeometry(imageWidth, imageHeight);
        return geometry?.IsValid == true ? geometry : null;
    }

    public void RefreshSafety() { lock (gate) RefreshSafetyCore(); }
    private void OnSafetyChanged() { lock (gate) { if (!disposed) Disarm("系统电源或用户会话已变化，请重新开始控制。"); } }

    private void RefreshSafetyCore()
    {
        if (disposed || !armed) return;
        var elapsed = clock.GetElapsedTime(startedAt);
        var failure = elapsed < TimeSpan.Zero || elapsed >= lease ? "本次桌面控制已到期，请重新开始。" : NativeFailure(out _);
        if (failure.Length != 0) Disarm(failure);
    }

    public void Apply(InputPacket packet) { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); ApplyCore(packet); } }
    public void Apply(InputPacket packet, long expectedConsentVersion)
    {
        lock (gate) { RequireConsent(expectedConsentVersion); ApplyCore(packet); }
    }

    private void RequireConsent(long expectedConsentVersion)
    {
        ObjectDisposedException.ThrowIf(disposed, this); RefreshSafetyCore();
        if (!armed || consentVersion != expectedConsentVersion) throw new InvalidOperationException("本机桌面控制许可已改变。");
    }

    private void ApplyCore(InputPacket packet, WindowsDesktopPoint? absolutePosition = null, PreviewSourceGeometry? expectedSource = null)
    {
        RefreshSafetyCore();
        if (!armed) throw new InvalidOperationException(unavailable);
        try
        {
            if (!ValidPacket(packet)) throw new ArgumentException("Invalid desktop input packet.");
            var failure = NativeFailure(out var displays, expectedSource is not null);
            if (failure.Length != 0) throw new InvalidOperationException(failure);
            if (expectedSource is not null && !displays.Any(d => d.Matches(expectedSource)))
                throw new InvalidOperationException("Preview source changed during the action.");
            var current = absolutePosition ?? platform.ReadPointer();
            if (packet.Kind == InputKind.MoveRelative && absolutePosition is null) current = new(current.X + packet.X, current.Y + packet.Y);
            var position = ClosestPoint(current, displays);
            var preview = expectedSource is not null;
            if (IsPointer(packet.Kind) && !platform.IsPointerAllowed(position, preview)) throw new InvalidOperationException("不能操作本机安全窗口或无法确认归属的鼠标目标。");
            var key = (InputKey)packet.Code;
            if (packet.Kind == InputKind.ButtonDown && !buttons.TryAdd(packet.Code, preview)) return;
            if (packet.Kind == InputKind.ButtonUp && !buttons.ContainsKey(packet.Code)) return;
            if (packet.Kind == InputKind.KeyUp && !keys.ContainsKey(key)) return;
            // Mark a down before posting: a partial native batch may already have pressed it.
            if (packet.Kind == InputKind.KeyDown) keys.TryAdd(key, preview);
            platform.Post(new(packet.Kind, packet.Code, packet.Delta, expectedSource, preview), position);
            pointer = position;
            // Remove an up only after success, so failed ups are retried during disarm.
            if (packet.Kind == InputKind.ButtonUp) buttons.Remove(packet.Code);
            if (packet.Kind == InputKind.KeyUp) keys.Remove(key);
            Interlocked.Increment(ref appliedCount);
        }
        catch { Disarm("输入投递失败，桌面控制已停止。"); throw; }
    }

    private static bool ValidPacket(InputPacket? packet) => packet is not null && packet.Sequence > 0 && double.IsFinite(packet.X) && double.IsFinite(packet.Y) && packet.Kind switch
    {
        InputKind.MoveRelative => packet.Code == 0 && packet.Delta == 0 && Math.Abs(packet.X) <= 32767 && Math.Abs(packet.Y) <= 32767,
        InputKind.ButtonDown or InputKind.ButtonUp => packet.Code is >= 0 and <= 2 && packet.X == 0 && packet.Y == 0 && packet.Delta == 0,
        InputKind.Wheel => packet.Code == 0 && packet.X == 0 && packet.Y == 0 && packet.Delta != 0 && Math.Abs((long)packet.Delta) <= 12000,
        InputKind.KeyDown or InputKind.KeyUp => Enum.IsDefined((InputKey)packet.Code) && packet.X == 0 && packet.Y == 0 && packet.Delta == 0,
        _ => false
    };

    private string NativeFailure(out IReadOnlyList<WindowsDesktopDisplay> displays, bool preview = false)
    {
        displays = [];
        try
        {
            var failure = platform.ReadEnvironment(preview).Failure;
            if (failure.Length != 0) return failure;
            displays = platform.ReadDisplays();
            if (displays.Count is < 1 or > 64 || displays.Any(d => !d.IsValid) || displays.Count(d => d.Primary) != 1 ||
                displays.Select(d => d.SourceId).Distinct(StringComparer.Ordinal).Count() != displays.Count)
                return "无法确认实际显示器范围。";
            return "";
        }
        catch { return "无法确认本机桌面安全状态。"; }
    }

    private static WindowsDesktopPoint ClosestPoint(WindowsDesktopPoint point, IReadOnlyList<WindowsDesktopDisplay> displays)
    {
        if (!point.IsValid) throw new ArgumentException("Invalid native pointer.");
        return displays.Select(d => d.Bounds.Clamp(point)).MinBy(p => Math.Pow(p.X - point.X, 2) + Math.Pow(p.Y - point.Y, 2));
    }

    public void ReleaseAll() { lock (gate) { if (!disposed) Disarm("控制连接已结束。"); } }
    public void ReleaseAll(long expectedConsentVersion)
    {
        lock (gate) { if (!disposed && consentVersion == expectedConsentVersion) Disarm("控制连接已结束。"); }
    }

    private void Disarm(string reason)
    {
        if (armed) { armed = false; consentVersion++; }
        unavailable = reason;
        var releases = keys.Where(k => (int)k.Key < 224).OrderBy(k => k.Key).Select(k => new WindowsDesktopAction(InputKind.KeyUp, (int)k.Key, Preview: k.Value))
            .Concat(buttons.OrderBy(b => b.Key).Select(b => new WindowsDesktopAction(InputKind.ButtonUp, b.Key, Preview: b.Value)))
            .Concat(keys.Where(k => (int)k.Key >= 224).OrderBy(k => k.Key).Select(k => new WindowsDesktopAction(InputKind.KeyUp, (int)k.Key, Preview: k.Value))).ToArray();
        keys.Clear(); buttons.Clear();
        Interlocked.Add(ref releaseCount, releases.Length);
        foreach (var release in releases)
        {
            if (NativeFailure(out var displays, release.Preview).Length != 0) { lastReleaseError = "桌面状态不安全；已清空本地按下状态，未投递释放。"; break; }
            try
            {
                pointer = ClosestPoint(pointer, displays);
                if (IsPointer(release.Kind) && !platform.IsPointerAllowed(pointer, release.Preview)) { lastReleaseError = "鼠标释放目标受到保护；本地按下状态已清空。"; continue; }
                platform.Post(release, pointer);
            }
            catch { lastReleaseError = "部分系统释放投递失败；本地按下状态已清空。"; }
        }
    }

    private static bool IsPointer(InputKind kind) => kind is InputKind.MoveRelative or InputKind.ButtonDown or InputKind.ButtonUp or InputKind.Wheel;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Disarm("桌面控制已关闭。"); disposed = true;
            platform.SafetyChanged -= OnSafetyChanged; safetyTimer?.Dispose(); platform.Dispose();
        }
    }
}
