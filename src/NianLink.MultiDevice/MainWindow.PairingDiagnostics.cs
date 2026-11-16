#if FILELESS_PAIRING_CANDIDATE && FILELESS_PAIRING_DIAGNOSTICS
using System.Windows;
using System.Windows.Controls;
using NianLink.CandidateDiscovery;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    private TextBlock? peerPairingDiagnostics;

    private void InitializePairingDiagnostics()
    {
        if (PeerDiscoveryStatus.Parent is not Panel container) return;
        peerPairingDiagnostics = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8),
            Text = "测试诊断：开启被发现后显示本次会话线索。"
        };
        container.Children.Insert(container.Children.IndexOf(PeerDiscoveryStatus) + 1, peerPairingDiagnostics);
    }

    private void ResetPairingDiagnostics()
    {
        if (peerPairingDiagnostics is not null)
            peerPairingDiagnostics.Text = "测试诊断：本次会话尚未观察到单播数据。计数未认证，不能证明设备身份或网络送达情况。";
    }

    private void UpdatePairingDiagnostics(CandidateDiscoveryRun? run)
    {
        if (peerPairingDiagnostics is null || run is null || discoveryIsScan) return;
        var snapshot = run.GetPairingDiagnostics();
        var ui = incomingPromptOpen ? "界面正在处理请求。" : snapshot.Accepted > 0
            ? "曾受理请求，当前未见界面处理；请求可能已结束。" : "尚无受理请求。";
        static string Count(int value) => value >= 999 ? "至少 999" : value.ToString();
        peerPairingDiagnostics.Text = $"测试诊断：本次会话观察到单播数据 {Count(snapshot.Observed)} 条；校验未受理 {Count(snapshot.Rejected)} 条；受理 {Count(snapshot.Accepted)} 条。{ui}\n计数未认证，单播可能与配对无关，不能证明设备身份或网络送达情况。";
    }

    private void FinishPairingDiagnostics(CandidateDiscoveryRun? run, long stopEpoch)
    {
        if (stopEpoch == discoveryUiEpoch) UpdatePairingDiagnostics(run);
    }
}
#endif
