using DesktopToolbox.DeskLink;
namespace DesktopToolbox.Windows.DeskSwitch;

public enum DeskSwitchBackend { Deskflow, Native }

public sealed record IntegratedDeskSwitchStatus
{
    public DeskSwitchBackend Backend { get; init; } = DeskSwitchBackend.Deskflow;
    public bool Available { get; init; }
    public DeskLinkState Session { get; init; } = new();
    public DeskLinkSwitchResult? LastSwitch { get; init; }
    public string? ControlAddress { get; init; }
    public string? ControlFingerprint { get; init; }
    public int? ControlPort { get; init; }
    public string? LastError { get; init; }
}
public interface IIntegratedDeskSwitchService : IAsyncDisposable
{
    bool IsRunning { get; }
    IntegratedDeskSwitchStatus Snapshot { get; }
    Task<IntegratedDeskSwitchStatus> StartAsync(DeskSwitchSettings settings, CancellationToken token);
    Task<IntegratedDeskSwitchStatus> StopAsync(CancellationToken token);
    Task<IntegratedDeskSwitchStatus> RefreshAsync(CancellationToken token);
    Task<DeskLinkSwitchResult> SwitchAsync(string target, CancellationToken token);
    Task ConfirmPeerAsync(string fingerprint, CancellationToken token);
    Task RejectPeerAsync(CancellationToken token);
    Task<string> ExportPairingAsync(CancellationToken token);
}
