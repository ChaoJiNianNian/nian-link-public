using DesktopToolbox.DeskLink;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>Keeps daily Deskflow operations authoritative while preview control remains a separate permission.</summary>
public sealed class PreviewAwareDeskSwitchService(IIntegratedDeskSwitchService inner,Action<string> stopControl,Action invalidate) : IIntegratedDeskSwitchService
{
    public bool IsRunning=>inner.IsRunning;
    public IntegratedDeskSwitchStatus Snapshot=>inner.Snapshot;
    private void BeforeMutation(string reason){stopControl(reason);invalidate();}
    public Task<IntegratedDeskSwitchStatus> StartAsync(DeskSwitchSettings settings,CancellationToken token){BeforeMutation("daily-sharing-start");return inner.StartAsync(settings,token);}
    public Task<IntegratedDeskSwitchStatus> StopAsync(CancellationToken token){BeforeMutation("daily-sharing-stop");return inner.StopAsync(token);}
    public Task<DeskLinkSwitchResult> SwitchAsync(string target,CancellationToken token){BeforeMutation("daily-focus-switch");return inner.SwitchAsync(target,token);}
    public Task ConfirmPeerAsync(string fingerprint,CancellationToken token){BeforeMutation("daily-pairing-changed");return inner.ConfirmPeerAsync(fingerprint,token);}
    public Task RejectPeerAsync(CancellationToken token){BeforeMutation("daily-pairing-rejected");return inner.RejectPeerAsync(token);}
    public Task<IntegratedDeskSwitchStatus> RefreshAsync(CancellationToken token)=>inner.RefreshAsync(token);
    public Task<string> ExportPairingAsync(CancellationToken token)=>inner.ExportPairingAsync(token);
    public ValueTask DisposeAsync(){BeforeMutation("daily-sharing-disposed");return inner.DisposeAsync();}
}
