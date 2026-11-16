using System.Text.Json;

namespace DesktopToolbox.DeskLink;

/// <summary>Serializes public keyboard identities without exporting bearer tokens.</summary>
public static class NativePeerBindings
{
    public static string ToJson(IReadOnlyCollection<TrustedDeviceIdentity> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count > MultiDeviceCredentials.MaximumPeers) throw new ArgumentException("Too many devices.", nameof(devices));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var peers = new List<TrustedDeviceIdentity>(devices.Count);
        foreach (var device in devices)
        {
            if (device is null) throw new ArgumentException("A device is missing.", nameof(devices));
            DeskflowProfile.ValidateName(device.Name);
            var fingerprint = DeskLinkFingerprint.Normalize(device.Fingerprint);
            if (!names.Add(device.Name) || !fingerprints.Add(fingerprint))
                throw new ArgumentException("Device names and fingerprints must be unique.", nameof(devices));
            peers.Add(new(device.Name, fingerprint));
        }
        return JsonSerializer.Serialize(new { version = 1, peers = peers.Select(x => new { x.Name, x.Fingerprint }) }, JsonLineProtocol.Json);
    }
}
