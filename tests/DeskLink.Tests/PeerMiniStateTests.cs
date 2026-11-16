using DesktopToolbox.App.DeskLink;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;

namespace DeskLink.Tests;

public sealed class PeerMiniStateTests
{
    [Fact]
    public void ConnectedPeerShowsOnlyItsApprovedHostAndKeepsObservedFocus()
    {
        var state = PeerMiniState.Create("desktop", "laptop", new DeskLinkState
        {
            ProcessState = DeskLinkProcessState.Running,
            ConnectionState = DeskLinkConnectionState.Connected,
            ActiveComputer = "desktop"
        }, dataConnected: true);

        Assert.True(state.Connected);
        Assert.True(state.CanSwitchWindows);
        Assert.True(state.CanSwitchMac);
        Assert.Equal("desktop", state.LocalDeviceName);
        Assert.Equal("laptop", state.SelectedDevice);
        Assert.Equal("desktop", state.ActiveComputer);
        Assert.Equal("laptop", Assert.Single(state.Devices).Name);
    }

    [Fact]
    public void UnconfirmedOrDisconnectedPeerCannotSwitchAndDoesNotInventFocus()
    {
        var keyboard = new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
            ConnectionState = DeskLinkConnectionState.Connected, PendingPeerFingerprint = "pending",
            ActiveComputer = "some-other-device" };
        var state = PeerMiniState.Create("desktop", "laptop", keyboard, dataConnected: true);
        Assert.False(state.Connected);
        Assert.False(state.CanSwitchWindows);
        Assert.False(state.CanSwitchMac);
        Assert.Equal("", state.ActiveComputer);
        Assert.False(PeerMiniState.Create("desktop", "laptop", keyboard with { PendingPeerFingerprint = null }, false).Connected);
    }

    [Fact]
    public void IdenticalDevicesCannotBePresentedAsAnApprovedPair()
    {
        Assert.Throws<ArgumentException>(() => PeerMiniState.Create("desktop", "desktop", new(), dataConnected: true));
    }

    [Fact]
    public async Task MiniCannotSendToOrSelectAnotherDevice()
    {
        var backend = new Backend();
        await using var mini = new PeerMiniService("desktop", "laptop", backend);
        await mini.RefreshAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => mini.SelectDeviceAsync("other"));
        await Assert.ThrowsAsync<ArgumentException>(() => mini.SendFilesToDeviceAsync("other", ["file"]));
        Assert.Equal(0, backend.Sends);
        await mini.SendFilesToDeviceAsync("laptop", ["file"]);
        Assert.Equal(1, backend.Sends);
    }

    [Fact]
    public async Task OfflineMiniDoesNotExposeLastRemoteFrame()
    {
        var backend = new Backend { OfflineFrame = new PreviewFrame { State = "live", JpegBytes = [1, 2, 3] } };
        await using var mini = new PeerMiniService("desktop", "laptop", backend);
        await mini.RefreshAsync();
        Assert.False(mini.Snapshot.Connected);
        Assert.NotEqual("live", mini.Snapshot.Preview.State);
        Assert.Empty(mini.Snapshot.Preview.JpegBytes);
    }

    [Fact]
    public async Task UnconfirmedControlWarningSurvivesRefreshAndKeepsHostReturnAvailable()
    {
        var backend = new Backend { ControlWarning = "键鼠归还尚未确认", Connected = true };
        await using var mini = new PeerMiniService("desktop", "laptop", backend);
        await mini.RefreshAsync();
        Assert.Contains("未确认", mini.Snapshot.KeyboardSharingMessage);
        Assert.False(mini.Snapshot.CanSwitchWindows);
        Assert.True(mini.Snapshot.CanSwitchMac);
        await mini.RefreshAsync();
        Assert.Contains("未确认", mini.Snapshot.KeyboardSharingMessage);
    }

    [Fact]
    public async Task MiniShowsBackendTransferProgressAndCannotSendAfterDisposal()
    {
        var backend = new Backend { Transfer = new TransferProgress("t", "test.txt", 4, 8, "sending") };
        var mini = new PeerMiniService("desktop", "laptop", backend);
        await mini.RefreshAsync();
        Assert.Equal("sending", Assert.Single(mini.Snapshot.Transfers).State);
        await mini.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => mini.SendFilesToDeviceAsync("laptop", ["file"]));
        Assert.Equal(0, backend.Sends);
    }

    private sealed class Backend : IPeerMiniBackend
    {
        public int Sends { get; private set; }
        public PreviewFrame OfflineFrame { get; init; } = PreviewFrame.Off();
        public TransferProgress? Transfer { get; init; }
        public string? ControlWarning { get; init; }
        public bool Connected { get; init; }
        public bool PreviewInputOwnsForeground => false;
        public bool PreviewControlReady => false;
        public string PreviewControlHint => "";
        public Task<PeerMiniView> ReadAsync(CancellationToken token) => Task.FromResult(new PeerMiniView(Connected ? new DeskLinkState { ProcessState = DeskLinkProcessState.Running, ConnectionState = DeskLinkConnectionState.Connected } : new DeskLinkState(), Connected) { ControlWarning = ControlWarning, PreviewEnabled = true, Preview = OfflineFrame, Transfers = Transfer is null ? [] : [Transfer] });
        public DeskflowPreviewSession? CreatePreviewSession() => null;
        public Task SwitchAsync(bool local, CancellationToken token) => Task.CompletedTask;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        public Task SetPreviewEnabledAsync(bool enabled, CancellationToken token) => Task.CompletedTask;
        public Task SetPreviewRefreshSecondsAsync(int seconds, CancellationToken token) => Task.CompletedTask;
        public void SetPreviewVisible(bool visible, bool highResolution) { }
        public Task SetClipboardEnabledAsync(bool enabled, CancellationToken token) => Task.CompletedTask;
        public Task SendFilesAsync(IReadOnlyList<string> paths, CancellationToken token) { Sends++; return Task.CompletedTask; }
        public Task<SharedListing> ListSharedAsync(string? rootId, string path, CancellationToken token) => throw new NotSupportedException();
        public Task<TransferReceipt> DownloadSharedAsync(string rootId, string path, CancellationToken token) => throw new NotSupportedException();
        public Task<SharedPreview> PreviewSharedAsync(string rootId, string path, CancellationToken token) => throw new NotSupportedException();
        public Task SetReceiveDirectoryAsync(string path, CancellationToken token) => Task.CompletedTask;
        public Task SetSharedDirectoryAsync(string path, CancellationToken token) => Task.CompletedTask;
    }
}
