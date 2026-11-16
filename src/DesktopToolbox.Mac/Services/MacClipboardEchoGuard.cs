namespace DesktopToolbox.Mac.Services;

public enum MacClipboardAction { None, Publish, Clear }

/// <summary>Only a new native change is an event. Keep it pending until its network operation succeeds.</summary>
public sealed class MacClipboardEchoGuard
{
    private int? changeCount;
    private MacClipboardAction pending;
    public MacClipboardAction Observe(int count, string? text)
    {
        if (changeCount is null) { changeCount = count; return MacClipboardAction.None; }
        if (changeCount == count) return pending;
        changeCount = count;
        return pending = text is not null ? MacClipboardAction.Publish : MacClipboardAction.Clear;
    }
    public void Commit(int count) { if (changeCount == count) pending = MacClipboardAction.None; }
    public bool ShouldPublish(int count, string? text) { var action = Observe(count, text); Commit(count); return action == MacClipboardAction.Publish; }
    public void MarkRemoteApplied(int count) { changeCount = count; pending = MacClipboardAction.None; }
    public void Reset() { changeCount = null; pending = MacClipboardAction.None; }
}
