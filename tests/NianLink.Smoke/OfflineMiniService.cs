using DesktopToolbox.App.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;

namespace NianLink.Smoke;

// Deliberately does not construct the production networking, capture, input, or clipboard adapters.
internal sealed class OfflineMiniService : IMiniCollaborationService
{
    public MiniCollaborationSnapshot Snapshot { get; } = new() { ConnectionMessage = "隔离测试 · 未连接", PreviewEnabled = false, ClipboardEnabled = false };
    public event EventHandler? Changed { add { } remove { } }
    public int RefreshCount { get; private set; }
    public int RejectedOperationCount { get; private set; }
    public Task RefreshAsync(CancellationToken token = default) { token.ThrowIfCancellationRequested(); RefreshCount++; return Task.CompletedTask; }
    public void SetPreviewVisible(bool visible, bool highResolution = false) { }
    private Task Denied() { RejectedOperationCount++; return Task.FromException(new InvalidOperationException("隔离测试禁止业务操作。")); }
    private Task<T> Denied<T>() { RejectedOperationCount++; return Task.FromException<T>(new InvalidOperationException("隔离测试禁止业务操作。")); }
    public Task SwitchAsync(string platform, CancellationToken token = default) => Denied();
    public Task StartSharingAsync(CancellationToken token = default) => Denied();
    public Task StopSharingAsync(CancellationToken token = default) => Denied();
    public Task SetPreviewEnabledAsync(bool enabled, CancellationToken token = default) => Denied();
    public Task SetPreviewRefreshSecondsAsync(int seconds, CancellationToken token = default) => Denied();
    public Task SetClipboardEnabledAsync(bool enabled, CancellationToken token = default) => Denied();
    public Task SendFilesAsync(IReadOnlyList<string> paths, CancellationToken token = default) => Denied();
    public Task<SharedListing> ListSharedAsync(string? rootId = null, string relativePath = "", CancellationToken token = default) => Denied<SharedListing>();
    public Task<TransferReceipt> DownloadSharedAsync(string rootId, string relativePath, CancellationToken token = default) => Denied<TransferReceipt>();
    public Task<SharedPreview> GetSharedPreviewAsync(string rootId, string relativePath, CancellationToken token = default) => Denied<SharedPreview>();
    public Task SetReceiveDirectoryAsync(string path, CancellationToken token = default) => Denied();
    public Task SetSharedDirectoryAsync(string path, CancellationToken token = default) => Denied();
}
