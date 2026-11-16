using System.Windows;
using DesktopToolbox.DeskLink;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    private bool pairingFileSaved;
    private bool peerPairingConfirmed;
    private DeskLinkState? peerKeyboardSnapshot;
    private static Visibility When(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshPairingGuide()
    {
        if (HostPairingStep is null || PeerPairingStep is null) return;
        bool needsHost = pairing is null;
        bool pendingSave = unsavedInvitation is not null;
        bool review = !needsHost && offer is not null && !pendingSave;
        bool complete = !needsHost && pairingFileSaved && !review && !pendingSave;
        bool import = !needsHost && !review && !pendingSave && !complete;
        HostPairingPrepare.Visibility = When(needsHost);
        HostPairingImport.Visibility = When(import);
        HostPairingConfirm.Visibility = HostPairingRetry.Visibility = When(review);
        HostPairingSave.Visibility = When(pendingSave);
        HostPairingDone.Visibility = HostPairingAgain.Visibility = When(complete);
        HostPairingStep.Text = needsHost ? "先准备主控" : pendingSave ? "保存配对文件" : review ? "核对设备指纹" : complete ? "配对文件已保存" : "导入对方的身份卡";
        HostPairingGuide.Text = needsHost ? "准备后，再添加设备。" : pendingSave ? "配对文件含凭证，请保存并私下发给对应设备。" : review ? "与另一台电脑核对完整指纹，一致后继续。" : complete ? "请私下发给对应设备并在那边导入。" : "在另一台电脑生成身份卡并发送到本机；身份卡 10 分钟有效。";
        bool peerReview = hostPreview is not null;
        bool peerDone = peerPairingConfirmed && !peerReview;
        PeerPairingImport.Visibility = When(!peerReview && !peerDone);
        PeerPairingConfirm.Visibility = PeerPairingRetry.Visibility = When(peerReview);
        PeerPairingDone.Visibility = PeerPairingAgain.Visibility = When(peerDone);
        PeerPairingStep.Text = peerReview ? "核对主控指纹" : peerDone ? "配对已确认" : "导入主控配对文件";
        PeerPairingGuide.Text = peerReview ? "与主控电脑核对完整指纹，一致后继续。" : peerDone ? "可以开始连接主控。" : "配对文件含凭证，请仅通过私密方式传送。";
        PeerStartButton.Visibility = When(peer is null);
        PeerStopButton.Visibility = When(peer is not null);
        bool connected = peer is not null && peerKeyboardSnapshot?.ConnectionState == DeskLinkConnectionState.Connected;
        PeerMiniButton.Visibility = PeerSwitchButton.Visibility = PeerReturnButton.Visibility = When(connected);
        PeerConfirmKeyboardButton.Visibility = When(peer is not null && peerKeyboardSnapshot?.PendingPeerFingerprint is not null);
    }

    private void HostPairingAgain_Click(object sender, RoutedEventArgs e) { pairingFileSaved = false; RefreshPairingGuide(); }
    private void PeerPairingAgain_Click(object sender, RoutedEventArgs e) { peerPairingConfirmed = false; RefreshPairingGuide(); }
}
