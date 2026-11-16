using System.Runtime.CompilerServices;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Fact]
    public async Task LiveDemandUploadsOnceSuppressesSlowCaptureAndOffCancelsWithoutRetryingSameDemand()
    {
        using var folder=new TestDirectory();await using var control=CreateControl(folder.Path);await control.StartAsync();
        await using var server=new CollaborationServer(new(){ProfileDirectory=folder.Path,Port=0});await server.StartAsync();
        var bundle=control.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");var media=new FakeMedia();var source=new ControlledLiveSource();
        await using var service=new MacCollaborationService(folder.Path,_=>Task.FromResult<DeskLinkPairingBundle?>(bundle),media,server.Port,null,null,new HandoffBackend(),source);
        await service.SetPreviewEnabledAsync(true);server.SetPreviewRequested(true,previewRefreshSeconds:1);
        await service.RefreshOnceAsync();Assert.True(server.LivePreview.RemotePeerSupportsLivePreview);
        var slowBefore=media.Captures;await using var lease=server.LivePreview.RequestRemoteCapture(new());
        await service.RefreshOnceAsync();await UntilAsync(()=>Volatile.Read(ref source.Starts)==1);
        await service.RefreshOnceAsync();Assert.Equal(1,source.Starts);Assert.Equal(slowBefore,media.Captures);
        await service.SetPreviewEnabledAsync(false);await UntilAsync(()=>Volatile.Read(ref source.Stops)==1);
        await service.SetPreviewEnabledAsync(true);await service.RefreshOnceAsync();
        await Task.Delay(200);Assert.Equal(1,source.Starts);
    }
    [Fact]
    public async Task LossOfPairingCancelsOwnedLiveUpload()
    {
        using var folder=new TestDirectory();await using var control=CreateControl(folder.Path);await control.StartAsync();
        await using var server=new CollaborationServer(new(){ProfileDirectory=folder.Path,Port=0});await server.StartAsync();
        DeskLinkPairingBundle? bundle=control.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");var source=new ControlledLiveSource();
        await using var service=new MacCollaborationService(folder.Path,_=>Task.FromResult<DeskLinkPairingBundle?>(bundle),new FakeMedia(),server.Port,null,null,new HandoffBackend(),source);
        await service.SetPreviewEnabledAsync(true);await service.RefreshOnceAsync();await using var lease=server.LivePreview.RequestRemoteCapture(new());
        await service.RefreshOnceAsync();await UntilAsync(()=>Volatile.Read(ref source.Starts)==1);
        bundle=null;await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RefreshOnceAsync());await UntilAsync(()=>Volatile.Read(ref source.Stops)==1);
    }
    [Fact]
    public async Task ProductionViewerUsesLiveSubscriptionAndKeepsPassiveRefreshPreference()
    {
        using var folder=new TestDirectory();await using var control=CreateControl(folder.Path);await control.StartAsync();
        await using var server=new CollaborationServer(new(){ProfileDirectory=folder.Path,Port=0});await server.StartAsync();
        var bundle=control.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");var media=new FakeMedia();var live=new ImageLiveSource((await media.CaptureAsync()).JpegBytes!);
        server.LivePreview.SetLocalCaptureProvider(live);server.SetLocalPreviewSource(new(Guid.NewGuid(),1,"enabled"));var backend=new HandoffBackend();
        await using var service=new MacCollaborationService(folder.Path,_=>Task.FromResult<DeskLinkPairingBundle?>(bundle),media,server.Port,null,null,backend,new ControlledLiveSource());
        await service.SetPreviewEnabledAsync(true);await service.SetPreviewRefreshSecondsAsync(30);service.SetViewerVisibility(false,true);await service.RefreshOnceAsync();
        await using var session=service.CreateDeskflowPreviewSession();Assert.NotNull(session);Assert.True(session.UsesLivePreview);Assert.True(session.Supported);
        var oldPoll=service.CapturePreviewPollVersion();
        session.LivePacketReceived+=(id,packet)=>session.NotifyLiveFramePresented(id,packet);
        await session.EnableAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(session.IsActive);Assert.Equal(30,service.Snapshot.Settings.PreviewRefreshSeconds);Assert.Equal(30,server.LocalCaptureRefreshSeconds);
        var oldRequest=service.Snapshot.PassivePreviewRequestId;
        await session.StopAsync("fixture return");Assert.False(session.IsActive);Assert.Equal(1,backend.Ends);
        Assert.Equal(30,service.Snapshot.Settings.PreviewRefreshSeconds);Assert.NotEqual(oldRequest,service.Snapshot.PassivePreviewRequestId);
        var before=service.Snapshot.Preview;
        Assert.False(service.TryApplyPolledPreview(PreviewFrame.Off("off"),oldPoll.Generation,oldPoll.ViewerRevision));
        Assert.Same(before,service.Snapshot.Preview);
        await service.RefreshOnceAsync();
        var currentRequest=server.LocalCaptureRequestId;Assert.Equal(service.Snapshot.PassivePreviewRequestId,currentRequest);
        var clockWentBack=DateTimeOffset.UtcNow.AddHours(-1);
        server.SetLocalPreview(new(){State="live",Width=4,Height=4,JpegBytes=(await media.CaptureAsync()).JpegBytes!,CapturedAtUtc=clockWentBack,
            RequestId=currentRequest,SourceStatus=service.Snapshot.RemotePreviewSourceStatus,CaptureSequence=1});
        await service.RefreshOnceAsync();Assert.Equal("live",service.Snapshot.Preview.State);Assert.Equal(clockWentBack,service.Snapshot.Preview.CapturedAtUtc);
        var oldSource=service.Snapshot.RemotePreviewSourceStatus!;
        server.SetLocalPreviewSource(oldSource with{Revision=oldSource.Revision+1,Availability="disabled"});
        server.SetLocalPreviewSource(oldSource with{Revision=oldSource.Revision+2,Availability="enabled"});
        await service.RefreshOnceAsync();Assert.Equal(oldSource.Revision+2,service.Snapshot.RemotePreviewSourceStatus!.Revision);
        Assert.NotEqual(currentRequest,service.Snapshot.PassivePreviewRequestId);
    }
    [Fact]
    public async Task DisablingPreviewWhileSlowCaptureIsInFlightCannotPublishItsLateFrame()
    {
        using var folder=new TestDirectory();await using var control=CreateControl(folder.Path);await control.StartAsync();
        await using var server=new CollaborationServer(new(){ProfileDirectory=folder.Path,Port=0});await server.StartAsync();
        var bundle=control.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");var media=new DelayedPreviewMedia();
        await using var service=new MacCollaborationService(folder.Path,_=>Task.FromResult<DeskLinkPairingBundle?>(bundle),media,server.Port);
        await service.SetPreviewEnabledAsync(true);server.SetPreviewRequested(true);
        var poll=service.RefreshOnceAsync();await media.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.SetPreviewEnabledAsync(false);media.Continue.TrySetResult();await poll;
        Assert.False(service.Snapshot.Settings.PreviewEnabled);Assert.NotEqual("live",server.GetRemotePreview().State);
        Assert.Empty(server.GetRemotePreview().JpegBytes);Assert.False(server.LocalCaptureRequested);
    }
    [Fact]
    public async Task SourceStatusAndBoundCaptureTrackLockAndManualOff()
    {
        using var folder=new TestDirectory();await using var control=CreateControl(folder.Path);await control.StartAsync();
        await using var server=new CollaborationServer(new(){ProfileDirectory=folder.Path,Port=0});await server.StartAsync();
        var bundle=control.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");var media=new StatusSourceMedia();
        await using var service=new MacCollaborationService(folder.Path,_=>Task.FromResult<DeskLinkPairingBundle?>(bundle),media,server.Port);
        await service.SetPreviewEnabledAsync(true);var request=Guid.NewGuid();server.SetPreviewRequested(true,requestId:request);
        await service.RefreshOnceAsync();Assert.Equal("enabled",server.RemotePreviewSourceStatus?.Availability);
        Assert.Equal(request,server.GetRemotePreview().RequestId);Assert.Equal(1,server.GetRemotePreview().CaptureSequence);Assert.Equal(server.RemotePreviewSourceStatus,server.GetRemotePreview().SourceStatus);
        media.Status="locked";await service.RefreshOnceAsync();Assert.Equal("locked",server.RemotePreviewSourceStatus?.Availability);Assert.NotEqual("live",server.GetRemotePreview().State);
        await service.SetPreviewEnabledAsync(false);Assert.Equal("disabled",server.RemotePreviewSourceStatus?.Availability);Assert.NotEqual("live",server.GetRemotePreview().State);
    }
    [Fact]
    public async Task TurningPreviewOffIsPersistedEvenIfThePeerDisconnectsDuringNotification()
    {
        using var folder=new TestDirectory();await using var control=CreateControl(folder.Path);await control.StartAsync();
        await using var server=new CollaborationServer(new(){ProfileDirectory=folder.Path,Port=0});await server.StartAsync();
        var bundle=control.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");
        await using var service=new MacCollaborationService(folder.Path,_=>Task.FromResult<DeskLinkPairingBundle?>(bundle),new FakeMedia(),server.Port);
        await service.SetPreviewEnabledAsync(true);await service.RefreshOnceAsync();await server.StopAsync();
        await Assert.ThrowsAnyAsync<Exception>(()=>service.SetPreviewEnabledAsync(false));
        var saved=await new MacPrivateStore(folder.Path).ReadAsync<MacCollaborationSettings>("collaboration.json",default);
        Assert.NotNull(saved);Assert.False(saved.PreviewEnabled);Assert.False(service.Snapshot.Settings.PreviewEnabled);
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        for(var i=0;i<150&&!condition();i++)await Task.Delay(20);
        Assert.True(condition(),"Expected asynchronous live-stream condition did not occur.");
    }
    private sealed class ControlledLiveSource:ILivePreviewCaptureProvider
    {
        internal int Starts,Stops;
        public bool IsSupported=>true;
        public async IAsyncEnumerable<LivePreviewCapturePacket> CaptureAsync(LivePreviewCaptureOptions options,[EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            Interlocked.Increment(ref Starts);
            try
            {
                yield return new(){Kind=LivePreviewCaptureKind.HealthyIdle,Sequence=1};
                await Task.Delay(Timeout.InfiniteTimeSpan,cancellationToken);
            }
            finally{Interlocked.Increment(ref Stops);}
        }
    }
    private sealed class ImageLiveSource(byte[] jpeg):ILivePreviewCaptureProvider
    {
        public bool IsSupported=>true;
        public async IAsyncEnumerable<LivePreviewCapturePacket> CaptureAsync(LivePreviewCaptureOptions options,[EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            yield return new(){Kind=LivePreviewCaptureKind.Image,Sequence=1,Width=4,Height=4,JpegBytes=jpeg,CapturedAtUtc=DateTimeOffset.UtcNow};
            await Task.Delay(Timeout.InfiniteTimeSpan,cancellationToken);
        }
    }
    private sealed class DelayedPreviewMedia:IMacMediaBridge
    {
        internal TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<JsonDocument> RequestAsync(object request,CancellationToken cancellationToken=default)=>Task.FromResult(JsonDocument.Parse("{\"status\":\"ready\"}"));
        public async Task<MacScreenCaptureReply> CaptureAsync(int width=960,int height=540,uint? windowId=null,CancellationToken cancellationToken=default)
        {
            Started.TrySetResult();await Continue.Task;
            return await new FakeMedia().CaptureAsync(width,height,windowId,CancellationToken.None);
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class StatusSourceMedia:IMacMediaBridge
    {
        internal string Status="ready";
        public Task<JsonDocument> RequestAsync(object request,CancellationToken cancellationToken=default)=>Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(new{status=Status})));
        public Task<MacScreenCaptureReply> CaptureAsync(int width=960,int height=540,uint? windowId=null,CancellationToken cancellationToken=default)=>new FakeMedia().CaptureAsync(width,height,windowId,cancellationToken);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
