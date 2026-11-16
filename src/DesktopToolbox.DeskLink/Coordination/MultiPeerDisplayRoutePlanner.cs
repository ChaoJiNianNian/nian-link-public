using DesktopToolbox.DeskLink.Collaboration;

namespace DesktopToolbox.DeskLink.Coordination;

public sealed record MultiPeerDisplayRoute(string SourceDevice, string MonitorId, uint TargetInput);

/// <summary>Plans a monitor command on the video path currently visible, without issuing it.</summary>
public static class MultiPeerDisplayRoutePlanner
{
    public static MultiPeerDisplayRoute? Plan(string host, string from, string to,
        IReadOnlyDictionary<string, MultiPeerMonitorSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || from == to) return null;
        try { _ = MultiPeerMonitorGroup.Create(host, settings); }
        catch (ArgumentException) { return null; }
        var source = from == host ? null : settings.GetValueOrDefault(from);
        var target = to == host ? null : settings.GetValueOrDefault(to);
        if (from != host && !Valid(source) || to != host && !Valid(target)) return null;
        if (from == host) return new(from, target!.MonitorId, target.PeerInput);
        if (to == host) return new(from, source!.MonitorId, source.HostInput);
        if (source!.MonitorId != target!.MonitorId || source.HostInput != target.HostInput || source.PeerInput == target.PeerInput) return null;
        return new(from, source.MonitorId, target.PeerInput);
    }

    private static bool Valid(MultiPeerMonitorSettings? value) => value is not null && !string.IsNullOrWhiteSpace(value.MonitorId) &&
        value.MonitorId.Length <= 256 && !value.MonitorId.Any(char.IsControl) && value.HostInput is > 0 and <= 255 &&
        value.PeerInput is > 0 and <= 255 && value.HostInput != value.PeerInput;
}
