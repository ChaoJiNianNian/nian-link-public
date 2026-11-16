using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;

namespace DesktopToolbox.App.DeskLink;

/// <summary>Data supplied by the already paired client, without its pairing credentials.</summary>
public sealed record PeerMiniView(DeskLinkState Keyboard, bool DataConnected)
{
    public string? ControlWarning { get; init; }
    public bool PreviewEnabled { get; init; }
    public int PreviewRefreshSeconds { get; init; } = 30;
    public PreviewFrame Preview { get; init; } = PreviewFrame.Off();
    public Guid? PassivePreviewRequestId { get; init; }
    public PreviewSourceStatus? RemotePreviewSourceStatus { get; init; }
    public bool ClipboardEnabled { get; init; }
    public string ClipboardMessage { get; init; } = "请在两端开启文字剪贴板。";
    public string ReceiveDirectory { get; init; } = "";
    public string SharedDirectory { get; init; } = "";
    public IReadOnlyList<TransferProgress> Transfers { get; init; } = [];
}

public interface IPeerMiniBackend
{
    bool PreviewInputOwnsForeground { get; }
    bool PreviewControlReady { get; }
    string PreviewControlHint { get; }
    Task<PeerMiniView> ReadAsync(CancellationToken token);
    DeskflowPreviewSession? CreatePreviewSession();
    Task SwitchAsync(bool local, CancellationToken token);
    Task StartAsync(CancellationToken token);
    Task StopAsync(CancellationToken token);
    Task SetPreviewEnabledAsync(bool enabled, CancellationToken token);
    Task SetPreviewRefreshSecondsAsync(int seconds, CancellationToken token);
    void SetPreviewVisible(bool visible, bool highResolution);
    Task SetClipboardEnabledAsync(bool enabled, CancellationToken token);
    Task SendFilesAsync(IReadOnlyList<string> paths, CancellationToken token);
    Task<SharedListing> ListSharedAsync(string? rootId, string path, CancellationToken token);
    Task<TransferReceipt> DownloadSharedAsync(string rootId, string path, CancellationToken token);
    Task<SharedPreview> PreviewSharedAsync(string rootId, string path, CancellationToken token);
    Task SetReceiveDirectoryAsync(string path, CancellationToken token);
    Task SetSharedDirectoryAsync(string path, CancellationToken token);
}

/// <summary>Routes a client's mini window exclusively to its previously approved host.</summary>
public sealed class PeerMiniService : IMiniCollaborationService, IAsyncDisposable
{
    private readonly string local, host;
    private readonly IPeerMiniBackend backend;
    private readonly SemaphoreSlim gate = new(1, 1);
    private MiniCollaborationSnapshot snapshot;
    private volatile bool disposed;
    public event EventHandler? Changed;
    public MiniCollaborationSnapshot Snapshot => Volatile.Read(ref snapshot);
    public bool PreviewInputOwnsForeground => !disposed && backend.PreviewInputOwnsForeground;
    public bool PreviewControlReady => !disposed && Snapshot.Connected && backend.PreviewControlReady;
    public string PreviewControlHint => backend.PreviewControlHint;

    public PeerMiniService(string local, string host, IPeerMiniBackend backend)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        snapshot = PeerMiniState.Create(local, host, new(), false);
        this.local = local; this.host = host;
    }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var view = await backend.ReadAsync(token);
            var next = PeerMiniState.Create(local, host, view.Keyboard, view.DataConnected) with
            {
                PreviewEnabled = view.PreviewEnabled, PreviewRefreshSeconds = view.PreviewRefreshSeconds,
                Preview = view.DataConnected ? view.Preview : PreviewFrame.Off("disconnected"),
                PassivePreviewRequestId = view.DataConnected ? view.PassivePreviewRequestId : null,
                RemotePreviewSourceStatus = view.DataConnected ? view.RemotePreviewSourceStatus : null,
                ClipboardEnabled = view.ClipboardEnabled, ClipboardMessage = view.ClipboardMessage,
                ReceiveDirectory = view.ReceiveDirectory, SharedDirectory = view.SharedDirectory, Transfers = view.Transfers
            };
            if (!string.IsNullOrWhiteSpace(view.ControlWarning))
                next = next with { KeyboardSharingMessage = view.ControlWarning, CanSwitchWindows = false };
            Volatile.Write(ref snapshot, next);
        }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task SelectDeviceAsync(string name, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); ThrowIfUnavailable(); RequireHost(name); return Task.CompletedTask; }
    public DeskflowPreviewSession? CreateDeskflowPreviewSession() { ThrowIfUnavailable(); return backend.CreatePreviewSession(); }
    public async Task SwitchAsync(string platform, CancellationToken token = default)
    {
        ThrowIfUnavailable();
        var localTarget = platform switch { "Windows" => true, "Mac" => false, _ => throw new ArgumentException("请选择本机或主控。", nameof(platform)) };
        await backend.SwitchAsync(localTarget, token); await RefreshAsync(token);
    }
    public async Task StartSharingAsync(CancellationToken token = default) { ThrowIfUnavailable(); await backend.StartAsync(token); await RefreshAsync(token); }
    public async Task StopSharingAsync(CancellationToken token = default) { ThrowIfUnavailable(); await backend.StopAsync(token); await RefreshAsync(token); }
    public async Task SetPreviewEnabledAsync(bool enabled, CancellationToken token = default) { ThrowIfUnavailable(); await backend.SetPreviewEnabledAsync(enabled, token); await RefreshAsync(token); }
    public async Task SetPreviewRefreshSecondsAsync(int seconds, CancellationToken token = default) { ThrowIfUnavailable(); await backend.SetPreviewRefreshSecondsAsync(seconds, token); await RefreshAsync(token); }
    public void SetPreviewVisible(bool visible, bool highResolution = false) { ThrowIfUnavailable(); backend.SetPreviewVisible(visible, highResolution); }
    public async Task SetClipboardEnabledAsync(bool enabled, CancellationToken token = default) { ThrowIfUnavailable(); await backend.SetClipboardEnabledAsync(enabled, token); await RefreshAsync(token); }
    public Task SendFilesAsync(IReadOnlyList<string> paths, CancellationToken token = default) { ThrowIfUnavailable(); return backend.SendFilesAsync(paths, token); }
    public Task SendFilesToDeviceAsync(string name, IReadOnlyList<string> paths, CancellationToken token = default)
    { ThrowIfUnavailable(); RequireHost(name); return backend.SendFilesAsync(paths, token); }
    public Task<SharedListing> ListSharedAsync(string? rootId = null, string relativePath = "", CancellationToken token = default) { ThrowIfUnavailable(); return backend.ListSharedAsync(rootId, relativePath, token); }
    public Task<TransferReceipt> DownloadSharedAsync(string rootId, string relativePath, CancellationToken token = default) { ThrowIfUnavailable(); return backend.DownloadSharedAsync(rootId, relativePath, token); }
    public Task<SharedPreview> GetSharedPreviewAsync(string rootId, string relativePath, CancellationToken token = default) { ThrowIfUnavailable(); return backend.PreviewSharedAsync(rootId, relativePath, token); }
    public async Task SetReceiveDirectoryAsync(string path, CancellationToken token = default) { ThrowIfUnavailable(); await backend.SetReceiveDirectoryAsync(path, token); await RefreshAsync(token); }
    public async Task SetSharedDirectoryAsync(string path, CancellationToken token = default) { ThrowIfUnavailable(); await backend.SetSharedDirectoryAsync(path, token); await RefreshAsync(token); }
    private void RequireHost(string name) { if (!string.Equals(name, host, StringComparison.Ordinal)) throw new ArgumentException("该连接只能操作已配对的主控。", nameof(name)); }
    private void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(disposed, this);
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (disposed) return; disposed = true; backend.SetPreviewVisible(false, false); }
        finally { gate.Release(); }
    }
}
