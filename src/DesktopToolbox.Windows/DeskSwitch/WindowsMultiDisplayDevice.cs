using System.Text.Json;
using DesktopToolbox.DeskLink.Coordination;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>Maps the multi-device executor to a separately owned Windows display helper.</summary>
public sealed class WindowsMultiDisplayDevice : IMultiPeerDisplayDevice
{
    private readonly IDisplayBridge bridge;
    private readonly Func<string, uint, CancellationToken, Task<string>> prepareConfiguration;
    private readonly Func<bool> visiblePathConfirmed;
    private readonly string approvedMonitor, localMonitor;
    private readonly object sync = new();
    private (string Monitor, uint Input)? observed;

    public WindowsMultiDisplayDevice(SafeDisplayBridgeProcess bridge, string approvedMonitor, string localMonitor,
        Func<string, uint, CancellationToken, Task<string>> prepareConfiguration, Func<bool> visiblePathConfirmed)
        : this((IDisplayBridge)bridge, approvedMonitor, localMonitor, prepareConfiguration, visiblePathConfirmed) { }

    internal WindowsMultiDisplayDevice(IDisplayBridge bridge, string approvedMonitor, string localMonitor,
        Func<string, uint, CancellationToken, Task<string>> prepareConfiguration, Func<bool> visiblePathConfirmed)
    {
        this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        if (string.IsNullOrWhiteSpace(approvedMonitor) || string.IsNullOrWhiteSpace(localMonitor) || approvedMonitor.Length > 256 || localMonitor.Length > 256 ||
            approvedMonitor.Any(char.IsControl) || localMonitor.Any(char.IsControl)) throw new ArgumentException("显示器身份无效。");
        this.approvedMonitor = approvedMonitor; this.localMonitor = localMonitor;
        this.prepareConfiguration = prepareConfiguration ?? throw new ArgumentNullException(nameof(prepareConfiguration));
        this.visiblePathConfirmed = visiblePathConfirmed ?? throw new ArgumentNullException(nameof(visiblePathConfirmed));
    }

    public async Task<DisplayInputObservation> ReadAsync(string monitor, CancellationToken ct)
    {
        lock (sync) observed = null;
        if (monitor != approvedMonitor || !visiblePathConfirmed()) return new(null, false);
        var matches = (await bridge.ListAsync(ct)).Where(item => string.Equals(item.Id, localMonitor, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || !matches[0].CanReadInput || matches[0].CurrentInput is not { } current || !visiblePathConfirmed())
            return new(null, false);
        lock (sync) observed = (monitor, current);
        return new(current, true);
    }

    public async Task<DisplaySwitchOutcome> WriteAsync(string monitor, uint input, CancellationToken ct)
    {
        (string Monitor, uint Input)? prior;
        lock (sync) { prior = observed; observed = null; }
        if (!visiblePathConfirmed() || prior is null || prior.Value.Monitor != monitor || prior.Value.Input == input)
            throw new InvalidOperationException("显示器当前输入未确认。");
        var path = await prepareConfiguration(localMonitor, input, ct);
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) != "desk-switch.json") throw new InvalidOperationException("显示器配置路径无效。");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 16384) throw new InvalidOperationException("显示器配置不可用。");
        DeskSwitchSettings settings;
        try { settings = JsonSerializer.Deserialize<DeskSwitchSettings>(await File.ReadAllTextAsync(path, ct), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("显示器配置为空。"); }
        catch (JsonException error) { throw new InvalidOperationException("显示器配置损坏。", error); }
        settings.Validate();
        if (!settings.LinkDisplay || settings.MonitorId != localMonitor || settings.LocalInput != prior.Value.Input || settings.RemoteInput != input || !visiblePathConfirmed())
            throw new InvalidOperationException("显示器配置与已确认的输入不一致。");
        await bridge.ApplyAsync(path, "remote", false, ct);
        return new("acceptedUnverified", "显示器已接受命令，请确认实际画面。");
    }
}
