using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacPreviewAwareLinkServiceTests
{
    [Fact]
    public async Task APhysicalSwitchReleasesPreviewBeforeDelegatingTheOriginalSwitch()
    {
        var calls=new List<string>();var legacy=new Link(calls);var preview=new Preview(calls);
        await using var wrapper=new MacPreviewAwareLinkService(legacy,preview,_=>Task.FromResult<DeskLinkPairingBundle?>(null));
        var result=await wrapper.SwitchAsync("Win-PC");
        Assert.Equal(new[]{"preview-stop","switch:Win-PC"},calls);Assert.Same(legacy.Outcome,result);
    }
    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("accept-data-pairing")]
    public async Task ChangingTheLegacySessionAlwaysRevokesTemporaryPreviewFirst(string action)
    {
        var calls=new List<string>();var legacy=new Link(calls);var preview=new Preview(calls);
        await using var wrapper=new MacPreviewAwareLinkService(legacy,preview,_=>Task.FromResult<DeskLinkPairingBundle?>(null));
        if(action=="start")await wrapper.StartAsync(new());
        else if(action=="stop")await wrapper.StopAsync();
        else await wrapper.AcceptPairingAsync("pending-data-pairing");
        Assert.Equal(new[]{"preview-stop",action},calls);
    }
    [Fact]
    public async Task DataPairingAndSnapshotEventsNeverGrantPreviewPermissionOrReplaceDeskflowState()
    {
        var calls=new List<string>();var legacy=new Link(calls);var preview=new Preview(calls);
        await using var wrapper=new MacPreviewAwareLinkService(legacy,preview,_=>Task.FromResult<DeskLinkPairingBundle?>(null));
        MacLinkSnapshot? observed=null;wrapper.Changed+=snapshot=>observed=snapshot;
        var value=new MacLinkSnapshot{Backend=MacLinkBackend.Deskflow,ActiveComputer="Win-PC",HasApprovedPairing=true};
        legacy.Publish(value);await wrapper.PreviewPairingAsync("legacy-json");await wrapper.ConfirmPeerAsync("legacy-fingerprint",true);
        Assert.Same(value,observed);Assert.Same(value,preview.LastSnapshot);
        Assert.Equal(new[]{"observe","preview-data-pairing","confirm-data-peer"},calls);
    }
    [Fact]
    public async Task DisposalReleasesPreviewFirstAndDisposesItEvenIfLegacyCleanupFails()
    {
        var calls=new List<string>();var legacy=new Link(calls){FailDispose=true};var preview=new Preview(calls);
        var wrapper=new MacPreviewAwareLinkService(legacy,preview,_=>Task.FromResult<DeskLinkPairingBundle?>(null));
        await Assert.ThrowsAsync<IOException>(async()=>await wrapper.DisposeAsync());
        Assert.Equal(new[]{"preview-stop","legacy-dispose","preview-dispose"},calls);
        await wrapper.DisposeAsync();Assert.Equal(3,calls.Count);
    }
    private sealed class Preview(List<string> calls):IMacPreviewControlLifecycle
    {
        public MacLinkSnapshot? LastSnapshot;
        public void ObserveSnapshot(MacLinkSnapshot snapshot){LastSnapshot=snapshot;calls.Add("observe");}
        public void StopControl(string reason)=>calls.Add("preview-stop");
        public ValueTask DisposeAsync(){calls.Add("preview-dispose");return ValueTask.CompletedTask;}
    }
    private sealed class Link(List<string> calls):IMacLinkService
    {
        public bool FailDispose;
        public MacSwitchOutcome Outcome=new(true,"Win-PC",true,"verified","");
        public event Action<MacLinkSnapshot>? Changed;
        public void Publish(MacLinkSnapshot value)=>Changed?.Invoke(value);
        public Task<MacLinkSnapshot> RefreshAsync(CancellationToken token=default)=>Task.FromResult(new MacLinkSnapshot());
        public Task StartAsync(MacConnectionSettings settings,CancellationToken token=default){calls.Add("start");return Task.CompletedTask;}
        public Task StopAsync(CancellationToken token=default){calls.Add("stop");return Task.CompletedTask;}
        public Task ConfirmPeerAsync(string fingerprint,bool accept,CancellationToken token=default){calls.Add("confirm-data-peer");return Task.CompletedTask;}
        public Task<PairingPreview> PreviewPairingAsync(string json,CancellationToken token=default){calls.Add("preview-data-pairing");return Task.FromResult(new PairingPreview("id","127.0.0.1",24801,"Win-PC","My-Mac",new string('A',64)));}
        public Task AcceptPairingAsync(string id,CancellationToken token=default){calls.Add("accept-data-pairing");return Task.CompletedTask;}
        public Task<MacSwitchOutcome> SwitchAsync(string target,CancellationToken token=default){calls.Add("switch:"+target);return Task.FromResult(Outcome);}
        public ValueTask DisposeAsync(){calls.Add("legacy-dispose");return FailDispose?ValueTask.FromException(new IOException("cleanup failed")):ValueTask.CompletedTask;}
    }
}
