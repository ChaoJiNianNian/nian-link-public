using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacPreviewAdmissionObserverTests
{
    private static MacLinkSnapshot State(DeskLinkState? remote=null,DeskLinkProcessState local=DeskLinkProcessState.Running)=>new()
    {
        Settings=new(){MacName="My-Mac",WindowsName="Win-PC"},PreviewControlState=remote,
        PreviewLocalCoreState=new(){ProcessState=local,ProcessId=local==DeskLinkProcessState.Running?22:null}
    };
    private static DeskLinkState Remote(string active,long revision=1)=>new()
    {ProcessState=DeskLinkProcessState.Running,NativeControlAvailable=true,ProcessId=12,Revision=revision,ActiveComputer=active};
    [Fact]public void UnrelatedUiAndClientEventsCannotFreshenTheLastWindowsFocusSample()
    {
        var clock=new Clock();var observer=new MacPreviewAdmissionObserver(clock);var remote=Remote("Win-PC");
        observer.Observe(State(remote),false);Assert.True(observer.Check(false,"Win-PC").Allowed);
        clock.Now=900;observer.Observe(State(remote) with{StatusMessage="unrelated client update"},false);
        clock.Now=1001;Assert.False(observer.Check(false,"Win-PC").Allowed);
    }
    [Fact]public void ANewAuthenticatedObservationCanChangeThePermittedDirection()
    {
        var observer=new MacPreviewAdmissionObserver(new Clock());observer.Observe(State(Remote("Win-PC")),false);
        Assert.True(observer.Check(false,"Win-PC").Allowed);Assert.False(observer.Check(true,"Win-PC").Allowed);
        Assert.True(observer.Observe(State(Remote("My-Mac",2)),false));
        Assert.True(observer.Check(true,"Win-PC").Allowed);Assert.False(observer.Check(false,"Win-PC").Allowed);
    }
    [Fact]public void FaultedOrConnectingClientsAreNeverMistakenForExplicitlyStopped()
    {
        var observer=new MacPreviewAdmissionObserver(new Clock());observer.Observe(State(local:DeskLinkProcessState.Stopped),false);
        Assert.True(observer.Check(true,"Win-PC").Allowed);
        observer.Observe(State(local:DeskLinkProcessState.Faulted),false);Assert.False(observer.Check(true,"Win-PC").Allowed);
        observer.Observe(State(local:DeskLinkProcessState.Starting),false);Assert.False(observer.Check(false,"Win-PC").Allowed);
    }
    [Fact]public void EveryStoppedObservationNeedsACurrentNoOtherCoreProbe()
    {
        var clock=new Clock();var observer=new MacPreviewAdmissionObserver(clock);observer.Observe(State(local:DeskLinkProcessState.Stopped),false);
        clock.Now=2000;Assert.False(observer.Check(false,"Win-PC").Allowed);
        observer.RefreshLocalProbe(false);Assert.True(observer.Check(false,"Win-PC").Allowed);
        observer.RefreshLocalProbe(true);Assert.False(observer.Check(false,"Win-PC").Allowed);
    }
    [Fact]public void AConflictChangeCannotRejuvenateAnOldRemoteObservation()
    {
        var clock=new Clock();var observer=new MacPreviewAdmissionObserver(clock);var snapshot=State(Remote("Win-PC"));
        observer.Observe(snapshot,false);clock.Now=1200;observer.Observe(snapshot,true);observer.Observe(snapshot,false);
        Assert.False(observer.Check(false,"Win-PC").Allowed);
        observer.Observe(snapshot with{PreviewControlState=Remote("Win-PC",2)},false);Assert.True(observer.Check(false,"Win-PC").Allowed);
    }
    [Fact]public void APreviewPeerWithAnotherDeviceNameCannotUseTheCurrentDataPicture()
    {
        var observer=new MacPreviewAdmissionObserver(new Clock());observer.Observe(State(local:DeskLinkProcessState.Stopped),false);
        Assert.False(observer.Check(true,"Other-PC").Allowed);
    }
    [Fact]public void InvalidatingBeforeASwitchCannotResurrectTheSameRemoteReference()
    {
        var observer=new MacPreviewAdmissionObserver(new Clock());var snapshot=State(Remote("Win-PC"));
        observer.Observe(snapshot,false);observer.Invalidate();observer.RefreshLocalProbe(false);observer.Observe(snapshot,false);
        Assert.False(observer.Check(false,"Win-PC").Allowed);
        observer.Observe(State(Remote("Win-PC",2)),false);Assert.True(observer.Check(false,"Win-PC").Allowed);
    }
    [Fact]public void InvalidationRequiresANewLegacyObservationBeforeStoppedCanBeUsedAgain()
    {
        var observer=new MacPreviewAdmissionObserver(new Clock());var snapshot=State(local:DeskLinkProcessState.Stopped);
        observer.Observe(snapshot,false);observer.Invalidate();observer.RefreshLocalProbe(false);
        Assert.False(observer.Check(false,"Win-PC").Allowed);
        observer.Observe(snapshot,false);Assert.True(observer.Check(false,"Win-PC").Allowed);
    }
    [Fact]public void ADelayedProcessProbeCannotOverwriteANewerLegacyObservation()
    {
        var observer=new MacPreviewAdmissionObserver(new Clock());var oldStopped=State(local:DeskLinkProcessState.Stopped);
        observer.Observe(oldStopped,false);var current=State(Remote("Win-PC"));observer.Observe(current,false);
        Assert.False(observer.RefreshLocalProbe(oldStopped,false));Assert.Same(current,observer.Snapshot);
        Assert.False(observer.Check(true,"Win-PC").Allowed);Assert.True(observer.Check(false,"Win-PC").Allowed);
    }
    private sealed class Clock:TimeProvider
    {public long Now;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>Now;}
}
