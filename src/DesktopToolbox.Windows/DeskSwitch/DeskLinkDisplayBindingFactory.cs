using System.Globalization;
using DesktopToolbox.DeskLink;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>Transfers the saved Windows display identity, never a guessed default display.</summary>
public static class DeskLinkDisplayBindingFactory
{
    public static DeskLinkDisplayBinding? FromSettings(DeskSwitchSettings settings)
    {
        settings.Validate();
        if (!settings.LinkDisplay) return null;
        const string prefix = @"\\?\DISPLAY#";
        if (!settings.MonitorId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var name = settings.MonitorId[prefix.Length..].Split('#', 2)[0].ToUpperInvariant();
        if (name.Length != 7 || name.Take(3).Any(value => value is < 'A' or > 'Z') ||
            !uint.TryParse(name.AsSpan(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var product) || product == 0)
            return null;
        var vendor = (uint)(((name[0]-'A'+1)<<10) | ((name[1]-'A'+1)<<5) | (name[2]-'A'+1));
        return new() { Enabled = true, VendorId = vendor, ProductId = product,
            WindowsInput = settings.LocalInput, MacInput = settings.RemoteInput };
    }
}