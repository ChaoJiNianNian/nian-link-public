using System.Text.Json;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using Nian.Input.Control;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Fact]
    public async Task DataPairingAloneDoesNotOfferMacPreviewInput()
    {
        using var folder=new TestDirectory();
        await using var service=new MacCollaborationService(folder.Path,_=>Task.FromResult<DeskLinkPairingBundle?>(null),new FakeMedia());
        Assert.Null(service.CreatePreviewControlSession());
    }

    [Fact]
    public async Task VersionTwoUsesItsBoundDataPortWithoutInferringAnInputSession()
    {
        using var folder=new TestDirectory();await using var pairedControl=CreateControl(folder.Path);await pairedControl.StartAsync();
        await using var server=new CollaborationServer(new(){ProfileDirectory=folder.Path,Port=0});await server.StartAsync();
        var invitation=new DeviceInvitation("127.0.0.1",24803,new string('A',64),Convert.ToBase64String(new byte[32]),DateTimeOffset.UtcNow.AddMinutes(2),Permissions:DevicePermission.Control|DevicePermission.PreviewControl);
        var bundle=pairedControl.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac") with{Version=2,DataPort=server.Port,InputInvitation=invitation.ToCode()};
        await using var service=new MacCollaborationService(folder.Path,_=>Task.FromResult<DeskLinkPairingBundle?>(bundle),new FakeMedia(),1);
        await service.RefreshOnceAsync();Assert.True(service.Snapshot.IsOnline);Assert.Null(service.CreatePreviewControlSession());
    }

    [Fact]
    public async Task MacCaptureCarriesNativeProofAndControlRequestsOneSecondWithoutSavingIt()
    {
        using var folder=new TestDirectory();await using var pairedControl=CreateControl(folder.Path);await pairedControl.StartAsync();
        await using var server=new CollaborationServer(new(){ProfileDirectory=folder.Path,Port=0});await server.StartAsync();
        var bundle=pairedControl.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");var backend=new PreviewBackend();
        await using var service=new MacCollaborationService(folder.Path,_=>Task.FromResult<DeskLinkPairingBundle?>(bundle),new GeometryMedia(backend.Geometry),server.Port,null,backend);
        await service.SetPreviewEnabledAsync(true);await service.SetPreviewRefreshSecondsAsync(60);service.SetViewerVisibility(false,true);
        server.SetPreviewRequested(true);await service.RefreshOnceAsync();
        var frame=server.GetRemotePreview();Assert.Equal(backend.Geometry,frame.Geometry);Assert.NotNull(frame.ControlProof);
        Assert.True(PreviewFrameAuthority.ContentMatches(frame.ControlProof,frame.JpegBytes,frame.Width,frame.Height));
        await using var session=service.CreatePreviewControlSession();Assert.NotNull(session);
        session.ObserveDisplayedFrame(frame.ControlProof,frame.JpegBytes,frame.Width,frame.Height);await session.EnableAsync();
        Assert.Equal(1,server.LocalCaptureRefreshSeconds);Assert.Equal(60,service.Snapshot.Settings.PreviewRefreshSeconds);
        Assert.Equal(60,(await new MacPrivateStore(folder.Path).ReadAsync<MacCollaborationSettings>("collaboration.json",default))!.PreviewRefreshSeconds);
        await session.StopAsync("test-stop");Assert.Equal(60,server.LocalCaptureRefreshSeconds);
        session.ObserveDisplayedFrame(frame.ControlProof,frame.JpegBytes,frame.Width,frame.Height);await session.EnableAsync();
        service.SetViewerVisibility(false,false);Assert.False(session.IsActive);await session.StopAsync("test-finished");Assert.True(backend.Channel.Ends>=2);
    }

    private sealed class GeometryMedia(PreviewSourceGeometry geometry):IMacMediaBridge
    {
        private readonly FakeMedia inner=new();
        public Task<JsonDocument> RequestAsync(object request,CancellationToken cancellationToken=default)=>inner.RequestAsync(request,cancellationToken);
        public async Task<MacScreenCaptureReply> CaptureAsync(int width=960,int height=540,uint? windowId=null,CancellationToken cancellationToken=default)
            =>(await inner.CaptureAsync(width,height,windowId,cancellationToken)) with{Geometry=geometry};
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class PreviewBackend:INativePreviewControl
    {
        public PreviewSourceGeometry Geometry{get;}=new("mac-display-7","layout-1",0,0,1512,982,4,4);
        public PreviewChannel Channel{get;}=new();
        public PreviewFrameProof? BindCapturedFrame(byte[] jpeg,PreviewSourceGeometry? geometry)=>geometry is null?null:new PreviewFrameAuthority(()=>Geometry).Register(jpeg,geometry);
        public IPreviewControlChannel CreatePreviewChannel()=>Channel;
        public void InvalidateCapturedFrames(){}
    }
    private sealed class PreviewChannel:IPreviewControlChannel
    {
        public int Ends;
        public Task BeginAsync(CancellationToken token)=>Task.CompletedTask;
        public Task SendAsync(PreviewInputCommand command,CancellationToken token)=>Task.CompletedTask;
        public Task HeartbeatAsync(CancellationToken token)=>Task.CompletedTask;
        public Task EndAsync(CancellationToken token){Interlocked.Increment(ref Ends);return Task.CompletedTask;}
    }
}
