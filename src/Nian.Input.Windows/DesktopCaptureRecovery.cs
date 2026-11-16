namespace Nian.Input.Windows;

/// <summary>Native recovery is attempted independently for every resource, including partial installs.</summary>
internal sealed class DesktopCaptureRecovery(Action releaseKeyboard, Action releaseMouse, Action releaseCursor)
{
    private readonly object gate = new();
    private bool restored;
    private bool failed;

    public bool Restore()
    {
        // Concurrent Stop must not return before another thread has finished cursor restoration.
        // Only native cleanup actions run here; no enqueue, network, UI or user notification.
        lock (gate)
        {
            if (restored) return !failed;
            Attempt(releaseKeyboard);
            Attempt(releaseMouse);
            Attempt(releaseCursor);
            restored = true;
            return !failed;
        }
    }

    private void Attempt(Action release)
    {
        try { release(); }
        catch { failed = true; }
    }
}
