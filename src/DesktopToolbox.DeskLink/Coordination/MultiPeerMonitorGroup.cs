using System.Collections.ObjectModel;
using DesktopToolbox.DeskLink.Collaboration;

namespace DesktopToolbox.DeskLink.Coordination;

/// <summary>A validated single-monitor mapping; unmapped devices remain usable without display linking.</summary>
public sealed class MultiPeerMonitorGroup
{
    public string MonitorId { get; }
    public IReadOnlyDictionary<string, uint> Inputs { get; }
    private MultiPeerMonitorGroup(string monitorId, IDictionary<string, uint> inputs)
    { MonitorId = monitorId; Inputs = new ReadOnlyDictionary<string, uint>(inputs); }

    public static MultiPeerMonitorGroup Create(string host, IReadOnlyDictionary<string, MultiPeerMonitorSettings> mappings)
    {
        DeskflowProfile.ValidateName(host);
        ArgumentNullException.ThrowIfNull(mappings);
        if (mappings.Count is < 1 or > 4) throw new ArgumentException("请选择一至四台需要切屏的设备。", nameof(mappings));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { host };
        var inputs = new Dictionary<string, uint>(StringComparer.Ordinal);
        var ports = new HashSet<uint>();
        string? monitor = null; uint? hostInput = null;
        foreach (var (name, settings) in mappings)
        {
            DeskflowProfile.ValidateName(name);
            if (!names.Add(name) || settings is null || string.IsNullOrWhiteSpace(settings.MonitorId) ||
                settings.MonitorId.Length > 256 || settings.MonitorId.Any(char.IsControl) ||
                settings.HostInput is 0 or > 255 || settings.PeerInput is 0 or > 255 || settings.HostInput == settings.PeerInput)
                throw new ArgumentException("设备或显示器接口配置无效。", nameof(mappings));
            if (monitor is null)
            {
                monitor = settings.MonitorId; hostInput = settings.HostInput;
                inputs.Add(host, settings.HostInput); ports.Add(settings.HostInput);
            }
            if (monitor != settings.MonitorId || hostInput != settings.HostInput || !ports.Add(settings.PeerInput))
                throw new ArgumentException("显示器或输入接口不一致，请重新选择。", nameof(mappings));
            inputs.Add(name, settings.PeerInput);
        }
        return new(monitor!, inputs);
    }
}
