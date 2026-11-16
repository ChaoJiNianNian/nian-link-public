using DesktopToolbox.DeskLink;
using DesktopToolbox.Windows.DeskSwitch;

namespace DesktopToolbox.App.DeskLink;

/// <summary>Only the machine receiving input can offer a return for this exact session.</summary>
public static class TargetReturnButtonPolicy
{
    public static string? SessionId(IntegratedDeskSwitchStatus? status, string localName)
    {
        var state = status?.Session;
        var preview = state?.InputPreview;
        if (string.IsNullOrWhiteSpace(localName) || state is null ||
            state.ProcessState != DeskLinkProcessState.Running || !state.NativeControlAvailable ||
            preview is not { Id.Length: 32, Phase: DeskflowPreviewPhase.Active or DeskflowPreviewPhase.Recovery } ||
            !Guid.TryParseExact(preview.Id, "N", out _) ||
            !string.Equals(preview.TargetComputer, localName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(preview.ViewerComputer, localName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(state.ActiveComputer, localName, StringComparison.OrdinalIgnoreCase) ||
            !state.ConnectedComputers.Any(name => string.Equals(name, preview.ViewerComputer, StringComparison.OrdinalIgnoreCase)))
            return null;
        return preview.Id;
    }
}
