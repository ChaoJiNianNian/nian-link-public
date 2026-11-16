using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;
using System.Diagnostics;

namespace DesktopToolbox.App.DeskLink;

/// <summary>Host mini-window adapter. File sends retain the explicitly rendered recipient.</summary>
public sealed class MultiDeviceMiniService : IMiniCollaborationService, IAsyncDisposable
{
    private readonly MultiPeerRuntime runtime;
    private readonly MultiDeviceCredentialVault vault;
    private readonly string hostName;
    private readonly Func<bool> ownsForeground;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sessionGate = new();
    private MiniCollaborationSnapshot snapshot = new() { MultiDeviceMode = true };
    private DeskflowPreviewSession? prepared, issued;
    private string? preparedPeer, selected;
    private Guid? previewRequest;
    private bool previewEnabled;
    private volatile bool visible, highResolution, disposed;
    private long previewRenewed;
    public event EventHandler? Changed;
    public event Action<string>? DeviceSelected;
    public MiniCollaborationSnapshot Snapshot => Volatile.Read(ref snapshot);
    public bool PreviewInputOwnsForeground => ownsForeground();
    public bool PreviewControlReady { get { lock (sessionGate) return (prepared ?? issued)?.Supported == true && Snapshot.PreviewEnabled && Snapshot.RemotePreviewSourceStatus?.Availability == "enabled"; } }
    public string PreviewControlHint => "请先连接设备并允许画面共享。";

    public MultiDeviceMiniService(MultiPeerRuntime runtime, MultiDeviceCredentialVault vault, string hostName, Func<bool>? ownsForeground = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.vault = vault ?? throw new ArgumentNullException(nameof(vault));
        DeskflowProfile.ValidateName(hostName); this.hostName = hostName;
        this.ownsForeground = ownsForeground ?? (() => false);
    }

    public async Task SelectDeviceAsync(string name, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (!runtime.Devices.Any(device => device.Name == name)) throw new ArgumentException("请选择已配对的设备。", nameof(name));
            if (selected == name) return;
            if ((await runtime.ReadPreviewAsync(token)).HasSession) throw new InvalidOperationException("请先归还预览中的键鼠，再选择其他设备。");
            if (selected is { } old) await runtime.SetPreviewRequestedAsync(old, false, token);
            await DisposePreparedAsync();
            selected = name; previewRequest = null; previewRenewed = 0; previewEnabled = false;
            Volatile.Write(ref snapshot, Snapshot with { SelectedDevice = name, PreviewEnabled = false, Preview = PreviewFrame.Off(), RemotePreviewSourceStatus = null });
        }
        finally { gate.Release(); }
        DeviceSelected?.Invoke(name); Notify();
    }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            var keyboard = await runtime.GetKeyboardStateAsync(token);
            var devices = runtime.Devices.Select(device => new MiniDevice(device.Name,
                (device.Platform == "mac" ? "Mac · " : "Windows · ") + device.Name)).ToArray();
            var peer = selected;
            if (peer is not null && !devices.Any(device => device.Name == peer)) { selected = peer = null; previewEnabled = false; previewRequest = null; await DisposePreparedAsync(); }
            var running = keyboard.ProcessState == DeskLinkProcessState.Running;
            var next = new MiniCollaborationSnapshot { MultiDeviceMode = true, Devices = devices, SelectedDevice = peer, LocalDeviceName = hostName,
                KeyboardSharingRunning = running, KeyboardCanStart = !running && vault.GetTrustedDevices().Count > 0, KeyboardCanStop = running,
                KeyboardSharingConnected = peer is not null && keyboard.ConnectedComputers.Contains(peer, StringComparer.Ordinal),
                KeyboardSharingMessage = running ? "键鼠共享运行中 · 可切换电脑" : "键鼠共享未启动", ActiveComputer = keyboard.ActiveComputer ?? "",
                CanSwitchWindows = running && keyboard.NativeControlAvailable,
                CanSwitchMac = peer is not null && keyboard.NativeControlAvailable && keyboard.ConnectedComputers.Contains(peer, StringComparer.Ordinal),
                PreviewEnabled = previewEnabled };
            if (peer is not null)
            {
                var status = await runtime.GetClipboardStateAsync(peer, token);
                var settings = await runtime.GetFileSettingsAsync(peer, token);
                var transfers = await runtime.GetTransfersAsync(peer, token);
                if (previewEnabled && visible)
                {
                    if (previewRequest is null || previewRenewed != 0 && Stopwatch.GetElapsedTime(previewRenewed) >= TimeSpan.FromSeconds(3)) previewRequest = Guid.NewGuid();
                }
                else { previewRequest = null; previewRenewed = 0; }
                await runtime.SetPreviewRequestedAsync(peer, previewEnabled && visible, token, previewRequest,
                    highResolution ? 1920 : 960, highResolution ? 1080 : 540);
                if (previewRequest is not null) previewRenewed = Stopwatch.GetTimestamp();
                var frame = previewEnabled && visible ? await runtime.GetPreviewAsync(peer, token) : PreviewFrame.Off();
                var source = await runtime.GetRemotePreviewSourceAsync(peer, token);
                bool wrongPeer; lock (sessionGate) wrongPeer = prepared is not null && preparedPeer != peer;
                if (wrongPeer) await DisposePreparedAsync();
                if (prepared is null)
                {
                    var candidate = await runtime.CreateHostPreviewSessionAsync(peer, token);
                    lock (sessionGate) { prepared = candidate; preparedPeer = peer; }
                }
                next = next with { Connected = status.Connected, ConnectionMessage = status.Connected ? "设备已连接" : "等待设备连接",
                    ClipboardEnabled = status.LocalEnabled, ClipboardMessage = status.RemoteEnabled ? "两端已开启文字剪贴板。" : "请在对方开启文字剪贴板。",
                    ReceiveDirectory = settings.ReceiveDirectory ?? "", SharedDirectory = settings.Shares?.FirstOrDefault()?.Path ?? "", Transfers = transfers,
                    PreviewRefreshSeconds = settings.PreviewRefreshSeconds, Preview = frame, PassivePreviewRequestId = previewRequest, RemotePreviewSourceStatus = source };
            }
            Volatile.Write(ref snapshot, next);
        }
        finally { gate.Release(); }
        Notify();
    }

    public DeskflowPreviewSession? CreateDeskflowPreviewSession()
    {
        lock (sessionGate) { if (preparedPeer != Snapshot.SelectedDevice) return null; var result = prepared; issued = result; prepared = null; preparedPeer = null; return result; }
    }
    public void SetPreviewVisible(bool value, bool highResolution = false) { visible = value; this.highResolution = highResolution; }
    public async Task SetPreviewEnabledAsync(bool enabled, CancellationToken token = default)
    { await gate.WaitAsync(token); try { ThrowIfDisposed(); RequireSelected(); previewEnabled = enabled; previewRequest = null; previewRenewed = 0; } finally { gate.Release(); } await RefreshAsync(token); }
    public async Task SetPreviewRefreshSecondsAsync(int seconds, CancellationToken token = default)
    { var peer = RequireSelected(); await runtime.SetPreviewRefreshSecondsAsync(peer, seconds, token); await RefreshAsync(token); }
    public async Task SetClipboardEnabledAsync(bool enabled, CancellationToken token = default)
    { var peer = RequireSelected(); await runtime.SetClipboardEnabledAsync(peer, enabled, token); await RefreshAsync(token); }
    public async Task StartSharingAsync(CancellationToken token = default)
    { await gate.WaitAsync(token); try { ThrowIfDisposed(); await runtime.StartAsync(vault.GetTrustedDevices(), token); } finally { gate.Release(); } await RefreshAsync(token); }
    public async Task StopSharingAsync(CancellationToken token = default)
    { await gate.WaitAsync(token); try { ThrowIfDisposed(); await runtime.StopAsync(token); selected = null; previewEnabled = false; await DisposePreparedAsync(); } finally { gate.Release(); } await RefreshAsync(token); }
    public async Task SwitchAsync(string platform, CancellationToken token = default)
    {
        var target = platform switch { "Windows" => hostName, "Mac" => RequireSelected(), _ => throw new ArgumentException("请选择设备。") };
        var result = await runtime.SwitchKeyboardAsync(target, token);
        if (!result.InputTransferred) throw new InvalidOperationException(result.Error ?? "键鼠切换未确认。");
        if (result.VideoStatus is "failed" or "acceptedUnverified") throw new InvalidOperationException(result.Error ?? "键鼠已切换，请确认显示器画面。");
        await RefreshAsync(token);
    }
    public Task SendFilesAsync(IReadOnlyList<string> paths, CancellationToken token = default) => SendFilesToDeviceAsync(RequireSelected(), paths, token);
    public async Task SendFilesToDeviceAsync(string name, IReadOnlyList<string> paths, CancellationToken token = default)
    {
        ThrowIfDisposed();
        if (!runtime.Devices.Any(device => device.Name == name)) throw new ArgumentException("接收设备已不可用。", nameof(name));
        await runtime.QueueFilesAsync(name, paths, token);
    }
    public async Task SetReceiveDirectoryAsync(string path, CancellationToken token = default) { await runtime.SetReceiveDirectoryAsync(RequireSelected(), path, token); await RefreshAsync(token); }
    public async Task SetSharedDirectoryAsync(string path, CancellationToken token = default)
    { await runtime.SetSharedDirectoriesAsync(RequireSelected(), string.IsNullOrEmpty(path) ? [] : [new SharedDirectory("shared", "共享文件夹", path)], token); await RefreshAsync(token); }
    public Task<SharedListing> ListSharedAsync(string? rootId = null, string relativePath = "", CancellationToken token = default) => runtime.ListLocalSharedAsync(RequireSelected(), rootId, relativePath, token);
    public Task<TransferReceipt> DownloadSharedAsync(string rootId, string relativePath, CancellationToken token = default) => runtime.CopyLocalSharedAsync(RequireSelected(), rootId, relativePath, token);
    public Task<SharedPreview> GetSharedPreviewAsync(string rootId, string relativePath, CancellationToken token = default) => runtime.PreviewLocalSharedAsync(RequireSelected(), rootId, relativePath, token);
    private string RequireSelected() => Snapshot.SelectedDevice ?? throw new InvalidOperationException("请先选择设备。");
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    private async Task DisposePreparedAsync()
    { DeskflowPreviewSession? old; lock (sessionGate) { old = prepared; prepared = null; preparedPeer = null; issued = null; } if (old is not null) await old.DisposeAsync(); }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            if (selected is { } peer) try { await runtime.SetPreviewRequestedAsync(peer, false, CancellationToken.None); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or ObjectDisposedException) { }
            await DisposePreparedAsync(); disposed = true;
        }
        finally { gate.Release(); }
    }
}
