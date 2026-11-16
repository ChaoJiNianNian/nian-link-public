namespace DesktopToolbox.Mac.Models;

/// <summary>Keep configuration, recovery and trust decisions visible; busy controls alone do not expand the window.</summary>
public static class MacWindowPresentation
{
    public static string WindowTitle(bool nativeCandidate) => nativeCandidate ? "年年互联·自研测试" : "年年电脑切换";

    public static bool UseCollaboration(MacLinkSnapshot state, bool pairingPreviewPending) =>
        state.HasApprovedPairing && !pairingPreviewPending && state.PendingPeerFingerprint.Length == 0 &&
        !state.HasPendingDisplayCleanup && !state.HasDisplayRecoveryError;

    public static bool UseCompact(MacLinkSnapshot state, bool settingsRequested, bool pairingPreviewPending) =>
        !settingsRequested && !pairingPreviewPending &&
        state.IsRunning && state.IsConnected && state.ControlConnected &&
        !state.HasPendingDisplayCleanup && !state.HasDisplayRecoveryError && state.Error.Length == 0 && state.PendingPeerFingerprint.Length == 0;
}
