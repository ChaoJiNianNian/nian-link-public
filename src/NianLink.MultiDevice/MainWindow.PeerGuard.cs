using System.Threading;
using System.Threading.Tasks;
using DesktopToolbox.DeskLink;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    private void PresentPeerStoppedStatus() => PeerStatus.Text = PeerControlUnresolved
        ? "连接：未连接\n键鼠归还尚未确认，请检查键鼠所在设备。"
        : "连接：未连接";
#if DISCOVERY_PORTABLE
    // This lives with the candidate window, not the replaceable connection or preview.
    private readonly PeerControlOwner peerControlOwner = new();
#if NIAN_PEER_EMERGENCY_RESEARCH
    private PeerEmergencyWindow? peerEmergencyWindow;
#endif
    private bool PeerControlUnresolved => peerControlOwner.HasUnresolvedOperation;
    private bool PeerControlUnconfirmed => peerControlOwner.IsUncertain;
    private string PeerMiniControlHint => PeerControlUnconfirmed ? PeerControlOwner.UnconfirmedMessage : "请先连接主控，并在两端开启画面共享。";
    private void BindPeerControl(WindowsPeerLink current) => peerControlOwner.Bind(current);
    private void StopPeerControl(WindowsPeerLink current) => peerControlOwner.BeginStopping(current);
    private void RetirePeerControl(WindowsPeerLink current) => peerControlOwner.Retire(current);
#if NIAN_PEER_EMERGENCY_RESEARCH
    private void EnsurePeerEmergencyWindow(Func<Task> action)
    {
        Dispatcher.VerifyAccess();
        if (peerEmergencyWindow is not null) return;
        var window = new PeerEmergencyWindow(action);
        window.Closed += (_, _) => { if (ReferenceEquals(peerEmergencyWindow, window)) peerEmergencyWindow = null; };
        window.Show();
        peerEmergencyWindow = window;
    }
    private async Task ReturnPeerEmergencyAsync()
    {
        var current = peer ?? throw new InvalidOperationException("连接已停止，无法发送归还；请检查键鼠所在设备。");
        var invitation = peerStore?.Get() ?? throw new InvalidOperationException("当前没有已确认的主控连接。");
        await AttemptPeerEmergencyAsync(token => SwitchPeerAsync(current, invitation, invitation.HostName, token), TimeSpan.FromSeconds(3));
    }
    private static async Task AttemptPeerEmergencyAsync(Func<CancellationToken, Task<DeskLinkSwitchResult>> action, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        DeskLinkSwitchResult result;
        try { result = await action(deadline.Token).WaitAsync(deadline.Token); }
        catch (OperationCanceledException) { throw new InvalidOperationException("归还未确认，请检查键鼠所在设备；之前的请求可能仍在处理。"); }
        if (!result.InputTransferred) throw new InvalidOperationException("键鼠归还尚未确认，请检查键鼠所在设备。");
    }
    private void PresentPeerEmergencyStopped()
    {
        if (PeerControlUnresolved) peerEmergencyWindow?.ShowConnectionState(false, true);
        else { var window = peerEmergencyWindow; peerEmergencyWindow = null; window?.Release(); }
    }
#endif
    private GuardedPeerControl GuardPeer(WindowsPeerLink current, MultiPeerInvitation invitation) =>
        new(peerControlOwner, current, current.SwitchDetailedAsync, invitation.HostName, invitation.PeerName);
    private IDeskflowPreviewControl PreviewPeer(WindowsPeerLink current, MultiPeerInvitation invitation) => GuardPeer(current, invitation);
    private Task<DeskLinkSwitchResult> SwitchPeerAsync(WindowsPeerLink current, MultiPeerInvitation invitation, string target, CancellationToken token = default) =>
        GuardPeer(current, invitation).SwitchDetailedAsync(target, token);
#else
    private bool PeerControlUnresolved => false;
    private bool PeerControlUnconfirmed => false;
    private string PeerMiniControlHint => "请先连接主控，并在两端开启画面共享。";
    private void BindPeerControl(WindowsPeerLink current) { }
    private void StopPeerControl(WindowsPeerLink current) { }
    private void RetirePeerControl(WindowsPeerLink current) { }
    private IDeskflowPreviewControl PreviewPeer(WindowsPeerLink current, MultiPeerInvitation invitation) => current;
    private Task<DeskLinkSwitchResult> SwitchPeerAsync(WindowsPeerLink current, MultiPeerInvitation invitation, string target, CancellationToken token = default) =>
        current.SwitchDetailedAsync(target, token);
#endif
}
