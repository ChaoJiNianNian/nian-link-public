using System.Text.Json;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Fact]
    public async Task MultiDevicePairingUsesItsAssignedDataPort()
    {
        using var folder = new TestDirectory();
        var receive = System.IO.Path.Combine(folder.Path, "receive");
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, ReceiveDirectory = receive }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac") with { DataPort = server.Port };
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle),
            new FakeMedia(), 1, null, null, null, null, usePairedDataPort: true);
        var source = System.IO.Path.Combine(folder.Path, "fixture.txt"); await File.WriteAllTextAsync(source, "assigned port");
        await service.SendFilesAsync([source]);
        Assert.Equal("assigned port", await File.ReadAllTextAsync(Assert.Single(service.Snapshot.Receipts[0].SavedPaths)));
    }

    [Fact]
    public async Task MultiDeviceMacAndHostExchangeGenerationBoundPreviewFrames()
    {
        using var folder = new TestDirectory();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new SyntheticProtector());
        var token = await vault.AddConfirmedAsync("My-Mac", new string('1', 64));
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "Win-PC",
            System.Net.IPAddress.Loopback, 0, 0, peers => new MultiKeyboard(peers));
        await runtime.StartAsync(vault.GetTrustedDevices(), CancellationToken.None);
        var bundle = new DeskLinkPairingBundle { Host = "127.0.0.1", Port = runtime.ControlPort, DataPort = runtime.Devices.Single().DataPort,
            WindowsName = "Win-PC", MacName = "My-Mac", CertificateSha256 = runtime.CertificateSha256!, Token = token };
        var media = new FakeMedia();
        await using var mac = new MacCollaborationService(System.IO.Path.Combine(folder.Path, "mac"), _ => Task.FromResult<DeskLinkPairingBundle?>(bundle),
            media, 1, null, null, null, null, usePairedDataPort: true);
        var hostSource = new PreviewSourceStatus(Guid.NewGuid(), 1, "enabled");
        await runtime.SetPreviewSourceAsync("My-Mac", hostSource, CancellationToken.None);
        var macRequest = Guid.NewGuid();
        await runtime.SetPreviewRequestedAsync("My-Mac", true, CancellationToken.None, macRequest);
        await mac.SetPreviewEnabledAsync(true);
        for (var i = 0; i < 3 && (await runtime.GetPreviewAsync("My-Mac", CancellationToken.None)).State != "live"; i++) await mac.RefreshOnceAsync();
        Assert.Equal("live", (await runtime.GetPreviewAsync("My-Mac", CancellationToken.None)).State);
        var demand = await runtime.GetCaptureDemandAsync("My-Mac", CancellationToken.None);
        Assert.True(demand.Requested); Assert.NotNull(demand.RequestId);
        var image = await media.CaptureAsync();
        await runtime.PublishPreviewAsync("My-Mac", new PreviewFrame { State = "live", Width = image.Width, Height = image.Height,
            JpegBytes = image.JpegBytes!, RequestId = demand.RequestId, SourceStatus = hostSource, CaptureSequence = 1 }, CancellationToken.None);
        await mac.RefreshOnceAsync();
        Assert.Equal("live", mac.Snapshot.Preview.State);
        await runtime.SetPreviewSourceAsync("My-Mac", hostSource with { Revision = 2, Availability = "locked" }, CancellationToken.None);
        await mac.RefreshOnceAsync();
        Assert.NotEqual("live", mac.Snapshot.Preview.State);
    }

    private sealed class SyntheticProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }
    private sealed class MultiKeyboard(IReadOnlyList<TrustedDeviceIdentity> peers) : IMultiPeerKeyboard
    {
        public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
        public Task<DeskLinkState> GetStateAsync(CancellationToken ct) => Task.FromResult(new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
            NativeControlAvailable = true, ActiveComputer = "Win-PC", ConnectedComputers = peers.Select(x => x.Name).ToArray() });
        public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long revision, CancellationToken ct) =>
            Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target });
        public Task<DeskLinkState> StopAsync(CancellationToken ct) => GetStateAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CaptureUsesRequestedCadenceWhileClipboardAndSubscriptionsStillPollNormally()
    {
        using var folder = new TestDirectory(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");var native = new FakeMedia();var clock = new CaptureClock();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), native, server.Port, clock);
        await service.SetPreviewEnabledAsync(true);await service.SetPreviewRefreshSecondsAsync(60);server.SetPreviewRequested(true, previewRefreshSeconds:30);
        await service.RefreshOnceAsync();Assert.Equal(1,native.Captures);Assert.Equal(60,server.LocalCaptureRefreshSeconds);
        clock.Advance(29);await service.SetClipboardEnabledAsync(true);await service.RefreshOnceAsync();Assert.Equal(1,native.Captures);Assert.True(server.RemoteClipboardEnabled);
        clock.Advance(1);await service.RefreshOnceAsync();Assert.Equal(2,native.Captures);
        server.SetPreviewRequested(true,previewRefreshSeconds:5);await service.RefreshOnceAsync();Assert.Equal(3,native.Captures);
        await service.SetPreviewEnabledAsync(false);await service.RefreshOnceAsync();Assert.Equal(3,native.Captures);Assert.Empty(server.GetRemotePreview().JpegBytes);
    }
    private sealed class CaptureClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public void Advance(int seconds) => timestamp += seconds * 1000;
    }
    [Fact]
    public async Task ManualOffStopsActualRemoteCaptureAndClearsBothDirections()
    {
        using var folder = new TestDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var media = new FakeMedia(); await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        server.SetPreviewRequested(true, 1920, 1080);
        await service.RefreshOnceAsync();
        Assert.Equal(0, media.Captures); Assert.Empty(server.GetRemotePreview().JpegBytes);
        await service.SetPreviewEnabledAsync(true); service.SetViewerVisibility(false, true);
        await service.RefreshOnceAsync();
        Assert.Equal(1, media.Captures); Assert.Equal((1920,1080), media.LastSize); Assert.Equal("live", server.GetRemotePreview().State);
        Assert.Equal(1920, server.LocalCaptureMaxWidth); Assert.Equal(1080, server.LocalCaptureMaxHeight);
        await service.SetPreviewEnabledAsync(false);
        Assert.Empty(service.Snapshot.Preview.JpegBytes); Assert.Empty(server.GetRemotePreview().JpegBytes); Assert.False(server.LocalCaptureRequested);
        await service.RefreshOnceAsync(); Assert.Equal(1, media.Captures);
    }

    [Fact]
    public async Task ADetachedVisibleViewerKeepsStreamingWhileTheMiniIsCollapsed()
    {
        using var folder = new TestDirectory(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port);
        await service.SetPreviewEnabledAsync(true); service.SetViewerVisibility(false, true); await service.RefreshOnceAsync();
        Assert.True(server.LocalCaptureRequested);
        service.SetViewerVisibility(false, false); await service.RefreshOnceAsync(); Assert.False(server.LocalCaptureRequested);
        Assert.True(service.Snapshot.Settings.PreviewEnabled);
    }

    [Fact]
    public async Task FilesAndSharedDownloadsAreRealLocalCopiesThroughThePairedDataChannel()
    {
        using var folder = new TestDirectory(); var shared = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "shared")).FullName;
        var macReceive = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "mac-receive")).FullName;
        var file = System.IO.Path.Combine(shared, "图纸说明.txt"); await File.WriteAllTextAsync(file, "真实资料");
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, ReceiveDirectory = System.IO.Path.Combine(folder.Path, "win-receive"), SharedDirectories = [new("work", "项目资料", shared)] }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port);
        await service.SetReceiveDirectoryAsync(macReceive); await service.SendFilesAsync([file]);
        Assert.Equal("真实资料", await File.ReadAllTextAsync(Assert.Single(service.Snapshot.Receipts[0].SavedPaths)));
        var item = Assert.Single((await service.ListSharedAsync("work")).Items);
        Assert.Equal("真实资料", System.Text.Encoding.UTF8.GetString((await service.PreviewSharedAsync(item)).Bytes));
        await service.DownloadSharedAsync(item);
        Assert.Equal("真实资料", await File.ReadAllTextAsync(Assert.Single(service.Snapshot.Receipts[0].SavedPaths)));
        Assert.True(File.Exists(file));
    }
    [Fact]
    public async Task FailedSendingKeepsItsIdAcrossRestartAndRetryUsesThatSameId()
    {
        using var folder = new TestDirectory(); var file = System.IO.Path.Combine(folder.Path, "retry.txt"); await File.WriteAllTextAsync(file, "resume me");
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"); string id;
        await using (var first = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port))
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => first.SendFilesAsync([file]));
            Assert.Contains("Windows", failure.Message);
            Assert.Contains("收件", failure.Message);
            Assert.Equal("failed", first.Snapshot.Transfer!.State);
            Assert.Contains("收件", first.Snapshot.Transfer.Error);
            id = first.Snapshot.Settings.PendingSend!.Id; Assert.Equal(32, id.Length);
        }
        server.UpdateReceiveDirectory(System.IO.Path.Combine(folder.Path, "received"));
        await using var restarted = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port);
        await restarted.StartAsync(); Assert.Equal(id, restarted.Snapshot.Settings.PendingSend!.Id);
        await restarted.RetrySendAsync();
        Assert.Equal(id, Assert.Single(restarted.Snapshot.Receipts).Id); Assert.Null(restarted.Snapshot.Settings.PendingSend);
        Assert.Equal("resume me", await File.ReadAllTextAsync(Assert.Single(restarted.Snapshot.Receipts[0].SavedPaths)));
    }

    [Fact]
    public async Task PendingIncomingFilesAskForFolderAndResumeAfterUserSelectsOne()
    {
        using var folder = new TestDirectory();
        var source = System.IO.Path.Combine(folder.Path, "incoming.txt");
        await File.WriteAllTextAsync(source, "synthetic content");
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port);
        await server.QueueFilesAsync([source]);
        await service.RefreshOnceAsync();
        Assert.True(service.Snapshot.ReceiveSetupRequired);
        Assert.Empty(service.Snapshot.Receipts);
        var receive = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "mac-receive")).FullName;
        await service.SetReceiveDirectoryAsync(receive);
        Assert.False(service.Snapshot.ReceiveSetupRequired);
        await service.RefreshOnceAsync();
        for (var attempt = 0; attempt < 50 && service.Snapshot.Receipts.Count == 0; attempt++) await Task.Delay(100);
        Assert.Single(service.Snapshot.Receipts);
        Assert.Equal("synthetic content", await File.ReadAllTextAsync(Assert.Single(service.Snapshot.Receipts[0].SavedPaths)));
    }

    [Fact]
    public async Task FailedFolderPersistenceDoesNotPretendThatFolderWasSaved()
    {
        using var folder = new TestDirectory();
        var receive = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "receive")).FullName;
        Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "collaboration.json"));
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), new FakeMedia());
        await Assert.ThrowsAnyAsync<Exception>(() => service.SetReceiveDirectoryAsync(receive));
        Assert.Empty(service.Snapshot.Settings.ReceiveDirectory);
    }

    [Fact]
    public async Task RemovedReceiveFolderShowsErrorWithoutPretendingFileWasReceived()
    {
        using var folder = new TestDirectory();
        var source = System.IO.Path.Combine(folder.Path, "incoming.txt"); await File.WriteAllTextAsync(source, "synthetic");
        var receive = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "receive")).FullName;
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port);
        await service.SetReceiveDirectoryAsync(receive);
        Directory.Delete(receive);
        await server.QueueFilesAsync([source]);
        await service.RefreshOnceAsync();
        for (var attempt = 0; attempt < 50 && service.Snapshot.ReceiveError is null; attempt++) await Task.Delay(100);
        Assert.NotNull(service.Snapshot.ReceiveError);
        Assert.Empty(service.Snapshot.Receipts);
        Assert.False(Directory.Exists(receive));
    }

    [Fact]
    public async Task ChangingReceiveFolderDuringTransferIsRejectedUntilTransferFinishes()
    {
        using var folder = new TestDirectory();
        var source = System.IO.Path.Combine(folder.Path, "slow.bin"); await File.WriteAllBytesAsync(source, new byte[524288]);
        var original = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "original")).FullName;
        var alternate = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "alternate")).FullName;
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, BytesPerSecond = 65536 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port);
        await service.SetReceiveDirectoryAsync(original);
        await server.QueueFilesAsync([source]);
        await service.RefreshOnceAsync();
        for (var attempt = 0; attempt < 30 && service.Snapshot.Transfer?.State != "receiving"; attempt++) await Task.Delay(100);
        Assert.Equal("receiving", service.Snapshot.Transfer?.State);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetReceiveDirectoryAsync(alternate));
        Assert.Contains("接收完成", failure.Message);
        Assert.Equal(original, service.Snapshot.Settings.ReceiveDirectory);
        for (var attempt = 0; attempt < 100 && service.Snapshot.Receipts.Count == 0; attempt++) await Task.Delay(100);
        Assert.Single(service.Snapshot.Receipts);
        await service.SetReceiveDirectoryAsync(alternate);
        Assert.Equal(alternate, service.Snapshot.Settings.ReceiveDirectory);
    }

    [Fact]
    public async Task ChangingReceiveFolderDuringSharedDownloadIsRejectedUntilFinished()
    {
        using var folder = new TestDirectory();
        var shared = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "shared")).FullName;
        var source = System.IO.Path.Combine(shared, "slow.bin"); await File.WriteAllBytesAsync(source, new byte[524288]);
        var original = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "original")).FullName;
        var alternate = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "alternate")).FullName;
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, BytesPerSecond = 65536, SharedDirectories = [new("work", "shared", shared)] }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port);
        await service.SetReceiveDirectoryAsync(original);
        var item = Assert.Single((await service.ListSharedAsync("work")).Items);
        var download = service.DownloadSharedAsync(item);
        for (var attempt = 0; attempt < 30 && service.Snapshot.Transfer?.State != "receiving"; attempt++) await Task.Delay(100);
        Assert.Equal("receiving", service.Snapshot.Transfer?.State);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetReceiveDirectoryAsync(alternate));
        Assert.Contains("接收完成", failure.Message);
        Assert.Equal(original, service.Snapshot.Settings.ReceiveDirectory);
        await download;
        await service.SetReceiveDirectoryAsync(alternate);
        Assert.Equal(alternate, service.Snapshot.Settings.ReceiveDirectory);
    }

    [Fact]
    public async Task ClipboardOffCancelsAPendingNativePasteBeforeItCanWrite()
    {
        using var folder = new TestDirectory(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var native = new DelayedClipboard(); await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), native, server.Port);
        server.SetLocalClipboardEnabled(true); await service.SetClipboardEnabledAsync(true); await service.RefreshOnceAsync();
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, "do not paste after off"));
        var pending = service.RefreshOnceAsync(); await native.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service.SetClipboardEnabledAsync(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.False(native.Applied);
        Assert.Null(server.GetRemoteClipboard());
    }
    private sealed class DelayedClipboard : IMacMediaBridge
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Applied { get; private set; }
        public async Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default)
        {
            using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(request));
            if (parameters.RootElement.GetProperty("action").GetString() == "clipboardWrite")
            {
                WriteStarted.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); Applied = true;
            }
            return JsonDocument.Parse("{\"status\":\"clipboard\",\"changeCount\":1,\"text\":\"baseline\"}");
        }
        public Task<MacScreenCaptureReply> CaptureAsync(int width = 960, int height = 540, uint? windowId = null, CancellationToken cancellationToken = default) => Task.FromResult(new MacScreenCaptureReply { Status = "permissionRequired" });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task InflightProgressCannotRestoreTheUsersDisabledPreviewChoice()
    {
        using var folder = new TestDirectory(); await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), new FakeMedia());
        service.UpdateState(state => state with { Settings = state.Settings with { PreviewEnabled = true } });
        using var barrier = new Barrier(2); var settingsCalls = 0; var progressCalls = 0;
        var settings = Task.Run(() => service.UpdateState(state => { if (Interlocked.Increment(ref settingsCalls) == 1) barrier.SignalAndWait(); return state with { Settings = state.Settings with { PreviewEnabled = false } }; }));
        var progress = Task.Run(() => service.UpdateState(state => { if (Interlocked.Increment(ref progressCalls) == 1) barrier.SignalAndWait(); return state with { Transfer = new("test", "file", 10, 20, "sending") }; }));
        await Task.WhenAll(settings, progress);
        Assert.False(service.Snapshot.Settings.PreviewEnabled); Assert.Equal(10, service.Snapshot.Transfer!.BytesTransferred);
    }
    [Fact]
    public async Task RetryingApplicationExitCanDisposeCollaborationTwice()
    {
        using var folder = new TestDirectory(); var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), new FakeMedia());
        await service.DisposeAsync(); await service.DisposeAsync();
    }
    [Fact]
    public async Task ANewLocalCopyDuringTheNetworkRoundTripWinsOverAnOlderRemotePaste()
    {
        using var folder = new TestDirectory(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"); var native = new LocalCopyWins();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), native, server.Port);
        server.SetLocalClipboardEnabled(true); await service.SetClipboardEnabledAsync(true); await service.RefreshOnceAsync();
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, "old remote"));
        await service.RefreshOnceAsync(); Assert.False(native.Applied);
        await service.RefreshOnceAsync(); Assert.Equal("fresh local copy", server.GetRemoteClipboard()!.Text);
    }
    private sealed class LocalCopyWins : IMacMediaBridge
    {
        private int count = 1; public bool Applied { get; private set; }
        public Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default)
        {
            using var args = JsonDocument.Parse(JsonSerializer.Serialize(request));
            if (args.RootElement.GetProperty("action").GetString() == "clipboardWrite")
            {
                Assert.Equal(1, args.RootElement.GetProperty("expectedChangeCount").GetInt32());
                count = 2; return Task.FromResult(JsonDocument.Parse("{\"status\":\"changed\",\"changeCount\":2}"));
            }
            return Task.FromResult(JsonDocument.Parse(count == 1 ? "{\"status\":\"clipboard\",\"changeCount\":1,\"text\":\"baseline\"}" : "{\"status\":\"clipboard\",\"changeCount\":2,\"text\":\"fresh local copy\"}"));
        }
        public Task<MacScreenCaptureReply> CaptureAsync(int width=960,int height=540,uint? windowId=null,CancellationToken cancellationToken=default)=>Task.FromResult(new MacScreenCaptureReply());
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static PairedControlServer CreateControl(string profile) => new(new() { ProfileDirectory = profile, Port = 0, AllowedTargets = ["Win-PC", "My-Mac"] }, _ => Task.FromResult(new DeskLinkState()), (_, _) => Task.FromResult(new DeskLinkSwitchResult()));
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nian-mac-collab-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
    private sealed class FakeMedia : IMacMediaBridge
    {
        public int Captures { get; private set; }
        public (int Width, int Height) LastSize { get; private set; }
        public Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default) => Task.FromResult(JsonDocument.Parse("{\"status\":\"ready\"}"));
        public Task<MacScreenCaptureReply> CaptureAsync(int width = 960, int height = 540, uint? windowId = null, CancellationToken cancellationToken = default)
        { Captures++; LastSize = (width,height); return Task.FromResult(new MacScreenCaptureReply { Status = "frame", Width = 4, Height = 4, JpegBytes = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAEAAQDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD8qqKKKAP/2Q==") }); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
