namespace Nian.Input.Mac;

/// <summary>
/// Explicitly allowed, short-lived control of an unlocked user desktop. This target
/// is separate from the own-process experiment and never uses its event factory.
/// Construct/dispose the production native platform on the AppKit UI thread.
/// </summary>
public sealed partial class MacDesktopInputTarget : Nian.Input.Control.IPreviewInputTarget, IDisposable
{
    private static readonly TimeSpan MaximumLease = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly IMacDesktopPlatform platform;
    private readonly TimeProvider clock;
    private readonly MacInputState state = new();
    private readonly ITimer? safetyTimer;
    private bool armed, disposed;
    private bool waitingForDesktop, enteredDesktop;
    private long consentVersion, startedAt, appliedCount, releaseCount;
    private TimeSpan lease;
    private MacDesktopPoint pointer;
    private string? lastReleaseError;
    private string lastUnavailableReason = "请先在 Mac 本机允许桌面控制。";

    public MacDesktopInputTarget() : this(new QuartzDesktopPlatform(), TimeProvider.System, true) { }
    public MacDesktopInputTarget(MacDesktopOwnUiPolicy ownUiPolicy) : this(new QuartzDesktopPlatform(ownUiPolicy), TimeProvider.System, true) { }

    internal MacDesktopInputTarget(IMacDesktopPlatform platform, TimeProvider clock, bool monitorSafety)
    {
        this.platform = platform;
        this.clock = clock;
        platform.SafetyChanged += OnNativeSafetyTransition;
        if (monitorSafety)
            safetyTimer = clock.CreateTimer(_ => RefreshSafety(), null,
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
    }

    public bool Armed { get { lock (gate) { RefreshSafetyCore(); return armed; } } }
    public long ConsentVersion { get { lock (gate) { RefreshSafetyCore(); return consentVersion; } } }
    public bool IsReady { get { lock (gate) { RefreshSafetyCore(); return armed && !waitingForDesktop; } } }
    public string UnavailableReason
    {
        get { lock (gate) { RefreshSafetyCore(); return armed && !waitingForDesktop ? "" : lastUnavailableReason; } }
    }
    public long AppliedCount => Interlocked.Read(ref appliedCount);
    public long ReleaseCount => Interlocked.Read(ref releaseCount);
    public string? LastReleaseError { get { lock (gate) return lastReleaseError; } }
    public TimeSpan RemainingLease
    {
        get
        {
            lock (gate)
            {
                RefreshSafetyCore();
                var remaining = armed ? lease - clock.GetElapsedTime(startedAt) : TimeSpan.Zero;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Must be called in response to a local user's visible allow action.</summary>
    public void SetArmed(bool value, TimeSpan? lease = null)
    {
        lock (gate)
        {
            if (disposed)
            {
                if (value) throw new ObjectDisposedException(nameof(MacDesktopInputTarget));
                return;
            }
            if (!value)
            {
                Disarm("桌面控制已停止。");
                return;
            }
            var requestedLease = lease ?? MaximumLease;
            if (requestedLease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
            // Repeated local clicks cannot silently renew a running network session.
            if (armed) { RefreshSafetyCore(); return; }
            var failure = NativeFailure(out var displays, out var receiverUi, permitReceiverUi: true);
            if (failure.Length != 0) { lastUnavailableReason = failure; return; }
            try
            {
                pointer = ClosestPoint(platform.ReadPointer(), displays);
                platform.ResetSequence();
                startedAt = clock.GetTimestamp();
                this.lease = requestedLease < MaximumLease ? requestedLease : MaximumLease;
                lastReleaseError = null;
                armed = true;
                waitingForDesktop = receiverUi;
                enteredDesktop = !receiverUi;
                consentVersion++;
                lastUnavailableReason = receiverUi ? "已允许，请切换到普通测试应用，再从 Windows 开始控制。" : "";
            }
            catch { lastUnavailableReason = "无法读取桌面指针，未允许控制。"; }
        }
    }

    /// <summary>Also called by native sleep/session notifications and a short watchdog.</summary>
    public void RefreshSafety() { lock (gate) RefreshSafetyCore(); }

    /// <summary>Explicit authenticated-host renewal; never resets consent, native click state or held input.</summary>
    public bool RenewLease(long expectedConsentVersion, TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero || lease > MaximumLease) throw new ArgumentOutOfRangeException(nameof(lease));
        lock (gate)
        {
            RefreshSafetyCore();
            if (disposed || !armed || waitingForDesktop || consentVersion != expectedConsentVersion) return false;
            if (NativeFailure(out _).Length != 0) { Disarm("本机桌面不安全，控制已停止。"); return false; }
            startedAt = clock.GetTimestamp(); this.lease = lease; return true;
        }
    }

    private void OnNativeSafetyTransition()
    {
        lock (gate)
        {
            if (disposed) return;
            // Wake/session-activation must revoke too: a missed prior sleep event
            // must never make an old consent usable in a new desktop session.
            Disarm("系统休眠或用户会话已变化，请在 Mac 本机重新允许。");
        }
    }

    public void Apply(InputPacket packet, long expectedConsentVersion)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (consentVersion != expectedConsentVersion)
                throw new InvalidOperationException("本机允许状态已改变，请重新开始控制。");
            ApplyCore(packet);
        }
    }

    public void Apply(InputPacket packet)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ApplyCore(packet);
        }
    }

    private void ApplyCore(InputPacket packet, MacDesktopPoint? absolutePosition = null, Nian.Input.Control.PreviewSourceGeometry? expectedSource = null)
    {
        RefreshSafetyCore(checkNative: false);
        if (!armed) throw new InvalidOperationException(lastUnavailableReason);
        MacInputAction? attempted = null;
        try
        {
            var preview = expectedSource is not null;
            var failure = NativeFailure(out var displays, preview);
            if (failure.Length != 0) throw new InvalidOperationException(failure);
            if (expectedSource is not null && !displays.Any(d => d.Matches(expectedSource))) throw new InvalidOperationException("Preview source changed during the action.");
            enteredDesktop = true;
            waitingForDesktop = false;
            // Pointer hit testing happens before changing the held-input ledger:
            // clicking an inactive receiver window must not activate its controls.
            var current = absolutePosition ?? platform.ReadPointer();
            if (packet.Kind == InputKind.MoveRelative && absolutePosition is null) current = new(current.X + packet.X, current.Y + packet.Y);
            pointer = ClosestPoint(current, displays);
            if (IsPointerPacket(packet.Kind) && platform.ReadPointerGate(pointer, preview) != MacDesktopGate.Allowed)
            {
                // A preview may cross an excluded point while entering the image.
                // Ignore unpressed hover without posting any native event; clicks,
                // held drags, wheels and ordinary keyboard sharing still fail closed.
                if (preview && packet.Kind == InputKind.MoveRelative && !state.HasHeldMouseButtons) return;
                throw new InvalidOperationException("鼠标目标是本机实验窗口或无法确认归属，已停止控制。");
            }
            var action = state.Apply(packet, preview);
            if (action is null) return;
            action = action with { ExpectedSource = expectedSource };
            attempted = action;
            // Respect a concurrent physical local mouse, and display changes. Quartz
            // display coordinates are logical global points, including negative origins.
            platform.Post(action, pointer);
            Interlocked.Increment(ref appliedCount);
        }
        catch
        {
            // Apply removes an up event from its pure ledger before native posting.
            // A failed creation/post must not strand that previously held input.
            // Retry the release before releasing any remaining modifier keys.
            if (attempted?.Kind is InputKind.KeyUp or InputKind.ButtonUp)
                RetryFailedRelease(attempted);
            Disarm("输入投递失败，桌面控制已停止。");
            throw;
        }
    }

    private void RetryFailedRelease(MacInputAction release)
    {
        release = release with { ExpectedSource = null }; // A safety release is independent of an expired frame proof.
        Interlocked.Increment(ref releaseCount);
        if (NativeFailure(out var displays, release.Preview).Length != 0)
        {
            lastReleaseError = "桌面状态已改变；未重试向系统界面发送释放。";
            return;
        }
        try
        {
            pointer = ClosestPoint(platform.ReadPointer(), displays);
            if (IsPointerPacket(release.Kind) && platform.ReadPointerGate(pointer, release.Preview) != MacDesktopGate.Allowed)
            {
                lastReleaseError = "释放目标是本机实验窗口或无法确认归属，未发送鼠标释放。";
                return;
            }
            platform.Post(release, pointer);
        }
        catch { lastReleaseError = "系统释放重试失败；本地按下状态已清空。"; }
    }

    /// <summary>A stopped/disconnected control session also revokes local consent.</summary>
    public void ReleaseAll()
    {
        lock (gate)
        {
            if (disposed) return;
            Disarm("控制连接已结束，请在 Mac 本机重新允许。");
        }
    }

    /// <summary>Old connection cleanup cannot revoke a later local user's consent.</summary>
    public void ReleaseAll(long expectedConsentVersion)
    {
        lock (gate)
        {
            if (disposed || consentVersion != expectedConsentVersion) return;
            // The comparison and disarm share the same gate as local allow/stop
            // and injection, so no new consent can appear between the two.
            Disarm("控制连接已结束，请在 Mac 本机重新允许。");
        }
    }

    private void RefreshSafetyCore(bool checkNative = true)
    {
        if (disposed || !armed) return;
        var elapsed = clock.GetElapsedTime(startedAt);
        var failure = elapsed < TimeSpan.Zero || elapsed >= lease ? "本次桌面控制已到期，请在 Mac 本机重新允许。" : "";
        if (failure.Length == 0 && checkNative)
        {
            failure = NativeFailure(out _, out var receiverUi, permitReceiverUi: true);
            if (failure.Length == 0)
            {
                if (receiverUi && enteredDesktop) failure = "已返回本机实验窗口，桌面控制已停止。";
                else
                {
                    waitingForDesktop = receiverUi;
                    enteredDesktop |= !receiverUi;
                    lastUnavailableReason = receiverUi ? "已允许，请切换到普通测试应用，再从 Windows 开始控制。" : "";
                }
            }
        }
        if (failure.Length != 0) Disarm(failure);
    }

    private string NativeFailure(out IReadOnlyList<MacDesktopDisplay> displays, bool preview = false)
        => NativeFailure(out displays, out _, permitReceiverUi: false, preview: preview);

    private string NativeFailure(out IReadOnlyList<MacDesktopDisplay> displays, out bool receiverUi, bool permitReceiverUi, bool preview = false)
    {
        displays = [];
        receiverUi = false;
        try
        {
            var environment = platform.ReadEnvironment(preview);
            receiverUi = environment.ReceiverUiActive;
            var failure = environment.Failure;
            if (failure.Length != 0) return failure;
            if (receiverUi && !permitReceiverUi) return "不能远程操作本机实验程序的控制或配对界面。";
            displays = platform.ReadDisplays();
            if (displays.Count == 0 || displays.Count > 64 || displays.Any(d => !d.IsValid))
                return "无法确认实际显示器范围，已停止桌面控制。";
            return "";
        }
        catch { return "无法确认本机桌面安全状态，已停止控制。"; }
    }

    private static MacDesktopPoint ClosestPoint(MacDesktopPoint point, IReadOnlyList<MacDesktopDisplay> displays)
    {
        if (!point.IsValid) throw new InvalidOperationException("桌面指针位置无效。");
        var best = default(MacDesktopPoint);
        var bestDistance = double.PositiveInfinity;
        foreach (var display in displays)
        {
            var candidate = display.Clamp(point);
            var distance = Math.Sqrt(Math.Pow(point.X - candidate.X, 2) + Math.Pow(point.Y - candidate.Y, 2));
            if (distance < bestDistance) { bestDistance = distance; best = candidate; }
        }
        if (!double.IsFinite(bestDistance)) throw new InvalidOperationException("显示器位置无效。");
        return best;
    }

    private void Disarm(string reason)
    {
        if (armed) { armed = false; consentVersion++; }
        waitingForDesktop = false;
        enteredDesktop = false;
        lastUnavailableReason = reason;
        ReleaseCore();
    }

    private void ReleaseCore()
    {
        var releases = state.ReleaseAll();
        Interlocked.Add(ref releaseCount, releases.Count);
        if (releases.Count == 0) return;
        // Never send even releases into a login/password/unknown session. The local
        // ledger is still cleared so it cannot replay old held state after unlock.
        if (NativeFailure(out var displays).Length != 0)
        {
            lastReleaseError = "桌面状态不安全；已清空本地按下状态，未向系统界面发送释放。";
            return;
        }
        try { pointer = ClosestPoint(platform.ReadPointer(), displays); }
        catch { lastReleaseError = "无法确认指针位置；已清空本地按下状态。"; return; }
        foreach (var release in releases)
        {
            try
            {
                // Recheck for a safety transition between individual releases.
                if (NativeFailure(out _, release.Preview).Length != 0)
                {
                    lastReleaseError = "释放期间桌面状态已改变；剩余本地按下状态已清空。";
                    break;
                }
                if (IsPointerPacket(release.Kind) && platform.ReadPointerGate(pointer, release.Preview) != MacDesktopGate.Allowed)
                {
                    lastReleaseError = "释放目标是本机实验窗口或无法确认归属，未发送鼠标释放。";
                    continue;
                }
                platform.Post(release, pointer);
            }
            catch { lastReleaseError = "部分系统释放投递失败；本地按下状态已清空。"; }
        }
    }

    private static bool IsPointerPacket(InputKind kind) => kind is InputKind.MoveRelative or InputKind.ButtonDown or
        InputKind.ButtonUp or InputKind.Wheel;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Disarm("桌面控制实验已关闭。");
            disposed = true;
            platform.SafetyChanged -= OnNativeSafetyTransition;
            safetyTimer?.Dispose();
            platform.Dispose();
        }
    }
}
