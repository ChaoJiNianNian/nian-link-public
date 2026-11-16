namespace DesktopToolbox.DeskLink.Coordination;

public sealed record MultiScreenPendingSwitch(Guid Id, string TargetDevice, long KeyboardRevision, MultiScreenSwitchPlan Plan);

/// <summary>Tracks confirmed screen sources independently and serializes physical display operations.</summary>
public sealed class MultiScreenDisplayState
{
    private readonly object gate = new();
    private readonly MultiScreenTopology topology;
    private readonly Dictionary<string, string?> visible = new(StringComparer.OrdinalIgnoreCase);
    private MultiScreenPendingSwitch? pending;
    private long lastRevision = -1;
    internal MultiScreenTopology Topology => topology;
    public MultiScreenPendingSwitch? Pending { get { lock (gate) return pending; } }

    public MultiScreenDisplayState(MultiScreenTopology topology)
    {
        this.topology = topology ?? throw new ArgumentNullException(nameof(topology));
        foreach (var screen in topology.Screens) visible.Add(screen.ScreenId, null);
    }

    public string? VisibleDevice(string screenId)
    {
        lock (gate) return visible.TryGetValue(screenId, out var value) ? value : throw new ArgumentException("屏幕不存在。", nameof(screenId));
    }

    public void ConfirmVisible(string screenId, string device)
    {
        lock (gate)
        {
            var screen = FindScreen(screenId);
            if (pending is not null && string.Equals(pending.Plan.ScreenId, screen.ScreenId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("该屏幕的切换尚未结束。");
            var attachment = screen.Attachments.SingleOrDefault(item => string.Equals(item.DeviceName, device, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("该电脑未连接此屏幕。", nameof(device));
            visible[screen.ScreenId] = attachment.DeviceName;
        }
    }

    public MultiScreenPendingSwitch? Begin(string screenId, string targetDevice, string confirmedKeyboardTarget, long revision)
    {
        lock (gate)
        {
            var screen = FindScreen(screenId);
            if (pending is not null) throw new InvalidOperationException("上一条屏幕切换尚未确认结束。");
            if (!string.Equals(targetDevice, confirmedKeyboardTarget, StringComparison.OrdinalIgnoreCase) || revision < 0 || revision < lastRevision)
                throw new InvalidOperationException("键鼠目标未确认或状态已过期。");
            var attachment = screen.Attachments.SingleOrDefault(item => string.Equals(item.DeviceName, targetDevice, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("目标电脑未连接这块屏幕。");
            var plan = topology.Plan(screen.ScreenId, visible[screen.ScreenId], attachment.DeviceName);
            lastRevision = revision;
            if (plan is null) return null;
            visible[screen.ScreenId] = null;
            return pending = new(Guid.NewGuid(), attachment.DeviceName, revision, plan);
        }
    }

    // Invoke only after the executing device has confirmed that its driver stopped.
    public void Complete(Guid id, string status)
    {
        lock (gate)
        {
            if (pending is null || pending.Id != id) throw new InvalidOperationException("屏幕切换结果已过期。");
            visible[pending.Plan.ScreenId] = status == "verified" ? pending.TargetDevice : null;
            pending = null;
        }
    }

    internal void CancelBeforeDispatch(Guid id)
    {
        lock (gate)
        {
            if (pending is null || pending.Id != id) throw new InvalidOperationException("屏幕切换状态已变化。");
            visible[pending.Plan.ScreenId] = pending.Plan.SourceDevice;
            pending = null;
        }
    }

    private MultiScreenDefinition FindScreen(string screenId) => topology.Screens.SingleOrDefault(screen => string.Equals(screen.ScreenId, screenId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("屏幕不存在。", nameof(screenId));
}
