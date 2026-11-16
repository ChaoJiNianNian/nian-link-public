using System.IO;
using System.Net.Sockets;
using System.Windows;
using DesktopToolbox.App.DeskLink;
using DesktopToolbox.Core;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    private PeerMiniService? peerMiniService;
    private MiniCollaborationWindow? peerMiniWindow;
    private int? peerEngineProcessId;

    private async void OpenPeerMini_Click(object sender, RoutedEventArgs e) => await WorkAsync(async () =>
    {
        if (peerMiniWindow is { } existing) { await existing.ShowMiniAsync(); return; }
        var current = peer ?? throw new InvalidOperationException("请先开始连接主控。");
        var invitation = peerStore?.Get() ?? throw new InvalidOperationException("请先确认主控配对。");
        if (peerData is null) throw new InvalidOperationException("文件与画面连接尚未准备好。");
        if (peerDetached is not null) throw new InvalidOperationException("请先关闭设置页中的放大预览，再打开互联小窗。");
        var state = await current.GetStateAsync();
        if (state.ProcessState != DeskLinkProcessState.Running) throw new InvalidOperationException("请先开始连接主控。");
        peerEngineProcessId = state.ProcessId;
        var service = new PeerMiniService(invitation.PeerName, invitation.HostName, new PeerMiniBackend(this, invitation));
        MiniCollaborationWindow? window = null;
        try
        {
            await service.RefreshAsync();
            var miniRoot = Path.Combine(root, "client", "mini");
            window = new MiniCollaborationWindow(service, new JsonFileStore(new AppPaths(miniRoot)),
                error => Dispatcher.BeginInvoke(() => ShowFailure(error, "互联小窗")),
                Path.Combine(miniRoot, "webview"));
            window.ConnectionSettingsRequested += () => { Show(); WindowState = WindowState.Normal; Activate(); };
            await window.StartAsync();
            peerMiniService = service; peerMiniWindow = window;
            await window.ShowMiniAsync();
        }
        catch
        {
            if (window is not null) await window.StopAsync();
            await service.DisposeAsync();
            throw;
        }
    });

    private async Task ClosePeerMiniAsync()
    {
        var window = peerMiniWindow; var service = peerMiniService;
        if (window is not null)
        {
            try { await window.ClosePreviewForDeviceChangeAsync(); }
            catch (InvalidOperationException) when (PeerControlUnresolved) { /* Stop must remain available; the owner keeps the outcome unknown. */ }
        }
        if (window is not null) await window.StopAsync();
        if (service is not null) await service.DisposeAsync();
        peerMiniWindow = null; peerMiniService = null;
    }

    private sealed class PeerMiniBackend(MainWindow owner, MultiPeerInvitation approved) : IPeerMiniBackend
    {
        private readonly object transferGate = new();
        private readonly List<TransferProgress> transfers = [];
        private MultiPeerInvitation Approved() => owner.peerStore?.Get() is { } current && current == approved
            ? current : throw new InvalidOperationException("配对已变化，请重新打开互联小窗。");
        private WindowsPeerLink Keyboard() { Approved(); return owner.peer ?? throw new InvalidOperationException("请先连接主控。"); }
        private CollaborationClient Data() { Approved(); return owner.peerData ?? throw new InvalidOperationException("请先连接主控。"); }

        public bool PreviewInputOwnsForeground => owner.peer is { } current &&
            PreviewForeground.IsOwnedCaptureWindow(owner.peerEngineProcessId, current.OwnsLiveProcess);
        public bool PreviewControlReady => !owner.PeerControlUnconfirmed && owner.peer is not null && owner.peerData is not null && owner.peerViewEnabled &&
            owner.peerRemoteLiveSupported && owner.peerRemoteGenerationSupported && owner.peerRemoteSource?.Availability == "enabled";
        public string PreviewControlHint => owner.PeerMiniControlHint;

        public async Task<PeerMiniView> ReadAsync(CancellationToken token)
        {
            Approved();
            var keyboard = owner.peer is { } current ? await current.GetStateAsync(token) : new DeskLinkState();
            owner.peerEngineProcessId = keyboard.ProcessId;
            var connected = false; var remoteClipboard = false;
            if (owner.peerData is { } data)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var status = await data.GetStatusAsync(timeout.Token);
                    connected = ReferenceEquals(data, owner.peerData);
                    remoteClipboard = status.RemoteClipboardEnabled;
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is IOException or SocketException or OperationCanceledException or UnauthorizedAccessException or System.Security.Authentication.AuthenticationException or ObjectDisposedException) { }
            }
            return new PeerMiniView(keyboard, connected)
            {
                ControlWarning = owner.PeerControlUnconfirmed ? "键鼠操作尚未确认，请检查键鼠所在设备。" : null,
                PreviewEnabled = owner.peerViewEnabled, PreviewRefreshSeconds = owner.peerRefreshSeconds,
                Preview = owner.peerViewEnabled ? owner.lastPeerFrame : PreviewFrame.Off(),
                PassivePreviewRequestId = owner.peerPreviewRequest, RemotePreviewSourceStatus = owner.peerRemoteSource,
                ClipboardEnabled = owner.peerClipboardEnabled,
                ClipboardMessage = remoteClipboard ? "两端已开启文字剪贴板。" : "请在主控开启文字剪贴板。",
                ReceiveDirectory = owner.peerReceiveDirectory ?? "", Transfers = TransferSnapshot()
            };
        }

        private TransferProgress[] TransferSnapshot() { lock (transferGate) return transfers.ToArray(); }
        private void RecordTransfer(TransferProgress progress)
        {
            lock (transferGate)
            {
                var index = transfers.FindIndex(item => item.Id == progress.Id);
                if (index >= 0) transfers[index] = progress;
                else { transfers.Add(progress); if (transfers.Count > 5) transfers.RemoveAt(0); }
            }
        }
        private sealed class TransferReporter(Action<TransferProgress> report) : IProgress<TransferProgress>
        { public void Report(TransferProgress progress) => report(progress); }

        public DeskflowPreviewSession? CreatePreviewSession()
        {
            var current = Keyboard(); var data = Data();
            var source = new RemoteSubscriptionControlLivePreviewSource(() => ReferenceEquals(owner.peerData, data) ? data : null,
                () => owner.peerViewEnabled && owner.peerRemoteGenerationSupported && owner.peerRemoteLiveSupported && owner.peerRemoteSource?.Availability == "enabled");
            return new DeskflowPreviewSession(owner.PreviewPeer(current, Approved()), source);
        }

        public async Task SwitchAsync(bool local, CancellationToken token)
        {
            var invitation = Approved();
            var result = await owner.SwitchPeerAsync(Keyboard(), invitation, local ? invitation.PeerName : invitation.HostName, token);
            if (!result.InputTransferred) throw new InvalidOperationException(result.Error ?? "键鼠切换未确认。");
            if (result.VideoStatus is "failed" or "acceptedUnverified") throw new InvalidOperationException(result.Error ?? "键鼠已切换，请确认显示器画面。");
        }
        public Task StartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Approved(); return owner.StartPeerInsideAsync(); }
        public async Task StopAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Approved();
            if (owner.peer is { } current) owner.StopPeerControl(current);
            if (owner.peerMiniWindow is { } mini)
            {
                try { await mini.ClosePreviewForDeviceChangeAsync(); }
                catch (InvalidOperationException) when (owner.PeerControlUnresolved) { }
            }
            await owner.StopPeerInsideAsync();
        }
        public Task SetPreviewEnabledAsync(bool enabled, CancellationToken token) { Approved(); return owner.SetPeerViewPreviewAsync(enabled, token); }
        public Task SetPreviewRefreshSecondsAsync(int seconds, CancellationToken token) { Approved(); return owner.SetPeerRefreshAsync(seconds, token); }
        public void SetPreviewVisible(bool visible, bool highResolution)
        { owner.peerMiniPreviewVisible = visible; owner.peerMiniHighResolution = visible && highResolution; }
        public Task SetClipboardEnabledAsync(bool enabled, CancellationToken token) { token.ThrowIfCancellationRequested(); Approved(); return owner.SetPeerClipboardAsync(enabled); }
        public async Task SendFilesAsync(IReadOnlyList<string> paths, CancellationToken token)
        { await Data().SendFilesAsync(paths, new TransferReporter(RecordTransfer), token); }
        public Task<SharedListing> ListSharedAsync(string? rootId, string path, CancellationToken token) => Data().ListSharedAsync(rootId, path, token);
        public Task<SharedPreview> PreviewSharedAsync(string rootId, string path, CancellationToken token) => Data().GetSharedPreviewAsync(rootId, path, token);
        public Task<TransferReceipt> DownloadSharedAsync(string rootId, string path, CancellationToken token)
        {
            var directory = owner.peerReceiveDirectory;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) throw new InvalidOperationException("请先选择本机收件文件夹。");
            return Data().DownloadSharedAsync(rootId, path, directory, null, token);
        }
        public async Task SetReceiveDirectoryAsync(string path, CancellationToken token)
        {
            var invitation = Approved();
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new DirectoryNotFoundException("请选择本机收件文件夹。");
            var settings = owner.peerSettings ?? throw new InvalidOperationException("本机设置尚未打开。");
            var key = InvitationKey(invitation);
            await settings.SetAsync(key, settings.Get(key) with { ReceiveDirectory = path }, token);
            owner.peerReceiveDirectory = path;
            owner.PeerFilesStatus.Text = "收件位置：" + path;
        }
        public Task SetSharedDirectoryAsync(string path, CancellationToken token) =>
            Task.FromException(new NotSupportedException("被控端暂不提供本机共享目录，请在主控设置共享文件夹。"));
    }
}
