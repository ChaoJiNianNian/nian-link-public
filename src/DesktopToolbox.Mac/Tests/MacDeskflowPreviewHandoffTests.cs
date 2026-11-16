using System.Reflection;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.ViewModels;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacDeskflowPreviewHandoffTests
{
    [Fact]
    public async Task ConnectedButPendingFingerprintCannotAcquireControl()
    {
        await using var setup=await Setup.CreateAsync();await using var session=NewSyntheticSession();
        InstallSession(setup.Service,session);
        SendSession(setup.Service,session,1,DeskLinkConnectionState.Connected,"ABCD");
        var snapshot=(MacLinkSnapshot)typeof(MacLinkService).GetMethod("Snapshot",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(setup.Service,null)!;
        Assert.False(snapshot.IsConnected);Assert.Equal("ABCD",snapshot.PendingPeerFingerprint);
        Assert.False(setup.Service.PreviewHandoffSupported);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>setup.Service.BeginPreviewAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>setup.Service.SwitchAsync("My-Mac"));
        Assert.Equal(0,setup.Preview.Begins);Assert.Equal(0,setup.OrdinarySwitches);
    }

    [Theory]
    [InlineData(DeskLinkConnectionState.Connected,"ABCD")]
    [InlineData(DeskLinkConnectionState.Disconnected,null)]
    public async Task UntrustedStateCannotRenewButDoesNotBlockPreviewReturn(DeskLinkConnectionState connection,string? pending)
    {
        await using var setup=await Setup.CreateAsync();await using var session=NewSyntheticSession();
        InstallSession(setup.Service,session);
        SendSession(setup.Service,session,1,DeskLinkConnectionState.Connected);
        var active=await setup.Service.BeginPreviewAsync();
        SendSession(setup.Service,session,2,connection,pending);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>setup.Service.RenewPreviewAsync(active.Id!));
        Assert.Equal(0,setup.Preview.Renews);
        var returned=await setup.Service.EndPreviewAsync(active.Id!);
        Assert.Equal(DeskflowPreviewPhase.Viewing,returned.Phase);Assert.Equal(1,setup.Preview.Ends);
    }
    [Fact]
    public async Task AuthoritativeDisconnectBeforeBlockedListenerRejectsBeginAndSwitch()
    {
        await using var setup=await Setup.CreateAsync();
        await using var session=NewSyntheticSession();
        InstallSession(setup.Service,session);
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        EventHandler<DeskLinkState> blocker=(sender,value)=>{if(value.ConnectionState==DeskLinkConnectionState.Disconnected){entered.Set();release.Wait(TimeSpan.FromSeconds(5));}};
        session.StateChanged+=blocker;
        session.StateChanged+=(sender,value)=>SendSession(setup.Service,session,value.SessionStateVersion,value.ConnectionState);
        UpdateSession(session,DeskLinkConnectionState.Connected);
        var delayed=Task.Run(()=>UpdateSession(session,DeskLinkConnectionState.Disconnected));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var snapshot=(MacLinkSnapshot)typeof(MacLinkService).GetMethod("Snapshot",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(setup.Service,null)!;
            Assert.False(snapshot.IsConnected);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>setup.Service.BeginPreviewAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(()=>setup.Service.SwitchAsync("My-Mac"));
            Assert.Equal(0,setup.Preview.Begins);Assert.Equal(0,setup.OrdinarySwitches);
        }
        finally {release.Set();await delayed;session.StateChanged-=blocker;}
    }

    private static void UpdateSession(DeskflowSession session,DeskLinkConnectionState connection)=>typeof(DeskflowSession).GetMethod("Update",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(session,[new Func<DeskLinkState,DeskLinkState>(value=>value with {ProcessState=DeskLinkProcessState.Running,ConnectionState=connection})]);
    [Fact]
    public async Task DisconnectWhileBeginReadsRemoteStateRejectsHandoffAndDelayedConnected()
    {
        await using var setup=await Setup.CreateAsync();
        await using var session=NewSyntheticSession();
        InstallSession(setup.Service,session);
        SendSession(setup.Service,session,1,DeskLinkConnectionState.Connected);
        setup.Preview.ReadStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Preview.ResumeRead=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending=setup.Service.BeginPreviewAsync();
        try
        {
            await setup.Preview.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            SendSession(setup.Service,session,2,DeskLinkConnectionState.Disconnected);
            SendSession(setup.Service,session,1,DeskLinkConnectionState.Connected);
        }
        finally { setup.Preview.ResumeRead.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>pending);
        Assert.Equal(0,setup.Preview.Begins);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>setup.Service.SwitchAsync("My-Mac"));
        Assert.Equal(0,setup.OrdinarySwitches);
    }

    private static DeskflowSession NewSyntheticSession()=>new(new DeskflowSessionOptions{EnginePath=Path.Combine(OwnedDirectory("missing"),"engine"),ProfileDirectory=OwnedDirectory("profile"),LocalName="My-Mac",RemoteName="Win-PC",Role=DeskLinkRole.Server});
    private static void InstallSession(MacLinkService owner,DeskflowSession session)=>typeof(MacLinkService).GetMethod("ReplaceSession",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(owner,[session]);
    private static void SendSession(MacLinkService owner,DeskflowSession session,long version,DeskLinkConnectionState state,string? pending=null)=>typeof(MacLinkService).GetMethod("OnSessionChanged",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(owner,[session,new DeskLinkState{SessionStateVersion=version,ProcessState=DeskLinkProcessState.Running,ConnectionState=state,PendingPeerFingerprint=pending}]);
    [Fact]
    public async Task AnUnpairedMacDoesNotCreateASecondIdentityOrOfferInputHandoff()
    {
        var directory=OwnedDirectory("unpaired");await using var service=new MacLinkService(directory);
        var backend=Assert.IsAssignableFrom<IDeskflowPreviewControl>(service);
        Assert.False(backend.PreviewHandoffSupported);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>backend.BeginPreviewAsync());
        Assert.False(Directory.Exists(Path.Combine(directory,"preview-control")));
    }
    [Fact]
    public async Task BeginRenewAndReturnUseTheExistingAuthenticatedPairWithoutOrdinarySwitch()
    {
        await using var setup=await Setup.CreateAsync();var backend=(IDeskflowPreviewControl)setup.Service;
        var before=await setup.Service.GetApprovedPairingAsync();
        var active=await backend.BeginPreviewAsync();Assert.Equal(DeskflowPreviewPhase.Active,active.Phase);
        Assert.Equal("My-Mac",active.ViewerComputer);Assert.Equal("Win-PC",active.TargetComputer);
        Assert.Equal(active.Id,(await backend.RenewPreviewAsync(active.Id!)).Id);
        Assert.Equal(DeskflowPreviewPhase.Viewing,(await backend.EndPreviewAsync(active.Id!)).Phase);
        Assert.Equal(1,setup.Preview.Begins);Assert.Equal(1,setup.Preview.Renews);Assert.Equal(1,setup.Preview.Ends);Assert.Equal(0,setup.OrdinarySwitches);
        Assert.Same(before,await setup.Service.GetApprovedPairingAsync());
        Assert.False(Directory.Exists(Path.Combine(setup.ClientDirectory,"preview-control")));
    }
    [Fact]
    public async Task ARefreshInFlightDoesNotFalselyDisconnectAnActivePreview()
    {
        await using var setup=await Setup.CreateAsync();
        var backend=(IDeskflowPreviewControl)setup.Service;
        var active=await backend.BeginPreviewAsync();
        Assert.True((await setup.Service.RefreshAsync()).ControlConnected);
        Assert.True(backend.PreviewHandoffSupported);

        setup.Preview.ReadStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Preview.ResumeRead=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh=setup.Service.RefreshAsync();
        try
        {
            await setup.Preview.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(refresh.IsCompleted);
            // A concurrent UI state push reads a Snapshot while the service gate is held.
            var inFlight=await setup.Service.RefreshAsync();
            Assert.True(inFlight.ControlConnected);
            Assert.True(inFlight.IsConnected);
            Assert.True(backend.PreviewHandoffSupported);
            Assert.Equal(active.Id,inFlight.InputPreview.Id);
            Assert.Equal(0,setup.Preview.Ends);
        }
        finally
        {
            setup.Preview.ResumeRead.TrySetResult();
            await refresh;
        }
        Assert.True((await setup.Service.RefreshAsync()).ControlConnected);
        await backend.EndPreviewAsync(active.Id!);
    }

    [Fact]
    public async Task AFailedRefreshStillRevokesPreviewReadiness()
    {
        await using var setup=await Setup.CreateAsync();
        var backend=(IDeskflowPreviewControl)setup.Service;
        Assert.True(backend.PreviewHandoffSupported);
        setup.Preview.FailStateReads=true;
        var failed=await setup.Service.RefreshAsync();
        Assert.False(failed.ControlConnected);
        Assert.False(backend.PreviewHandoffSupported);
        setup.Preview.FailStateReads=false;
        Assert.True((await setup.Service.RefreshAsync()).ControlConnected);
        Assert.True(backend.PreviewHandoffSupported);
    }
    [Fact]
    public async Task ACancelledInFlightRefreshDoesNotRetainStaleReadiness()
    {
        await using var setup=await Setup.CreateAsync();
        var backend=(IDeskflowPreviewControl)setup.Service;
        Assert.True(backend.PreviewHandoffSupported);
        setup.Preview.ReadStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Preview.ResumeRead=new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation=new CancellationTokenSource();
        var refresh=setup.Service.RefreshAsync(cancellation.Token);
        try
        {
            await setup.Preview.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(backend.PreviewHandoffSupported);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>refresh);
            Assert.False(backend.PreviewHandoffSupported);
        }
        finally {setup.Preview.ResumeRead.TrySetResult();}
    }
    [Fact]
    public async Task TheOrdinaryReturnButtonEndsTheCurrentPreviewWithoutVideoIntent()
    {
        await using var setup=await Setup.CreateAsync();var backend=(IDeskflowPreviewControl)setup.Service;
        await backend.BeginPreviewAsync();
        var result=await setup.Service.SwitchAsync("My-Mac");
        Assert.True(result.InputTransferred);Assert.Equal("suppressed",result.VideoStatus);Assert.Equal(1,setup.Preview.Ends);Assert.Equal(0,setup.OrdinarySwitches);
    }
    [Fact]
    public async Task AMacCanExplicitlyReturnTheWindowsViewersCurrentPreview()
    {
        await using var setup=await Setup.CreateAsync();setup.Preview.Activate("Win-PC","My-Mac");await setup.Service.RefreshAsync();
        var result=await setup.Service.SwitchAsync("Win-PC");
        Assert.True(result.InputTransferred);Assert.Equal("suppressed",result.VideoStatus);Assert.Equal(1,setup.Preview.Ends);Assert.Equal(0,setup.OrdinarySwitches);
    }
    [Fact]
    public async Task TheFloatingButtonReturnsOnlyTheCurrentMacTargetWithoutChangingDisplay()
    {
        await using var setup=await Setup.CreateAsync();setup.Preview.Activate("Win-PC","My-Mac");
        var snapshot=await setup.Service.RefreshAsync();var id=MacRemotePreviewReturn.CurrentId(snapshot);
        Assert.Equal(setup.Preview.Current.Id,id);
        var result=await setup.Service.ReturnRemotePreviewAsync(id!);
        Assert.Equal(DeskflowPreviewPhase.Viewing,result.Phase);Assert.Equal(1,setup.Preview.Ends);Assert.Equal(0,setup.OrdinarySwitches);
        Assert.Null(MacRemotePreviewReturn.CurrentId(await setup.Service.RefreshAsync()));
    }
    [Fact]
    public async Task FloatingReturnRejectsAStaleSessionOrAMacViewer()
    {
        await using var setup=await Setup.CreateAsync();setup.Preview.Activate("Win-PC","My-Mac");
        var old=setup.Preview.Current.Id!;await setup.Service.RefreshAsync();setup.Preview.Activate("My-Mac","Win-PC");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>setup.Service.ReturnRemotePreviewAsync(old));
        var snapshot=await setup.Service.RefreshAsync();Assert.Null(MacRemotePreviewReturn.CurrentId(snapshot));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>setup.Service.ReturnRemotePreviewAsync(setup.Preview.Current.Id!));
        Assert.Equal(0,setup.Preview.Ends);Assert.Equal(0,setup.OrdinarySwitches);
    }
    [Fact]
    public async Task FloatingButtonDisappearsWhenControlOrInputDisconnects()
    {
        await using var setup=await Setup.CreateAsync();setup.Preview.Activate("Win-PC","My-Mac");
        var snapshot=await setup.Service.RefreshAsync();Assert.NotNull(MacRemotePreviewReturn.CurrentId(snapshot));
        Assert.Null(MacRemotePreviewReturn.CurrentId(snapshot with {ControlConnected=false}));
        Assert.Null(MacRemotePreviewReturn.CurrentId(snapshot with {IsConnected=false}));
        Assert.Equal(snapshot.InputPreview.Id,MacRemotePreviewReturn.CurrentId(snapshot with {InputPreview=snapshot.InputPreview with {Phase=DeskflowPreviewPhase.Recovery}}));
    }
    [Fact]
    public async Task AFailedReturnRemainsAvailableAndCanBeRetriedWithoutVideoSwitching()
    {
        await using var setup=await Setup.CreateAsync();setup.Preview.Activate("Win-PC","My-Mac");
        var snapshot=await setup.Service.RefreshAsync();var id=MacRemotePreviewReturn.CurrentId(snapshot)!;
        setup.Preview.FailReturn=true;
        var recovery=await setup.Service.ReturnRemotePreviewAsync(id);
        Assert.Equal(DeskflowPreviewPhase.Recovery,recovery.Phase);
        Assert.Equal(id,MacRemotePreviewReturn.CurrentId(await setup.Service.RefreshAsync()));
        setup.Preview.FailReturn=false;
        var returned=await setup.Service.ReturnRemotePreviewAsync(id);
        Assert.Equal(DeskflowPreviewPhase.Viewing,returned.Phase);Assert.False(returned.HasSession);
        Assert.Equal(2,setup.Preview.Ends);Assert.Equal(0,setup.OrdinarySwitches);
    }
    [Fact]
    public async Task TheButtonReportsUnconfirmedReturnAndAllowsRetry()
    {
        await using var setup=await Setup.CreateAsync();setup.Preview.Activate("Win-PC","My-Mac");
        using var model=new MacLinkViewModel(setup.Service);await model.InitializeAsync();
        var id=model.RemotePreviewReturnId!;Assert.NotNull(id);setup.Preview.FailReturn=true;
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>model.ReturnRemotePreviewAsync(id));
        Assert.Contains("尚未确认",error.Message);Assert.Equal(id,model.RemotePreviewReturnId);
        setup.Preview.FailReturn=false;await model.ReturnRemotePreviewAsync(id);
        Assert.Null(model.RemotePreviewReturnId);Assert.Equal(2,setup.Preview.Ends);Assert.Equal(0,setup.OrdinarySwitches);
    }
    [Fact]
    public async Task AStaleIdCannotRenewOrEndAnotherInputSession()
    {
        await using var setup=await Setup.CreateAsync();var backend=(IDeskflowPreviewControl)setup.Service;
        var active=await backend.BeginPreviewAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>backend.RenewPreviewAsync(Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>backend.EndPreviewAsync(Guid.NewGuid().ToString("N")));
        Assert.Equal(active.Id,setup.Preview.Current.Id);Assert.Equal(0,setup.Preview.Renews);Assert.Equal(0,setup.Preview.Ends);
    }
    [Fact]
    public async Task ClosingAnOwnedPreviewReturnsInputWithoutRunningTheNormalStopDisplayRecovery()
    {
        await using var setup=await Setup.CreateAsync();await ((IDeskflowPreviewControl)setup.Service).BeginPreviewAsync();
        typeof(MacLinkService).GetField("stopRecoveryNeeded",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(setup.Service,true);
        await setup.Service.DisposeAsync();
        Assert.Equal(1,setup.Preview.Ends);Assert.Equal(0,setup.OrdinarySwitches);
    }
    [Fact]
    public async Task ClosingAReturnedPreviewAfterARefreshFailureDoesNotCreateOrdinaryDisplayIntent()
    {
        await using var setup=await Setup.CreateAsync();var backend=(IDeskflowPreviewControl)setup.Service;
        var active=await backend.BeginPreviewAsync();await backend.EndPreviewAsync(active.Id!);
        setup.Preview.FailStateReads=true;
        var failed=await setup.Service.RefreshAsync();Assert.False(failed.ControlConnected);
        setup.Preview.FailStateReads=false;
        typeof(MacLinkService).GetField("stopRecoveryNeeded",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(setup.Service,true);
        await setup.Service.DisposeAsync();
        Assert.Equal(1,setup.Preview.Ends);Assert.Equal(0,setup.OrdinarySwitches);
    }
    [Fact]
    public async Task AConfirmedOrdinarySwitchRestoresNormalStopRecoveryEvenAtTheOldPreviewRevision()
    {
        await using var setup=await Setup.CreateAsync();var backend=(IDeskflowPreviewControl)setup.Service;
        var active=await backend.BeginPreviewAsync();await backend.EndPreviewAsync(active.Id!);
        var before=setup.Preview.State().Revision;
        var ordinary=await setup.Service.SwitchAsync("My-Mac");
        Assert.True(ordinary.InputTransferred);Assert.Equal(before,setup.Preview.State().Revision);
        await setup.Service.RefreshAsync();
        typeof(MacLinkService).GetField("stopRecoveryNeeded",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(setup.Service,true);
        await setup.Service.DisposeAsync();
        Assert.Equal(1,setup.Preview.Ends);Assert.Equal(2,setup.OrdinarySwitches);
    }
    [Fact]
    public async Task AnAuthenticatedNewNormalFocusRestoresOrdinaryStopRecoveryAfterPreview()
    {
        await using var setup=await Setup.CreateAsync();var backend=(IDeskflowPreviewControl)setup.Service;
        var active=await backend.BeginPreviewAsync();await backend.EndPreviewAsync(active.Id!);
        var completed=setup.Preview.State();
        setup.Preview.OrdinarySwitch("Win-PC");setup.Preview.OrdinarySwitch("My-Mac");
        var normal=setup.Preview.State();
        Assert.Equal(completed.ProcessId,normal.ProcessId);Assert.True(normal.Revision>normal.DisplaySuppressedThroughRevision);
        Assert.False(normal.DisplaySuppressed);Assert.False(normal.InputPreview!.HasSession);Assert.Contains("My-Mac",normal.ConnectedComputers);
        var refreshed=await setup.Service.RefreshAsync();Assert.True(refreshed.ControlConnected);Assert.True(refreshed.IsConnected);Assert.Equal("My-Mac",refreshed.ActiveComputer);
        Assert.Equal(0,setup.OrdinarySwitches);
        typeof(MacLinkService).GetField("stopRecoveryNeeded",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(setup.Service,true);
        await setup.Service.DisposeAsync();
        Assert.Equal(1,setup.Preview.Ends);Assert.Equal(1,setup.OrdinarySwitches);
    }
    [Fact]
    public async Task ClosingAReturnedPreviewAtItsSuppressionRevisionDoesNotCreateOrdinaryDisplayIntent()
    {
        await using var setup=await Setup.CreateAsync();var backend=(IDeskflowPreviewControl)setup.Service;
        var active=await backend.BeginPreviewAsync();await backend.EndPreviewAsync(active.Id!);
        var completed=setup.Preview.State();Assert.Equal(completed.Revision,completed.DisplaySuppressedThroughRevision);
        await setup.Service.RefreshAsync();
        typeof(MacLinkService).GetField("stopRecoveryNeeded",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(setup.Service,true);
        await setup.Service.DisposeAsync();
        Assert.Equal(1,setup.Preview.Ends);Assert.Equal(0,setup.OrdinarySwitches);
    }
    private static string OwnedDirectory(string name)=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../..","test-profiles","preview-control","mac-handoff-"+name+"-"+Guid.NewGuid().ToString("N")));
    private sealed class Setup:IAsyncDisposable
    {
        internal readonly string ClientDirectory=OwnedDirectory("client");
        internal readonly PreviewBackend Preview=new();
        internal PairedControlServer Server=null!;
        internal MacLinkService Service=null!;
        internal int OrdinarySwitches;
        internal static async Task<Setup> CreateAsync()
        {
            var setup=new Setup();
            setup.Server=new(new(){ProfileDirectory=OwnedDirectory("server"),Port=0,AllowedTargets=["Win-PC","My-Mac"]},setup.Preview.ReadStateAsync,
                (target,_)=>{setup.OrdinarySwitches++;setup.Preview.OrdinarySwitch(target);return Task.FromResult(new DeskLinkSwitchResult{Ok=true,Target=target,ActiveComputer=target,InputTransferred=true});},setup.Preview);
            await setup.Server.StartAsync();
            var bundle=setup.Server.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");
            await new MacPrivateStore(setup.ClientDirectory).WriteAsync("paired-control.json",new{UserApproved=true,BundleJson=bundle.ToJson()},default);
            setup.Service=new(setup.ClientDirectory);
            typeof(MacLinkService).GetField("clientState",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(setup.Service,new DeskLinkState{ProcessState=DeskLinkProcessState.Running,ConnectionState=DeskLinkConnectionState.Connected,ProcessId=200});
            await setup.Service.RefreshAsync();return setup;
        }
        public async ValueTask DisposeAsync(){await Service.DisposeAsync();await Server.DisposeAsync();}
    }
    private sealed class PreviewBackend:IDeskflowPreviewControl
    {
        internal DeskflowPreviewState Current=new(){ReturnShortcut="Ctrl+Alt+F12"};
        internal int Begins,Renews,Ends;
        internal bool FailStateReads,FailReturn;
        internal TaskCompletionSource? ReadStarted,ResumeRead;
        private string active="My-Mac";private long revision=1,through=-1;
        public bool PreviewHandoffSupported=>true;
        public string PreviewReturnShortcut=>"Ctrl+Alt+F12";
        public string PreviewHandoffHint=>"只交接键鼠";
        internal DeskLinkState State()
        {
            if(FailStateReads)throw new IOException("Synthetic control read failure.");
            return new(){ProcessState=DeskLinkProcessState.Running,ConnectionState=DeskLinkConnectionState.Connected,NativeControlAvailable=true,ProcessId=100,
                ActiveComputer=active,Revision=revision,ConnectedComputers=["My-Mac"],PreviewHandoffSupported=true,InputPreview=Current,DisplaySuppressed=Current.HasSession,DisplaySuppressedThroughRevision=through};
        }
        internal async Task<DeskLinkState> ReadStateAsync(CancellationToken token)
        {
            if(ReadStarted is{} started&&ResumeRead is{} resume)
            {started.TrySetResult();await resume.Task.WaitAsync(token);}
            return State();
        }
        internal void Activate(string viewer,string target)
        {active=target;through=++revision;Current=new(){Id=Guid.NewGuid().ToString("N"),ViewerComputer=viewer,TargetComputer=target,Phase=DeskflowPreviewPhase.Active,ReturnShortcut=PreviewReturnShortcut,ExpiresAtUtc=DateTimeOffset.UtcNow.AddSeconds(10)};}
        internal void OrdinarySwitch(string target){if(active!=target)revision++;active=target;}
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken cancellationToken=default){Begins++;Activate("My-Mac","Win-PC");return Task.FromResult(Current);}
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id,CancellationToken cancellationToken=default){Renews++;Assert.Equal(Current.Id,id);return Task.FromResult(Current);}
        public Task<DeskflowPreviewState> EndPreviewAsync(string id,CancellationToken cancellationToken=default)
        {Ends++;Assert.Equal(Current.Id,id);if(FailReturn){Current=Current with {Phase=DeskflowPreviewPhase.Recovery,Message="尚未确认键鼠归还，请重试"};return Task.FromResult(Current);}active=Current.ViewerComputer;through=++revision;Current=new(){ViewerComputer=active,ReturnShortcut=PreviewReturnShortcut};return Task.FromResult(Current);}
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken=default)=>Task.FromResult(Current);
    }
}
