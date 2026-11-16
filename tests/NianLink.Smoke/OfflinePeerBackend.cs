using DesktopToolbox.App.DeskLink;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;

namespace NianLink.Smoke;

// Synthetic backend: never starts input, capture, clipboard, network, or file transfers.
internal sealed class OfflinePeerBackend : IPeerMiniBackend
{
    public bool PreviewInputOwnsForeground => false;
    public bool PreviewControlReady => false;
    public string PreviewControlHint => "隔离测试未连接";
    public int RefreshCount { get; private set; }
    public Task<PeerMiniView> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RefreshCount++;
        return Task.FromResult(new PeerMiniView(new DeskLinkState(), false));
    }
    public DeskflowPreviewSession? CreatePreviewSession() => null;
    private static Task Denied() => Task.FromException(new InvalidOperationException("隔离测试禁止业务操作。"));
    private static Task<T> Denied<T>() => Task.FromException<T>(new InvalidOperationException("隔离测试禁止业务操作。"));
    public Task SwitchAsync(bool local, CancellationToken token) => Denied();
    public Task StartAsync(CancellationToken token) => Denied();
    public Task StopAsync(CancellationToken token) => Denied();
    public Task SetPreviewEnabledAsync(bool enabled, CancellationToken token) => Denied();
    public Task SetPreviewRefreshSecondsAsync(int seconds, CancellationToken token) => Denied();
    public void SetPreviewVisible(bool visible, bool highResolution) { }
    public Task SetClipboardEnabledAsync(bool enabled, CancellationToken token) => Denied();
    public Task SendFilesAsync(IReadOnlyList<string> paths, CancellationToken token) => Denied();
    public Task<SharedListing> ListSharedAsync(string? rootId, string path, CancellationToken token) => Denied<SharedListing>();
    public Task<TransferReceipt> DownloadSharedAsync(string rootId, string path, CancellationToken token) => Denied<TransferReceipt>();
    public Task<SharedPreview> PreviewSharedAsync(string rootId, string path, CancellationToken token) => Denied<SharedPreview>();
    public Task SetReceiveDirectoryAsync(string path, CancellationToken token) => Denied();
    public Task SetSharedDirectoryAsync(string path, CancellationToken token) => Denied();
}
