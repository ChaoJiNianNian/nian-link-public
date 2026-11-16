namespace DesktopToolbox.Windows.DeskSwitch.Collaboration;

// Classification inspects format metadata only, never image content or file paths.
internal enum ClipboardContentKind { None, Text }

internal static class ClipboardTextPolicy
{
    internal static ClipboardContentKind Classify(bool excluded, bool historyDisallowed, bool cloudDisallowed,
        bool fileDrop, bool hasImage, bool hasText)
    {
        if (excluded || historyDisallowed || cloudDisallowed || fileDrop || hasImage) return ClipboardContentKind.None;
        return hasText ? ClipboardContentKind.Text : ClipboardContentKind.None;
    }
}
