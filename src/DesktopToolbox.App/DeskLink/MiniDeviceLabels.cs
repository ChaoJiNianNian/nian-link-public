namespace DesktopToolbox.App.DeskLink;

/// <summary>Projects friendly labels without changing device identifiers or routing.</summary>
public static class MiniDeviceLabels
{
    public static IReadOnlyList<MiniDevice> Apply(IReadOnlyList<MiniDevice> devices, Func<string, string>? displayName)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (displayName is null) return devices;
        return devices.Select(device =>
        {
            var alias = displayName(device.Name);
            if (string.IsNullOrWhiteSpace(alias) || alias == device.Name) return device;
            var prefix = device.Label.EndsWith(device.Name, StringComparison.Ordinal)
                ? device.Label[..^device.Name.Length] : "";
            return device with { Label = prefix + alias };
        }).ToArray();
    }
}
