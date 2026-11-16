using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacDisplayPreviewSuppressionTests
{
    private static DeskLinkState State(string active,long revision,int pid=100,bool suppressed=false,long through=-1)=>new()
    {
        ProcessState=DeskLinkProcessState.Running,NativeControlAvailable=true,ProcessId=pid,Revision=revision,ActiveComputer=active,
        DisplaySuppressed=suppressed,DisplaySuppressedThroughRevision=through
    };
    private static MacDisplaySwitchCoordinator Create(Action onWrite)
    {
        var value=new MacDisplaySwitchCoordinator((_,_)=>{onWrite();return Task.FromResult(new MacLocalDisplaySwitchResult(true,"acceptedUnverified",null));});
        value.Configure(new(){Enabled=true,VendorId=0x61a9,ProductId=0x27b1,WindowsInput=15,MacInput=16},"Win-PC","My-Mac");
        return value;
    }
    [Fact]
    public async Task PreviewFocusTransitionsAdvanceTheBaselineWithoutAnyDisplayWrite()
    {
        var writes=0;using var value=Create(()=>writes++);
        await value.ObserveAsync(State("My-Mac",1));
        Assert.Null(await value.ObserveAsync(State("Win-PC",2,suppressed:true,through:2)));
        Assert.Null(await value.ObserveAsync(State("My-Mac",3,suppressed:true,through:3)));
        Assert.Equal(0,writes);
        Assert.Equal("acceptedUnverified",(await value.ObserveAsync(State("Win-PC",4,through:3)))?.VideoStatus);
        Assert.Equal(1,writes);
    }
    [Fact]
    public async Task ExplicitReturnAlsoRespectsTheMasterSuppressionFlag()
    {
        var writes=0;using var value=Create(()=>writes++);
        var result=await value.ReturnAfterConfirmedFocusAsync(State("Win-PC",2,suppressed:true,through:2));
        Assert.False(result.Attempted);Assert.Equal("suppressed",result.VideoStatus);Assert.False(result.NeedsAttention);Assert.Equal(0,writes);
    }
    [Fact]
    public async Task ACompletedPreviewWatermarkBlocksBothPollingAndExplicitLateFocus()
    {
        var writes=0;using var value=Create(()=>writes++);
        await value.ObserveAsync(State("My-Mac",5));
        Assert.Null(await value.ObserveAsync(State("Win-PC",6,through:6)));
        var result=await value.ReturnAfterConfirmedFocusAsync(State("Win-PC",6,through:6));
        Assert.Equal("suppressed",result.VideoStatus);Assert.Equal(0,writes);
    }
    [Fact]
    public async Task ADisconnectCannotEraseThePreviouslyAuthenticatedSuppressionWatermark()
    {
        var writes=0;using var value=Create(()=>writes++);
        await value.ObserveAsync(State("My-Mac",9,suppressed:true,through:10));
        value.ResetBaseline();
        Assert.Null(await value.ObserveAsync(State("Win-PC",10)));
        var explicitResult=await value.ReturnAfterConfirmedFocusAsync(State("Win-PC",10));
        Assert.Equal("suppressed",explicitResult.VideoStatus);Assert.Equal(0,writes);
        await value.ObserveAsync(State("My-Mac",11));
        await value.ObserveAsync(State("Win-PC",12));Assert.Equal(1,writes);
    }
    [Fact]
    public async Task LaterPacketsWithoutTheMarkerCannotLowerTheRememberedWatermark()
    {
        var writes=0;using var value=Create(()=>writes++);
        await value.ObserveAsync(State("My-Mac",20,suppressed:true,through:22));
        Assert.Null(await value.ObserveAsync(State("Win-PC",21)));
        Assert.Equal("suppressed",(await value.ReturnAfterConfirmedFocusAsync(State("Win-PC",22))).VideoStatus);
        Assert.Equal(0,writes);
    }
    [Fact]
    public async Task SuppressionWatermarksAreBoundToTheWindowsCoreProcess()
    {
        var writes=0;using var value=Create(()=>writes++);
        await value.ObserveAsync(State("My-Mac",50,suppressed:true,through:50));
        await value.ObserveAsync(State("My-Mac",0,pid:200));
        await value.ObserveAsync(State("Win-PC",1,pid:200));
        Assert.Equal(1,writes);
    }
    [Fact]
    public async Task AnAuthenticatedOrdinarySwitchCanRestoreVideoAtTheCompletedPreviewRevision()
    {
        var writes=0;using var value=Create(()=>writes++);
        await value.ObserveAsync(State("My-Mac",1));
        await value.ObserveAsync(State("Win-PC",2,suppressed:true,through:2));
        var completed=State("Win-PC",3,through:3);
        await value.ObserveAsync(completed);
        var result=await value.ReturnAfterConfirmedExplicitSwitchAsync(completed);
        Assert.True(result.Attempted);Assert.Equal("acceptedUnverified",result.VideoStatus);Assert.Equal(1,writes);
        Assert.Null(await value.ObserveAsync(State("Win-PC",2)));
        Assert.Equal("suppressed",(await value.ReturnAfterConfirmedFocusAsync(completed)).VideoStatus);
        Assert.Equal(1,writes);
    }
    [Fact]
    public async Task AnOrdinarySwitchCannotOverrideAnActiveDisplaySuppressionFlag()
    {
        var writes=0;using var value=Create(()=>writes++);
        var result=await value.ReturnAfterConfirmedExplicitSwitchAsync(State("Win-PC",4,suppressed:true,through:4));
        Assert.False(result.Attempted);Assert.Equal("suppressed",result.VideoStatus);Assert.Equal(0,writes);
    }
}
