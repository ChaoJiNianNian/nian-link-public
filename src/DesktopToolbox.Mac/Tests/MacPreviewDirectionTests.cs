using System.Text.Json;
using System.Runtime.CompilerServices;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Theory]
    [InlineData("{}", false, false)]
    [InlineData("{\"PreviewEnabled\":true}", true, true)]
    [InlineData("{\"PreviewEnabled\":false}", false, false)]
    [InlineData("{\"PreviewEnabled\":true,\"ShareScreenEnabled\":false}", true, false)]
    public void PreviewSettingsMigrateOnlyExplicitOldConsent(string json, bool view, bool share)
    {
        var settings = JsonSerializer.Deserialize<MacCollaborationSettings>(json)!;
        Assert.Equal(view, settings.PreviewEnabled);
        Assert.Equal(share, settings.ShareScreenEnabled);
        var roundTrip = JsonSerializer.Deserialize<MacCollaborationSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(view, roundTrip.PreviewEnabled);
        Assert.Equal(share, roundTrip.ShareScreenEnabled);
    }

    [Fact]
    public async Task ViewingAndSharingAreIndependentlyEnabledAndDisabled()
    {
        using var folder = new TestDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var media = new FakeMedia(); await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        server.SetPreviewRequested(true);
        service.SetViewerVisibility(false, true);

        await service.SetViewPreviewEnabledAsync(true); await service.RefreshOnceAsync();
        Assert.True(server.LocalCaptureRequested);
        Assert.Equal(0, media.Captures);
        Assert.Empty(server.GetRemotePreview().JpegBytes);

        await service.SetShareScreenEnabledAsync(true); await service.RefreshOnceAsync();
        Assert.True(server.LocalCaptureRequested);
        Assert.Equal(1, media.Captures);
        Assert.Equal("live", server.GetRemotePreview().State);

        await service.SetShareScreenEnabledAsync(false); await service.RefreshOnceAsync();
        Assert.True(server.LocalCaptureRequested);
        Assert.Empty(server.GetRemotePreview().JpegBytes);
        Assert.Equal(1, media.Captures);

        await service.SetShareScreenEnabledAsync(true); await service.RefreshOnceAsync();
        Assert.Equal("live", server.GetRemotePreview().State);
        await service.SetViewPreviewEnabledAsync(false); await service.RefreshOnceAsync();
        Assert.False(server.LocalCaptureRequested);
        Assert.Equal("live", server.GetRemotePreview().State);
    }

    [Fact]
    public async Task TurningOffViewingDoesNotCancelAnInFlightMacScreenShare()
    {
        using var folder = new TestDirectory(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"); var media = new DelayedPreviewMedia();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        await service.SetViewPreviewEnabledAsync(true); await service.SetShareScreenEnabledAsync(true);
        server.SetPreviewRequested(true);
        var poll = service.RefreshOnceAsync(); await media.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.SetViewPreviewEnabledAsync(false);
        media.Continue.TrySetResult(); await poll;
        Assert.False(service.Snapshot.Settings.PreviewEnabled); Assert.True(service.Snapshot.Settings.ShareScreenEnabled);
        Assert.Equal("live", server.GetRemotePreview().State); Assert.False(server.LocalCaptureRequested);
    }

    [Fact]
    public async Task TurningOffSharingDiscardsAnInFlightMacFrameWithoutDisablingViewing()
    {
        using var folder = new TestDirectory(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"); var media = new DelayedPreviewMedia();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        await service.SetViewPreviewEnabledAsync(true); await service.SetShareScreenEnabledAsync(true);
        server.SetPreviewRequested(true);
        var poll = service.RefreshOnceAsync(); await media.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.SetShareScreenEnabledAsync(false);
        media.Continue.TrySetResult(); await poll;
        Assert.True(service.Snapshot.Settings.PreviewEnabled); Assert.False(service.Snapshot.Settings.ShareScreenEnabled);
        Assert.NotEqual("live", server.GetRemotePreview().State); Assert.Empty(server.GetRemotePreview().JpegBytes);
        Assert.True(server.LocalCaptureRequested);
    }

    [Fact]
    public async Task LegacyOffDisablesBothDirectionsEvenIfPeerIsUnavailable()
    {
        using var folder = new TestDirectory(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port);
        await service.SetPreviewEnabledAsync(true); await service.RefreshOnceAsync(); await server.StopAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => service.SetPreviewEnabledAsync(false));
        Assert.False(service.Snapshot.Settings.PreviewEnabled); Assert.False(service.Snapshot.Settings.ShareScreenEnabled);
        var saved = await new MacPrivateStore(folder.Path).ReadAsync<MacCollaborationSettings>("collaboration.json", default);
        Assert.NotNull(saved); Assert.False(saved.PreviewEnabled); Assert.False(saved.ShareScreenEnabled);
    }

    [Fact]
    public async Task ReaffirmingShareAndDisablingViewKeepAnActiveLiveUpload()
    {
        using var folder = new TestDirectory(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"); var source = new ControlledLiveSource();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port, null, null, new HandoffBackend(), source);
        await service.SetViewPreviewEnabledAsync(true); await service.SetShareScreenEnabledAsync(true); await service.RefreshOnceAsync();
        await using var lease = server.LivePreview.RequestRemoteCapture(new());
        await service.RefreshOnceAsync(); await UntilAsync(() => Volatile.Read(ref source.Starts) == 1);
        await service.SetShareScreenEnabledAsync(true);
        await service.SetViewPreviewEnabledAsync(false);
        await service.RefreshOnceAsync();
        Assert.Equal(1, Volatile.Read(ref source.Starts)); Assert.Equal(0, Volatile.Read(ref source.Stops));
        await service.SetShareScreenEnabledAsync(false); await UntilAsync(() => Volatile.Read(ref source.Stops) == 1);
    }

    [Fact]
    public async Task SharingCanBeTurnedOffWhilePermissionRequestIsPending()
    {
        using var folder = new TestDirectory(); var media = new PendingPermissionMedia();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), media);
        using var cancel = new CancellationTokenSource();
        var enabling = service.SetShareScreenEnabledAsync(true, cancel.Token);
        await media.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disabling = service.SetShareScreenEnabledAsync(false);
        var completed = await Task.WhenAny(disabling, Task.Delay(1000)) == disabling;
        cancel.Cancel();
        try { await enabling; } catch (OperationCanceledException) { }
        await disabling;
        Assert.True(completed, "Disabling sharing must not wait for an outstanding system permission request.");
        Assert.False(service.Snapshot.Settings.ShareScreenEnabled);
    }
    private sealed class PendingPermissionMedia : IMacMediaBridge
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default)
        {
            using var body = JsonDocument.Parse(JsonSerializer.Serialize(request));
            if (body.RootElement.GetProperty("action").GetString() == "permission")
            {
                Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return JsonDocument.Parse("{\"status\":\"ready\"}");
        }
        public Task<MacScreenCaptureReply> CaptureAsync(int width = 960, int height = 540, uint? windowId = null, CancellationToken cancellationToken = default) => Task.FromResult(new MacScreenCaptureReply());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task LegacyOffRevokesViewingWhileShareCleanupIsStillBlocked()
    {
        using var folder = new TestDirectory(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"); var source = new SlowShutdownLiveSource();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port, null, null, new HandoffBackend(), source);
        await service.SetPreviewEnabledAsync(true); await service.RefreshOnceAsync();
        await using var lease = server.LivePreview.RequestRemoteCapture(new());
        await service.RefreshOnceAsync(); await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var bothOff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += state => { if (!state.Settings.PreviewEnabled && !state.Settings.ShareScreenEnabled) bothOff.TrySetResult(); };
        var stopping = service.SetPreviewEnabledAsync(false);
        bool completed;
        try { completed = await Task.WhenAny(bothOff.Task, Task.Delay(1000)) == bothOff.Task; }
        finally { source.Release.TrySetResult(); }
        await stopping;
        Assert.True(completed, "Both local permissions must be revoked before waiting for slow share cleanup.");
    }
    private sealed class SlowShutdownLiveSource : ILivePreviewCaptureProvider
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsSupported => true;
        public async IAsyncEnumerable<LivePreviewCapturePacket> CaptureAsync(LivePreviewCaptureOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            yield return new() { Kind = LivePreviewCaptureKind.HealthyIdle, Sequence = 1 };
            await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    [Fact]
    public async Task RevokingShareWhilePollIsWaitingCannotStartMacCapture()
    {
        using var folder = new TestDirectory(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"); var media = new FakeMedia();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        await service.SetViewPreviewEnabledAsync(true); await service.SetShareScreenEnabledAsync(true); server.SetPreviewRequested(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.TestBeforeMacCapture = async () => { entered.TrySetResult(); await resume.Task; };
        var poll = service.RefreshOnceAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.SetShareScreenEnabledAsync(false); resume.TrySetResult(); await poll;
        Assert.Equal(0, media.Captures); Assert.False(service.Snapshot.Settings.ShareScreenEnabled);
        Assert.NotEqual("live", server.GetRemotePreview().State);
    }

    [Fact]
    public async Task RevokingShareDuringOldLiveStreamStopCannotAuthorizeReplacement()
    {
        using var folder = new TestDirectory(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"); var source = new ControlledLiveSource();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port, null, null, new HandoffBackend(), source);
        await service.SetShareScreenEnabledAsync(true); await service.RefreshOnceAsync();
        var first = server.LivePreview.RequestRemoteCapture(new()); await service.RefreshOnceAsync();
        await UntilAsync(() => Volatile.Read(ref source.Starts) == 1);
        await first.DisposeAsync(); await using var second = server.LivePreview.RequestRemoteCapture(new());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorized = 0;
        service.TestAfterLiveUploadStopped = async () => { entered.TrySetResult(); await resume.Task; };
        service.TestLiveUploadAuthorized = () => Interlocked.Increment(ref authorized);
        var poll = service.RefreshOnceAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += state => { if (!state.Settings.ShareScreenEnabled) disabled.TrySetResult(); };
        var stopping = service.SetShareScreenEnabledAsync(false);
        await disabled.Task.WaitAsync(TimeSpan.FromSeconds(5)); resume.TrySetResult(); await poll; await stopping;
        Assert.Equal(0, Volatile.Read(ref authorized)); Assert.False(service.Snapshot.Settings.ShareScreenEnabled);
    }

    [Theory]
    [InlineData("{\"previewEnabled\":true}")]
    [InlineData("{\"previewEnabled\":true,\"shareScreenEnabled\":true}")]
    public async Task MultiDeviceSessionDoesNotAutomaticallyShareOldOrNewSavedConsent(string settings)
    {
        using var folder = new TestDirectory(); await File.WriteAllTextAsync(Path.Combine(folder.Path, "collaboration.json"), settings);
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), new FakeMedia(), 1, null, null, null, null, usePairedDataPort: true);
        await service.StartAsync();
        Assert.True(service.Snapshot.Settings.PreviewEnabled); Assert.False(service.Snapshot.Settings.ShareScreenEnabled);
    }

    [Fact]
    public async Task FailedShareRevocationRemainsOffAfterMultiDeviceRestartAndCanBeRetried()
    {
        using var folder = new TestDirectory();
        await using (var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), new FakeMedia(), 1, null, null, null, null, usePairedDataPort: true))
        {
            await service.StartAsync(); await service.SetShareScreenEnabledAsync(true);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.TestBeforeShareSave = async () => { entered.TrySetResult(); await resume.Task; };
            using var cancel = new CancellationTokenSource();
            var stopping = service.SetShareScreenEnabledAsync(false, cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); resume.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
            Assert.False(service.Snapshot.Settings.ShareScreenEnabled);
            var diskBeforeRetry = await new MacPrivateStore(folder.Path).ReadAsync<MacCollaborationSettings>("collaboration.json", default);
            Assert.True(diskBeforeRetry!.ShareScreenEnabled);
            service.TestBeforeShareSave = null;
            await service.SetShareScreenEnabledAsync(false);
            var diskAfterRetry = await new MacPrivateStore(folder.Path).ReadAsync<MacCollaborationSettings>("collaboration.json", default);
            Assert.False(diskAfterRetry!.ShareScreenEnabled);
        }
        await using var restarted = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), new FakeMedia(), 1, null, null, null, null, usePairedDataPort: true);
        await restarted.StartAsync(); Assert.False(restarted.Snapshot.Settings.ShareScreenEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiDeviceRestartFailsClosedAfterRevocationWriteFailsOrIsCanceled(bool canceled)
    {
        using var folder = new TestDirectory();
        await using (var original = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), new FakeMedia(), 1, null, null, null, null, usePairedDataPort: true))
        {
            await original.StartAsync(); await original.SetViewPreviewEnabledAsync(true); await original.SetShareScreenEnabledAsync(true);
            original.TestBeforeShareSave = () => canceled ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(new IOException("simulated isolated store failure"));
            await Assert.ThrowsAnyAsync<Exception>(() => original.SetShareScreenEnabledAsync(false));
            Assert.False(original.Snapshot.Settings.ShareScreenEnabled);
            Assert.True((await new MacPrivateStore(folder.Path).ReadAsync<MacCollaborationSettings>("collaboration.json", default))!.ShareScreenEnabled);
        }
        await using var restarted = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), new FakeMedia(), 1, null, null, null, null, usePairedDataPort: true);
        await restarted.StartAsync(); Assert.True(restarted.Snapshot.Settings.PreviewEnabled); Assert.False(restarted.Snapshot.Settings.ShareScreenEnabled);
        await restarted.SetShareScreenEnabledAsync(false);
        Assert.False((await new MacPrivateStore(folder.Path).ReadAsync<MacCollaborationSettings>("collaboration.json", default))!.ShareScreenEnabled);
    }
}
