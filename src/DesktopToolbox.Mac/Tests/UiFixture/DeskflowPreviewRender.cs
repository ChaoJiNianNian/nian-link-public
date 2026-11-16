using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.Views;
using Nian.Input.Control;

internal static partial class Program
{
    /// <summary>Real handoff viewer and service factory; inert responses, fixture image, no desktop or input APIs.</summary>
    private static async Task RunDeskflowPreviewRenderAsync(IClassicDesktopStyleApplicationLifetime application)
    {
        var failures=new List<string>();var phases=new List<object>();var completed=false;
        var reportDirectory=Path.GetDirectoryName(output)!;Directory.CreateDirectory(reportDirectory);
        var prefix=Path.GetFileNameWithoutExtension(output);
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixture-desktop.jpg"));
        using var sourceBitmap=new Bitmap(new MemoryStream(bytes));
        var backend=new DeskflowRenderBackend();
        // Do not start polling, create a network client, write a profile, or request native permissions.
        await using var service=new MacCollaborationService(Path.Combine(reportDirectory,"fixture-profile-deskflow"),
            _=>Task.FromResult<DeskLinkPairingBundle?>(null),new FakeMedia(),24802,null,null,backend);
        service.UpdateState(current=>current with{IsOnline=true,Settings=current.Settings with{PreviewEnabled=true,PreviewRefreshSeconds=30}});
        service.SetViewerVisibility(false,true);
        await using var session=service.CreateLegacyDeskflowPreviewSessionForFixture()??throw new InvalidOperationException("The inert legacy fixture factory returned no session.");
        var effectiveCadence=typeof(MacCollaborationService).GetProperty("EffectivePreviewRefreshSeconds",BindingFlags.Instance|BindingFlags.NonPublic)!;
        int EffectiveSeconds()=>(int)effectiveCadence.GetValue(service)!;
        var viewer=new MacRemotePreviewWindow(session,"Mac","Windows")
        {
            Name="FixtureProductionDeskflowPreview",ShowActivated=false,ShowInTaskbar=false,
            WindowStartupLocation=WindowStartupLocation.Manual,Position=new PixelPoint(-5000,-5000)
        };
        var oldControl=typeof(MacRemotePreviewWindow).GetField("control",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var surface=typeof(MacRemotePreviewWindow).GetField("preview",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(viewer)!;
        var surfaceControl=typeof(MacPreviewSurface).GetField("control",BindingFlags.Instance|BindingFlags.NonPublic)!;
        PreviewFrame Frame()=>new(){State="live",Width=sourceBitmap.PixelSize.Width,Height=sourceBitmap.PixelSize.Height,JpegBytes=bytes,CapturedAtUtc=DateTimeOffset.UtcNow};
        async Task WaitFor(Func<bool> condition,string message)
        {
            for(var index=0;index<60&&!condition();index++)await Task.Delay(25);
            if(!condition())throw new InvalidOperationException(message);
        }
        async Task Capture(string name,bool dark,double width,double height,PreviewControlPhase expected,bool connected=true)
        {
            viewer.Width=width;viewer.Height=height;viewer.SetAppearance(dark);
            viewer.SetConnectionState(connected,connected?"只交接键鼠，屏幕保持不变":"对方设备已断开，请重新连接");
            viewer.ShowFrame(connected?Frame():PreviewFrame.Off("offline"),expected==PreviewControlPhase.Viewing?30:1);
            viewer.UpdateLayout();await Task.Delay(120);viewer.UpdateLayout();
            if(session.Phase!=expected)failures.Add(name+": unexpected handoff phase "+session.Phase);
            if(expected is PreviewControlPhase.Preparing or PreviewControlPhase.Controlling or PreviewControlPhase.Stopping)
                if(EffectiveSeconds()!=1)failures.Add(name+": the handoff must request the temporary one second cadence");
            if(oldControl.GetValue(viewer) is not null||surfaceControl.GetValue(surface) is not null)failures.Add(name+": coordinate control must not be constructed");
            var root=(Visual)viewer.Content!;var bounds=root.Bounds;
            if(bounds.Width<=0||bounds.Height<=0)throw new InvalidOperationException("Production handoff viewer has no rendered bounds.");
            var allButtons=root.GetVisualDescendants().OfType<Button>().ToArray();
            if(allButtons.Any(button=>button.IsEffectivelyVisible&&button.Name is "PreviewPairingAction" or "PreviewShortcuts"))
                failures.Add(name+": handoff mode must not show coordinate pairing or sent-key menus");
            var buttonResults=allButtons.Where(button=>button.IsEffectivelyVisible).Select(button=>
            {
                var point=button.TranslatePoint(new Point(0,0),root);
                var inside=point is{} origin&&origin.X>=-1&&origin.Y>=-1&&origin.X+button.Bounds.Width<=bounds.Width+1&&origin.Y+button.Bounds.Height<=bounds.Height+1;
                return new{name=button.Name,label=button.Content?.ToString(),enabled=button.IsEnabled,inside,bounds=button.Bounds.ToString()};
            }).ToArray();
            if(buttonResults.Any(button=>!button.inside))failures.Add(name+": toolbar button is outside the native content bounds");
            var action=allButtons.Single(button=>button.Name=="PreviewControlAction");
            if(expected==PreviewControlPhase.Stopping&&action.IsEnabled)failures.Add(name+": pending return must not offer a new start");
            if(!connected&&action.IsEnabled)failures.Add(name+": disconnected viewer must remain read only");
            var text=root.GetVisualDescendants().OfType<TextBlock>().Select(block=>block.Text??"").ToArray();
            if(expected==PreviewControlPhase.Controlling&&(!text.Any(value=>value.Contains("键鼠已交给 Windows · 屏幕留在 Mac",StringComparison.Ordinal))||
                !text.Any(value=>value.Contains("Ctrl+Alt+F12",StringComparison.Ordinal)&&value.Contains("鼠标沿远端位置移动",StringComparison.Ordinal))))
                failures.Add(name+": relative handoff and reliable return shortcut must be visible");
            var imagePath=Path.Combine(reportDirectory,prefix+"-"+name+".png");
            using(var image=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(bounds.Width),(int)Math.Ceiling(bounds.Height)),new Vector(96,96)))
            {image.Render(root);image.Save(imagePath);}
            phases.Add(new{name,dark,requestedWidth=width,requestedHeight=height,clientWidth=viewer.ClientSize.Width,clientHeight=viewer.ClientSize.Height,
                phase=session.Phase.ToString(),buttons=buttonResults,status=text,effectiveRefreshSeconds=EffectiveSeconds(),storedRefreshSeconds=service.Snapshot.Settings.PreviewRefreshSeconds,
                image=imagePath,realNativeWindow=viewer.TryGetPlatformHandle() is not null,productionViewer=viewer.GetType().FullName});
        }
        void Save()
        {
            var passed=completed&&failures.Count==0;
            var report=new{mode="deskflow-preview-native-render",fixture=true,productionViewer=true,productionServiceFactory=false,
                syntheticFrameOnly=true,inertTransportOnly=true,nativeInputTargetConstructed=false,
                coordinateControlConstructed=oldControl.GetValue(viewer) is not null||surfaceControl.GetValue(surface) is not null,
                frameSource="checked-in fixture-desktop.jpg",realDesktopCaptured=false,networkClientStarted=false,
                phases,backend.BeginCalls,backend.EndCalls,backend.RenewCalls,restoredRefreshSeconds=EffectiveSeconds(),failures,passed};
            File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine(JsonSerializer.Serialize(new{mode="deskflow-preview-native-render",phaseCount=phases.Count,failures,passed}));
        }
        viewer.Show();viewer.Position=new PixelPoint(-5000,-5000);
        try
        {
            await WaitFor(()=>viewer.Bounds.Width>0,"Production handoff viewer did not open.");
            await Capture("light-viewing",false,1040,680,PreviewControlPhase.Viewing);
            await Capture("dark-viewing",true,1040,680,PreviewControlPhase.Viewing);
            await Capture("light-narrow-viewing",false,420,520,PreviewControlPhase.Viewing);
            backend.BeginGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.ShowFrame(Frame(),1);var enabling=session.EnableAsync();
            await WaitFor(()=>backend.BeginCalls==1&&session.Phase==PreviewControlPhase.Preparing,"Preparing phase was not exposed by the handoff session.");
            await Capture("light-preparing",false,1040,680,PreviewControlPhase.Preparing);
            viewer.ShowFrame(Frame(),1);backend.BeginGate.TrySetResult();await enabling;
            await Capture("dark-controlling",true,1040,680,PreviewControlPhase.Controlling);
            await Capture("dark-narrow-controlling",true,420,520,PreviewControlPhase.Controlling);
            backend.EndGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopping=session.StopAsync("正在收回键鼠，请稍候…");
            await WaitFor(()=>backend.EndCalls==1&&session.Phase==PreviewControlPhase.Stopping,"Returning phase was not exposed by the handoff session.");
            await Capture("dark-stopping",true,1040,680,PreviewControlPhase.Stopping);
            backend.EndGate.TrySetResult();await stopping;
            await Capture("light-disconnected",false,1040,680,PreviewControlPhase.Viewing,false);
            if(EffectiveSeconds()!=30||service.Snapshot.Settings.PreviewRefreshSeconds!=30)failures.Add("Temporary capture did not restore the unchanged 30 second preference.");
            if(backend.BeginCalls!=1||backend.EndCalls!=1)failures.Add("Rendering created an unexpected handoff or return request.");
            completed=true;Save();
        }
        catch(Exception error){failures.Add(error.ToString());Save();}
        finally
        {
            backend.BeginGate?.TrySetCanceled();backend.EndGate?.TrySetResult();
            await session.StopAsync("Fixture finished");viewer.Close();application.Shutdown(completed&&failures.Count==0?0:2);
        }
    }
    private sealed class DeskflowRenderBackend:IDeskflowPreviewControl
    {
        internal TaskCompletionSource? BeginGate,EndGate;
        internal int BeginCalls,EndCalls,RenewCalls;
        private DeskflowPreviewState state=new(){ReturnShortcut="Ctrl+Alt+F12"};
        public bool PreviewHandoffSupported=>true;
        public string PreviewReturnShortcut=>"Ctrl+Alt+F12";
        public string PreviewHandoffHint=>"只交接键鼠，显示器保持不变";
        public async Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken cancellationToken=default)
        {
            Interlocked.Increment(ref BeginCalls);if(BeginGate is{} pending)await pending.Task.WaitAsync(cancellationToken);
            return state=new(){Id=Guid.NewGuid().ToString("N"),ViewerComputer="My-Mac",TargetComputer="Win-PC",Phase=DeskflowPreviewPhase.Active,ReturnShortcut=PreviewReturnShortcut,ExpiresAtUtc=DateTimeOffset.UtcNow.AddSeconds(8)};
        }
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id,CancellationToken cancellationToken=default)
        {Interlocked.Increment(ref RenewCalls);return Task.FromResult(state);}
        public async Task<DeskflowPreviewState> EndPreviewAsync(string id,CancellationToken cancellationToken=default)
        {
            Interlocked.Increment(ref EndCalls);if(EndGate is{} pending)await pending.Task.WaitAsync(cancellationToken);
            return state=new(){ReturnShortcut=PreviewReturnShortcut};
        }
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken=default)=>Task.FromResult(state);
    }
}
