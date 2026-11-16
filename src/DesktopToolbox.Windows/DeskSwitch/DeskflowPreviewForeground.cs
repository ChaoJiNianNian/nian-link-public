using DesktopToolbox.DeskLink;

namespace DesktopToolbox.Windows.DeskSwitch;

public interface IDeskflowPreviewForegroundState
{
    bool PreviewInputOwnsForeground { get; }
}

public static class DeskflowPreviewForegroundPolicy
{
    public static bool IsTrusted(DeskLinkState state, string viewer, int ownedPid, bool ownedProcessAlive,
        uint foregroundPid, string windowClass) => ownedProcessAlive && ownedPid > 0 &&
        state.ProcessId == ownedPid && foregroundPid == (uint)ownedPid && windowClass == "DeskflowDesk" &&
        state.ProcessState == DeskLinkProcessState.Running && state.NativeControlAvailable &&
        state.InputPreview is { Id.Length: 32, Phase: DeskflowPreviewPhase.Preparing or DeskflowPreviewPhase.Active } preview &&
        string.Equals(preview.ViewerComputer, viewer, StringComparison.Ordinal);
}
