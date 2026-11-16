namespace DesktopToolbox.Windows.DeskSwitch.Collaboration;

/// <summary>Arms only new local copies and suppresses the sequence written by a remote paste.</summary>
public sealed class ClipboardBridgeCursor(string origin)
{
    private bool armed;
    private uint sequence;
    private string? appliedId;
    private uint? retrySequence;

    public bool ShouldPublish(uint currentSequence, bool enabled)
    {
        if (!enabled) { armed = false; retrySequence = null; sequence = currentSequence; return false; }
        if (!armed) { armed = true; sequence = currentSequence; return false; }
        if (retrySequence == currentSequence) { retrySequence = null; return true; }
        retrySequence = null;
        if (sequence == currentSequence) return false;
        sequence = currentSequence;
        return true;
    }
    public bool ShouldApply(string id, string source) => !string.IsNullOrEmpty(id) && source != origin && id != appliedId;
    // Ignore a legacy peer's image without changing the local sequence baseline.
    public void Ignore(string id) => appliedId = id;
    public void RetryPublish(uint currentSequence) => retrySequence = currentSequence;
    public void Applied(string id, uint currentSequence) { appliedId = id; sequence = currentSequence; armed = true; retrySequence = null; }
}
