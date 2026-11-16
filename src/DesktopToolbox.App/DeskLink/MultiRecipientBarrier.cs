namespace DesktopToolbox.App.DeskLink;

/// <summary>Only the device acknowledged by the visible page can receive a native file drop.</summary>
public sealed class MultiRecipientBarrier
{
    private readonly object gate = new();
    private string? selected, acknowledged;
    private long version;

    public long Publish(string? name)
    {
        lock (gate)
        {
            if (!string.Equals(selected, name, StringComparison.Ordinal))
            { selected = name; acknowledged = null; checked { version++; } }
            return version;
        }
    }

    public bool Acknowledge(string name, long renderedVersion)
    {
        lock (gate)
        {
            if (selected is null || name != selected || renderedVersion != version) return false;
            acknowledged = name; return true;
        }
    }

    public bool TryGetRecipient(out string? name)
    {
        lock (gate) { name = acknowledged; return name is not null && name == selected; }
    }
}
