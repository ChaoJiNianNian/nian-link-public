namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>Proposes a local display match; the user must still confirm it is the same physical display.</summary>
public static class WindowsDisplayIdentityResolver
{
    public static string? FindCandidate(string approvedMonitor, IReadOnlyList<DisplayMonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var approved = Identity(approvedMonitor);
        if (approved is null) return null;
        var matches = monitors.Where(monitor => monitor is not null && Identity(monitor.Id) is { } current &&
            current.VendorId == approved.VendorId && current.ProductId == approved.ProductId).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }

    private static DesktopToolbox.DeskLink.DeskLinkDisplayBinding? Identity(string id)
    {
        try { return DeskLinkDisplayBindingFactory.FromSettings(new DeskSwitchSettings { MonitorId = id, LocalInput = 15, RemoteInput = 16, LinkDisplay = true }); }
        catch (ArgumentException) { return null; }
    }
}
