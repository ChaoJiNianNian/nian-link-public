namespace DesktopToolbox.Mac.Models;

/// <summary>Report an early failure immediately, but let the desktop loop start before shutting it down.</summary>
public static class MacStartupFailure
{
    public static void Schedule(Exception failure, Action<string> writeError, Action<int> setExitCode,
        Action<Action> post, Action<int> shutdown)
    {
        setExitCode(2);
        writeError(failure.ToString());
        post(() => shutdown(2));
    }
}
