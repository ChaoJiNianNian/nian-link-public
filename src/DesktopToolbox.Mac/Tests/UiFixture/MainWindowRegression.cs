using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.Views;
using DesktopToolbox.Mac.ViewModels;

internal static partial class Program
{
    /// <summary>Real MainWindow path, not a replacement Window hosting only the child control.</summary>
    private static async Task RunMainWindowAsync(IClassicDesktopStyleApplicationLifetime application)
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"main-window-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var link=new StableRecordingLink();var collaboration=new MacCollaborationService(directory,_=>Task.FromResult<DesktopToolbox.DeskLink.DeskLinkPairingBundle?>(null),new FakeMedia());
        var owner=new MainWindow(link,new FakeWireless(),collaboration){ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Position=new PixelPoint(-5000,-5000),RequestedThemeVariant=ThemeVariant.Dark};
        var control=(MacCollaborationControl)Field(owner,"collaborationControl")!;
        var handleField=typeof(MacWebViewHost).GetField("view",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var ready=typeof(MacWebViewHost).GetProperty("BridgeReady",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var observations=new List<object>();var failures=new List<string>();var phases=new List<object>();
        var transparencyChanges=0;var decorationsChanges=0;var handleChanges=0;var readyChanges=0;
        nint previousHandle=0;bool? previousReady=null;bool quitRequested=false,cleanup=false,closed=false,reportWritten=false;
        void Sample(string label)
        {
            var handle=(nint)handleField.GetValue(control)!;var bridgeReady=(bool)ready.GetValue(control)!;
            if(previousHandle!=0&&handle!=previousHandle)handleChanges++;
            if(previousReady.HasValue&&previousReady!=bridgeReady)readyChanges++;
            previousHandle=handle;previousReady=bridgeReady;
            observations.Add(new{label,handle=(long)handle,bridgeReady,compact=Field(owner,"compactMode"),visible=control.IsVisible,width=owner.Width,height=owner.Height});
        }
        void WriteReport()
        {
            var result=new{mode="real-main-window",fixture=true,syntheticServicesOnly=true,phases,observations,failures,standardQuitRequested=quitRequested,standardQuitClosed=closed&&!cleanup,linkStopCalls=link.StopCalls,linkDisposeCalls=link.DisposeCalls,passed=failures.Count==0&&closed&&!cleanup};
            Environment.ExitCode=failures.Count==0&&closed&&!cleanup?0:2;File.WriteAllText(output,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(result));reportWritten=true;
        }
        owner.Closed+=(_,_)=>{closed=true;if(quitRequested&&!cleanup)WriteReport();};
        owner.Show();owner.Position=new PixelPoint(-5000,-5000);
        try
        {
            for(var i=0;i<100&&(!(bool)ready.GetValue(control)!||Field(owner,"compactMode") is not true);i++)await Task.Delay(100);
            if(!(bool)ready.GetValue(control)!||Field(owner,"compactMode") is not true)throw new InvalidOperationException("Real MainWindow did not enter ready collaboration mode.");
            ((DispatcherTimer)Field(owner,"refreshTimer")!).Stop();((DispatcherTimer)Field(owner,"wirelessTimer")!).Stop();await Task.Delay(150);
            owner.PropertyChanged+=(_,change)=>{if(change.Property==TopLevel.TransparencyLevelHintProperty)transparencyChanges++;if(change.Property==Window.WindowDecorationsProperty)decorationsChanges++;};
            async Task StablePhase(string name)
            {
                var beforeTransparency=transparencyChanges;var beforeDecorations=decorationsChanges;var beforeHandles=handleChanges;var beforeReady=readyChanges;
                Sample(name+"-before");
                for(var i=0;i<10;i++){link.PublishStable();await Task.Delay(60);Sample(name+"-refresh-"+i);}
                var transparencyDelta=transparencyChanges-beforeTransparency;var decorationsDelta=decorationsChanges-beforeDecorations;var handlesDelta=handleChanges-beforeHandles;var readyDelta=readyChanges-beforeReady;
                phases.Add(new{name,refreshCount=10,transparencyChanges=transparencyDelta,decorationsChanges=decorationsDelta,nativeHandleChanges=handlesDelta,bridgeReadyChanges=readyDelta});
                if(transparencyDelta!=0||decorationsDelta!=0||handlesDelta!=0||readyDelta!=0)failures.Add(name+": unchanged snapshot changed native chrome or WK lifetime");
            }
            await StablePhase("initial-stable");
            var dispatch=typeof(MacCollaborationControl).GetMethod("OnWebRequest",BindingFlags.NonPublic|BindingFlags.Instance)!;
            MacWebRequest.TryParse("{\"id\":\"fixture-settings\",\"action\":\"connection.openSettings\",\"payload\":{}}",out var request);dispatch.Invoke(control,[request]);await Task.Delay(100);Sample("settings");
            if(control.IsVisible)failures.Add("Settings failed to hide native collaboration view");
            ((Button)Field(owner,"settingsToggle")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);
            for(var i=0;i<50&&(!(bool)ready.GetValue(control)!||!control.IsVisible);i++)await Task.Delay(100);
            if(!control.IsVisible)failures.Add("Return from settings failed to show collaboration view");
            previousReady=(bool)ready.GetValue(control)!;previousHandle=(nint)handleField.GetValue(control)!;
            await StablePhase("after-settings-stable");
            link.PublishUnpaired();
            var viewModel=(MacLinkViewModel)Field(owner,"model")!;
            await viewModel.PreviewPairingAsync("fixture-pairing");await Task.Delay(200);
            var pane=(ScrollViewer)Field(owner,"details")!;
            var fingerprint=(TextBlock)Field(owner,"pairingDetails")!;
            var fingerprintPoint=fingerprint.TranslatePoint(new Point(0,0),pane);
            var pairingInViewport=fingerprintPoint is {} point&&point.Y>=0&&point.Y<pane.Bounds.Height;
            if(!pairingInViewport)failures.Add("Imported fingerprint was not revealed in the real scroll viewport.");
            var retainedOffset=pane.Offset;
            for(var i=0;i<10;i++){link.PublishStable();await Task.Delay(30);}
            if(pane.Offset!=retainedOffset)failures.Add("Background refresh moved the pairing scroll position.");
            if(link.AcceptCalls!=0)failures.Add("Showing imported pairing must never approve it.");
            phases.Add(new{name="pairing-reveal",pairingInViewport,retainedScrollPosition=pane.Offset==retainedOffset,approvals=link.AcceptCalls});
            pane.Offset=new Vector(0,0);
            link.PairingFailure=new InvalidOperationException("fixture pairing connection unavailable");
            await viewModel.AcceptPairingAsync();await Task.Delay(200);
            var failureText=(TextBlock)Field(owner,"pairingFeedback")!;
            var failurePoint=failureText.TranslatePoint(new Point(0,0),pane);
            var failureInViewport=failurePoint is {} failureOrigin&&failureOrigin.Y>=0&&failureOrigin.Y<pane.Bounds.Height;
            if(!failureInViewport||!failureText.Text!.Contains("fixture pairing connection unavailable"))failures.Add("Pairing failure was not persistently visible beside the confirmation controls.");
            var failureOffset=pane.Offset;
            for(var i=0;i<10;i++){link.PublishStable();await Task.Delay(30);}
            if(pane.Offset!=failureOffset||!failureText.Text!.Contains("fixture pairing connection unavailable"))failures.Add("Refresh erased or scrolled away pairing failure feedback.");
            phases.Add(new{name="pairing-failure-feedback",failureInViewport,retainedScrollPosition=pane.Offset==failureOffset,fakeFailedAttempts=link.AcceptCalls});
            quitRequested=true;
            typeof(Window).GetMethod("HandleClosing",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(owner,[WindowCloseReason.ApplicationShutdown]);
            for(var i=0;i<50&&!closed;i++)await Task.Delay(100);
            if(!closed){failures.Add("ApplicationShutdown was canceled/hidden instead of closing and stopping services");WriteReport();cleanup=true;owner.RequestExit();}
            if(!reportWritten)WriteReport();
        }
        catch(Exception error){failures.Add(error.ToString());WriteReport();cleanup=true;owner.RequestExit();}
        finally
        {
            if(!closed){cleanup=true;owner.RequestExit();for(var i=0;i<20&&!closed;i++)await Task.Delay(100);}
            if(!reportWritten)WriteReport();
            application.Shutdown(failures.Count==0&&!cleanup?0:2);
        }
    }
    private static object? Field(object value,string name)=>value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(value);
    private sealed class StableRecordingLink:IMacLinkService
    {
        private MacLinkSnapshot state=new(){HasApprovedPairing=true,IsRunning=true,IsConnected=true,ControlConnected=true,ActiveComputer="My-Mac"};
        public int StopCalls,DisposeCalls,AcceptCalls;public event Action<MacLinkSnapshot>? Changed;
        public Exception? PairingFailure;
        public void PublishStable()=>Changed?.Invoke(state);
        public void PublishUnpaired(){state=new();PublishStable();}
        public Task<MacLinkSnapshot> RefreshAsync(CancellationToken cancellationToken=default)=>Task.FromResult(state);
        public Task StartAsync(MacConnectionSettings settings,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken=default){StopCalls++;return Task.CompletedTask;}
        public Task ConfirmPeerAsync(string fingerprint,bool accept,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task<PairingPreview> PreviewPairingAsync(string json,CancellationToken cancellationToken=default)=>Task.FromResult(new PairingPreview("fixture-preview","192.0.2.101",24931,"Fixture-Win","Fixture-Mac",new string('A',64)));
        public Task AcceptPairingAsync(string previewId,CancellationToken cancellationToken=default){AcceptCalls++;return PairingFailure is {} failure?Task.FromException(failure):Task.CompletedTask;}
        public Task<MacSwitchOutcome> SwitchAsync(string target,CancellationToken cancellationToken=default)=>Task.FromResult(new MacSwitchOutcome(true,target,true,"disabled",""));
        public ValueTask DisposeAsync(){DisposeCalls++;return ValueTask.CompletedTask;}
    }
}

