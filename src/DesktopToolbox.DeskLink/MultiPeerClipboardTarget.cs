namespace DesktopToolbox.DeskLink;

/// <summary>Choose one connected destination; never broadcast clipboard content.</summary>
public static class MultiPeerClipboardTarget
{
    public static string? ReconcileSelection(string? setupSelection, bool miniOpen, string? miniSelection) =>
        miniOpen && !string.Equals(setupSelection, miniSelection, StringComparison.Ordinal) ? null : setupSelection;

    public static string? Choose(string host, string? active, string? selected, IReadOnlyList<string> connected)
    {
        DeskflowProfile.ValidateName(host);
        ArgumentNullException.ThrowIfNull(connected);
        if (active is null) return null;
        if (!string.Equals(active, host, StringComparison.Ordinal) && !string.Equals(active, selected, StringComparison.Ordinal)) return null;
        var target = selected;
        if (target is null || string.Equals(target, host, StringComparison.Ordinal)) return null;
        return connected.Contains(target, StringComparer.Ordinal) ? target : null;
    }
}
