using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Fact]
    public async Task DeskflowHandoffTemporarilyRequestsOneSecondAndHidingRestoresTheStoredPreference()
    {
        var directory=HandoffDirectory();await using var control=CreateControl(directory);await control.StartAsync();
        await using var server=new CollaborationServer(new(){ProfileDirectory=directory,Port=0});await server.StartAsync();
        var bundle=control.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");var backend=new HandoffBackend();
        await using var service=new MacCollaborationService(directory,_=>Task.FromResult<DeskLinkPairingBundle?>(bundle),new FakeMedia(),server.Port,null,null,backend);
        await service.SetPreviewEnabledAsync(true);await service.SetPreviewRefreshSecondsAsync(60);service.SetViewerVisibility(false,true);await service.RefreshOnceAsync();
        await using var session=service.CreateLegacyDeskflowPreviewSessionForFixture();Assert.NotNull(session);
        var enabling=session.EnableAsync();session.ObserveFrame(HandoffFrame());await enabling;
        Assert.True(session.IsActive);Assert.Equal(1,server.LocalCaptureRefreshSeconds);Assert.Equal(60,service.Snapshot.Settings.PreviewRefreshSeconds);
        Assert.Equal(60,(await new MacPrivateStore(directory).ReadAsync<MacCollaborationSettings>("collaboration.json",default))!.PreviewRefreshSeconds);
        service.SetViewerVisibility(false,false);await session.StopAsync("test hide");
        Assert.False(session.IsActive);Assert.Equal(1,backend.Ends);Assert.Equal(60,server.LocalCaptureRefreshSeconds);Assert.Equal(0,backend.Disposes);
    }
    [Fact]
    public async Task DisposingTheCollaborationHostReturnsItsOwnedSessionButDoesNotDisposeTheSharedBackend()
    {
        var directory=HandoffDirectory();await using var control=CreateControl(directory);await control.StartAsync();
        await using var server=new CollaborationServer(new(){ProfileDirectory=directory,Port=0});await server.StartAsync();
        var bundle=control.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");var backend=new HandoffBackend();
        var service=new MacCollaborationService(directory,_=>Task.FromResult<DeskLinkPairingBundle?>(bundle),new FakeMedia(),server.Port,null,null,backend);
        await service.SetPreviewEnabledAsync(true);service.SetViewerVisibility(false,true);await service.RefreshOnceAsync();
        var session=service.CreateLegacyDeskflowPreviewSessionForFixture();Assert.NotNull(session);
        var enabling=session.EnableAsync();session.ObserveFrame(HandoffFrame());await enabling;
        await service.DisposeAsync();
        Assert.False(session.IsActive);Assert.Equal(1,backend.Ends);Assert.Equal(0,backend.Disposes);
    }
    [Fact]
    public async Task LosingTheApprovedDataPairingReturnsTheOwnedHandoff()
    {
        var directory=HandoffDirectory();await using var control=CreateControl(directory);await control.StartAsync();
        await using var server=new CollaborationServer(new(){ProfileDirectory=directory,Port=0});await server.StartAsync();
        DeskLinkPairingBundle? bundle=control.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");var backend=new HandoffBackend();
        await using var service=new MacCollaborationService(directory,_=>Task.FromResult<DeskLinkPairingBundle?>(bundle),new FakeMedia(),server.Port,null,null,backend);
        await service.SetPreviewEnabledAsync(true);service.SetViewerVisibility(false,true);await service.RefreshOnceAsync();
        await using var session=service.CreateLegacyDeskflowPreviewSessionForFixture();Assert.NotNull(session);
        var enabling=session.EnableAsync();session.ObserveFrame(HandoffFrame());await enabling;
        bundle=null;await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RefreshOnceAsync());await session.StopAsync("test disconnect");
        Assert.False(session.IsActive);Assert.Equal(1,backend.Ends);
    }
    [Fact]
    public async Task TheLegacyConstructorDoesNotAccidentallyOfferASecondHandoffBackend()
    {
        var directory=HandoffDirectory();await using var service=new MacCollaborationService(directory,_=>Task.FromResult<DeskLinkPairingBundle?>(null),new FakeMedia());
        Assert.Null(service.CreateDeskflowPreviewSession());
    }
    private static string HandoffDirectory()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../..","test-profiles","preview-control","mac-handoff-collab-"+Guid.NewGuid().ToString("N")));
    private static PreviewFrame HandoffFrame()=>new(){State="live",Width=1,Height=1,JpegBytes=[1],CapturedAtUtc=DateTimeOffset.UtcNow};
    private sealed class HandoffBackend:IDeskflowPreviewControl,IAsyncDisposable
    {
        private DeskflowPreviewState current=new(){ReturnShortcut="Ctrl+Alt+F12"};
        internal int Ends,Disposes;
        public bool PreviewHandoffSupported=>true;
        public string PreviewReturnShortcut=>"Ctrl+Alt+F12";
        public string PreviewHandoffHint=>"只交接键鼠，显示器保持不变";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken cancellationToken=default)
        {current=new(){Id=Guid.NewGuid().ToString("N"),ViewerComputer="My-Mac",TargetComputer="Win-PC",Phase=DeskflowPreviewPhase.Active,ReturnShortcut=PreviewReturnShortcut,ExpiresAtUtc=DateTimeOffset.UtcNow.AddSeconds(10)};return Task.FromResult(current);}
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id,CancellationToken cancellationToken=default)=>Task.FromResult(current);
        public Task<DeskflowPreviewState> EndPreviewAsync(string id,CancellationToken cancellationToken=default)
        {Interlocked.Increment(ref Ends);current=new(){ReturnShortcut=PreviewReturnShortcut};return Task.FromResult(current);}
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken=default)=>Task.FromResult(current);
        public ValueTask DisposeAsync(){Disposes++;return ValueTask.CompletedTask;}
    }
}
