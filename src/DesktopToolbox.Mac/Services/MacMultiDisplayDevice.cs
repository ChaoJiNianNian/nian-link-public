using DesktopToolbox.DeskLink.Coordination;
using DesktopToolbox.Mac.Services.Display;

namespace DesktopToolbox.Mac.Services;

/// <summary>Resolves an approved cross-platform display to one unambiguous local Mac display.</summary>
public sealed class MacMultiDisplayDevice : IMultiPeerDisplayDevice
{
    private readonly IMacDisplayControl bridge;
    private readonly string monitor;
    private readonly MacDisplayIdentity identity;
    private readonly HashSet<uint> approvedInputs;
    private readonly Func<bool> visiblePathConfirmed;
    private readonly object sync = new();
    private uint? observed;

    public MacMultiDisplayDevice(MacDisplayBridgeProcess bridge, string monitor, MacDisplayIdentity identity,
        IEnumerable<uint> approvedInputs, Func<bool> visiblePathConfirmed)
        : this((IMacDisplayControl)bridge, monitor, identity, approvedInputs, visiblePathConfirmed) { }

    internal MacMultiDisplayDevice(IMacDisplayControl bridge, string monitor, MacDisplayIdentity identity,
        IEnumerable<uint> approvedInputs, Func<bool> visiblePathConfirmed)
    {
        this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        if (string.IsNullOrWhiteSpace(monitor) || monitor.Length > 256 || monitor.Any(char.IsControl) ||
            identity.VendorId is 0 or > ushort.MaxValue || identity.ProductId is 0 or > ushort.MaxValue)
            throw new ArgumentException("显示器身份无效。", nameof(monitor));
        ArgumentNullException.ThrowIfNull(approvedInputs);
        this.approvedInputs = approvedInputs.ToHashSet();
        if (this.approvedInputs.Count < 2 || this.approvedInputs.Count > 16 || this.approvedInputs.Any(input => input is 0 or > 255))
            throw new ArgumentException("显示器接口配置无效。", nameof(approvedInputs));
        this.monitor = monitor; this.identity = identity;
        this.visiblePathConfirmed = visiblePathConfirmed ?? throw new ArgumentNullException(nameof(visiblePathConfirmed));
    }

    public async Task<DisplayInputObservation> ReadAsync(string requestedMonitor, CancellationToken ct)
    {
        lock (sync) observed = null;
        if (requestedMonitor != monitor || !visiblePathConfirmed() || !await HasUniqueDisplayAsync(ct)) return new(null, false);
        var result = await bridge.ReadInputAsync(identity, ct);
        if (!result.Ok || result.CurrentInput is not { } input || !approvedInputs.Contains(input) || !visiblePathConfirmed()) return new(null, false);
        lock (sync) observed = input;
        return new(input, true);
    }

    public async Task<DisplaySwitchOutcome> WriteAsync(string requestedMonitor, uint input, CancellationToken ct)
    {
        uint? prior; lock (sync) { prior = observed; observed = null; }
        if (requestedMonitor != monitor || prior is null || prior == input || !approvedInputs.Contains(input) || !visiblePathConfirmed() || !await HasUniqueDisplayAsync(ct))
            throw new InvalidOperationException("显示器当前输入或身份未确认。");
        if (!visiblePathConfirmed()) throw new InvalidOperationException("显示器当前画面已变化。");
        var result = await bridge.SetInputAsync(identity, checked((ushort)input), ct);
        return result.CommandAccepted ? new("acceptedUnverified", "显示器已接受命令，请确认实际画面。") : new("failed", "显示器未接受切换命令。");
    }

    private async Task<bool> HasUniqueDisplayAsync(CancellationToken ct) =>
        (await bridge.ListAsync(ct)).Count(item => item.Identity == identity) == 1;
}
