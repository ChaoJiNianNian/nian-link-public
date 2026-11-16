namespace DesktopToolbox.Mac.Models;

public enum MacAwdlState { Unknown, Unavailable, Enabled, Disabled }

public sealed record MacWirelessSnapshot(MacAwdlState State, string Message = "", string Error = "", bool? KeepEnabled = null)
{
    public bool IsSmoothEnabled => KeepEnabled ?? State == MacAwdlState.Disabled;
    public bool CanChange => KeepEnabled == true || State is MacAwdlState.Enabled or MacAwdlState.Disabled;
}
