using DesktopToolbox.DeskLink.Collaboration;

namespace DesktopToolbox.DeskLink.Coordination;

public sealed record MultiPeerDisplayCommand(Guid Id, string TargetDevice, long KeyboardRevision, MultiPeerDisplayRoute Route);

/// <summary>Tracks confirmed visible video separately from keyboard focus. Does not issue hardware commands.</summary>
public sealed class MultiPeerDisplayState
{
    private readonly object gate = new();
    private readonly string host;
    private string? visible;
    private long lastRevision = -1;
    private MultiPeerDisplayCommand? pending;

    public MultiPeerDisplayState(string host)
    {
        DeskflowProfile.ValidateName(host);
        this.host = host;
    }

    public string? VisibleDevice { get { lock (gate) return visible; } }

    // Only call after an explicit user confirmation or a trustworthy independent observation.
    public void ConfirmVisible(string device)
    {
        DeskflowProfile.ValidateName(device);
        lock (gate)
        {
            if (pending is not null) throw new InvalidOperationException("显示器切换仍在进行，无法确认画面位置。");
            visible = device;
        }
    }

    public MultiPeerDisplayCommand Begin(string target, string confirmedKeyboardTarget, long revision,
        IReadOnlyDictionary<string, MultiPeerMonitorSettings> settings)
    {
        DeskflowProfile.ValidateName(target);
        lock (gate)
        {
            if (pending is not null) throw new InvalidOperationException("上一次显示器切换尚未结束。");
            if (visible is null) throw new InvalidOperationException("请先确认显示器当前画面所在设备。");
            if (target != confirmedKeyboardTarget || revision < 0 || revision < lastRevision)
                throw new InvalidOperationException("键鼠状态未确认或已经过期。");
            var route = MultiPeerDisplayRoutePlanner.Plan(host, visible, target, settings)
                ?? throw new InvalidOperationException("显示器接口配置不完整或画面已经位于目标设备。");
            lastRevision = revision;
            visible = null;
            return pending = new(Guid.NewGuid(), target, revision, route);
        }
    }

    // Call only after the remote executor has finished or the command's execution window
    // has expired. A transport timeout alone must leave the command pending.
    public void Complete(Guid id, string status)
    {
        lock (gate)
        {
            if (pending is null || pending.Id != id) throw new InvalidOperationException("显示器切换结果已过期。");
            visible = status == "verified" ? pending.TargetDevice : null;
            pending = null;
        }
    }
}
