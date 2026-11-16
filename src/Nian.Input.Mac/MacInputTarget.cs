using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nian.Input.Mac.Tests")]

namespace Nian.Input.Mac;

public sealed record MacInputBounds(double X, double Y, double Width, double Height, MacInputWindow? Window = null)
{
    private const double EdgeTolerance = 1e-6;
    internal bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) &&
        double.IsFinite(Height) && Width > 1 && Height > 1 && double.IsFinite(X + Width) && double.IsFinite(Y + Height) &&
        X + Width > X && Y + Height > Y && Window is { IsValid: true } &&
        X >= Window.X - EdgeTolerance && Y >= Window.Y - EdgeTolerance &&
        X + Width <= Window.X + Window.Width + EdgeTolerance &&
        Y + Height <= Window.Y + Window.Height + EdgeTolerance;
}

internal interface IMacEventPlatform
{
    bool AccessibilityAllowed { get; }
    bool OwnProcessFrontmost { get; }
    void Post(MacInputAction action, double x, double y, MacInputWindow window);
}

public sealed class MacInputTarget : IInputTarget, IDisposable
{
    private readonly object gate = new();
    private readonly Func<bool> ownWindowActive;
    private readonly Func<MacInputBounds> getBounds;
    private readonly IMacEventPlatform platform;
    private readonly MacInputState state = new();
    private bool armed, disposed;
    private double x, y;
    private int armedWindowNumber;
    private MacInputWindow? lastValidWindow;
    private long appliedCount, releaseCount, consentVersion;
    private string? lastReleaseError;

    public MacInputTarget(Func<bool> ownWindowActive, Func<MacInputBounds> getBounds)
        : this(ownWindowActive, getBounds, new QuartzEventPlatform()) { }

    internal MacInputTarget(Func<bool> ownWindowActive, Func<MacInputBounds> getBounds, IMacEventPlatform platform)
    {
        this.ownWindowActive = ownWindowActive;
        this.getBounds = getBounds;
        this.platform = platform;
    }

    public bool Armed { get { lock (gate) return armed; } }
    public long ConsentVersion { get { lock (gate) return consentVersion; } }
    public bool IsReady { get { lock (gate) return ReadinessFailure(getBounds()).Length == 0; } }
    public string UnavailableReason { get { lock (gate) return ReadinessFailure(getBounds()); } }
    public long AppliedCount => Interlocked.Read(ref appliedCount);
    public long ReleaseCount => Interlocked.Read(ref releaseCount);
    public string? LastReleaseError { get { lock (gate) return lastReleaseError; } }
    public MacPointerEvidence? LastPointerEvidence
    {
        get { lock (gate) return (platform as QuartzEventPlatform)?.LastPointerEvidence; }
    }
    public MacPointerEvidence[] PointerEvidence
    {
        get { lock (gate) return (platform as QuartzEventPlatform)?.PointerEvidence ?? []; }
    }

    public void SetArmed(bool value)
    {
        lock (gate)
        {
            if (disposed)
            {
                if (value) throw new ObjectDisposedException(nameof(MacInputTarget));
                return;
            }
            if (!value)
            {
                UpdateArmed(false);
                ReleaseCore();
                return;
            }
            if (armed) return;
            var bounds = getBounds();
            if (!bounds.IsValid) return;
            x = bounds.X + bounds.Width / 2;
            y = bounds.Y + bounds.Height / 2;
            armedWindowNumber = bounds.Window!.Number;
            lastValidWindow = bounds.Window;
            UpdateArmed(true);
        }
    }

    public void Apply(InputPacket packet, long expectedConsentVersion)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (consentVersion != expectedConsentVersion)
                throw new InvalidOperationException("本地允许状态已改变，请重新开始测试。");
            // Monitor is reentrant: the version check and existing readiness,
            // state bookkeeping and native post all remain under the same gate.
            Apply(packet);
        }
    }

    public void Apply(InputPacket packet)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var bounds = getBounds();
            var failure = ReadinessFailure(bounds);
            if (failure.Length != 0)
            {
                UpdateArmed(false);
                ReleaseCore();
                throw new InvalidOperationException(failure);
            }
            try
            {
                var action = state.Apply(packet);
                if (action is null) return;
                if (action.Kind == InputKind.MoveRelative) { x += action.X; y += action.Y; }
                x = Math.Clamp(x, bounds.X, bounds.X + bounds.Width - 0.5);
                y = Math.Clamp(y, bounds.Y, bounds.Y + bounds.Height - 0.5);
                lastValidWindow = bounds.Window!;
                platform.Post(action, x, y, lastValidWindow);
                Interlocked.Increment(ref appliedCount);
            }
            catch
            {
                UpdateArmed(false);
                ReleaseCore();
                throw;
            }
        }
    }

    public void ReleaseAll() { lock (gate) ReleaseCore(); }

    private void UpdateArmed(bool value)
    {
        if (armed == value) return;
        armed = value;
        consentVersion++;
    }

    private string ReadinessFailure(MacInputBounds bounds)
    {
        if (disposed) return "实验已关闭。";
        if (!armed) return "尚未允许输入：请点「允许本窗测试」。";
        if (!platform.AccessibilityAllowed) return "本实验程序尚未获得辅助功能权限。";
        if (!ownWindowActive() || !platform.OwnProcessFrontmost) return "请保持本实验窗口在最前方，再允许测试。";
        if (!bounds.IsValid) return "鼠标实验区或原生窗口尚未就绪。";
        if (bounds.Window!.Number != armedWindowNumber) return "实验窗口已改变，请重新允许输入。";
        return "";
    }

    private void ReleaseCore()
    {
        var releases = state.ReleaseAll();
        Interlocked.Add(ref releaseCount, releases.Count);
        if (releases.Count == 0) return;
        // Even after focus is lost, releases go to our own PID, never the newly
        // active application. Permission loss still clears all local state.
        if (!platform.AccessibilityAllowed) { lastReleaseError = "辅助功能权限已失效；本地按下状态已清除。"; return; }
        if (lastValidWindow is not { IsValid: true } window)
        {
            lastReleaseError = "原生实验窗口已失效；本地按下状态已清除。";
            return;
        }
        foreach (var release in releases)
        {
            try { platform.Post(release, x, y, window); }
            catch (Exception error) { lastReleaseError = $"本窗释放输入失败：{error.Message}"; }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            UpdateArmed(false);
            ReleaseCore();
            (platform as IDisposable)?.Dispose();
            disposed = true;
        }
    }
}
