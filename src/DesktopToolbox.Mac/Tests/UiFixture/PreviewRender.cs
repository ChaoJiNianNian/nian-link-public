using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Mac.Views;
using Nian.Input.Control;

internal static partial class Program
{
    /// <summary>Native Avalonia renderer with the production viewer, a static fixture JPEG and an inert channel.</summary>
    private static async Task RunPreviewRenderAsync(IClassicDesktopStyleApplicationLifetime application)
    {
        var failures=new List<string>();var phases=new List<object>();var completed=false;
        var reportDirectory=Path.GetDirectoryName(output)!;Directory.CreateDirectory(reportDirectory);
        var prefix=Path.GetFileNameWithoutExtension(output);
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixture-desktop.jpg"));
        using var sourceBitmap=new Bitmap(new MemoryStream(bytes));
        var source=new PreviewSourceGeometry("fixture-only-desktop","fixed-fixture-layout",0,0,1920,1080,sourceBitmap.PixelSize.Width,sourceBitmap.PixelSize.Height);
        var channel=new PreviewRenderChannel();
        var captureRequests=new List<bool>();
        await using var session=new PreviewControlSession(channel,(enabled,_)=>{captureRequests.Add(enabled);return Task.CompletedTask;});
        var viewer=new MacRemotePreviewWindow(session)
        {
            Name="FixtureProductionPreview",ShowActivated=false,ShowInTaskbar=false,
            WindowStartupLocation=WindowStartupLocation.Manual,Position=new PixelPoint(-5000,-5000)
        };
        PreviewFrame Frame()=>new()
        {
            State="live",Width=source.ImageWidth,Height=source.ImageHeight,JpegBytes=bytes,CapturedAtUtc=DateTimeOffset.UtcNow,Geometry=source,
            ControlProof=new(Guid.NewGuid().ToString("N"),Convert.ToHexString(SHA256.HashData(bytes)),source)
        };
        async Task WaitFor(Func<bool> condition,string message)
        {
            for(var index=0;index<60&&!condition();index++)await Task.Delay(25);
            if(!condition())throw new InvalidOperationException(message);
        }
        async Task Capture(string name,bool dark,double width,double height,PreviewControlPhase expected,bool connected=true)
        {
            viewer.Width=width;viewer.Height=height;viewer.SetAppearance(dark);viewer.SetConnectionState(connected,connected?"预览操作已连接，默认仅观看":"对方设备已断开，请重新连接");
            viewer.ShowFrame(connected?Frame():PreviewFrame.Off("offline"),30);
            viewer.UpdateLayout();await Task.Delay(120);viewer.UpdateLayout();
            if(session.Phase!=expected)failures.Add(name+": unexpected control phase "+session.Phase);
            var root=(Visual)viewer.Content!;var bounds=root.Bounds;
            if(bounds.Width<=0||bounds.Height<=0)throw new InvalidOperationException("Production preview has no rendered bounds.");
            var buttons=root.GetVisualDescendants().OfType<Button>().Where(button=>button.IsEffectivelyVisible).ToArray();
            var buttonResults=buttons.Select(button=>
            {
                var point=button.TranslatePoint(new Point(0,0),root);
                var inside=point is{} origin&&origin.X>=-1&&origin.Y>=-1&&origin.X+button.Bounds.Width<=bounds.Width+1&&origin.Y+button.Bounds.Height<=bounds.Height+1;
                return new{name=button.Name,label=button.Content?.ToString(),enabled=button.IsEnabled,inside,bounds=button.Bounds.ToString()};
            }).ToArray();
            if(buttonResults.Any(button=>!button.inside))failures.Add(name+": production toolbar button is outside the native content bounds");
            var action=buttons.Single(button=>button.Name=="PreviewControlAction");
            if(expected==PreviewControlPhase.Stopping&&action.IsEnabled)failures.Add(name+": stopping must not offer a new start");
            if(!connected&&action.IsEnabled)failures.Add(name+": disconnected viewer must remain read only");
            var imagePath=Path.Combine(reportDirectory,prefix+"-"+name+".png");
            using(var image=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(bounds.Width),(int)Math.Ceiling(bounds.Height)),new Vector(96,96)))
            {image.Render(root);image.Save(imagePath);}
            phases.Add(new{name,dark,requestedWidth=width,requestedHeight=height,clientWidth=viewer.ClientSize.Width,clientHeight=viewer.ClientSize.Height,
                phase=session.Phase.ToString(),buttons=buttonResults,image=imagePath,realNativeWindow=viewer.TryGetPlatformHandle() is not null,productionViewer=viewer.GetType().FullName});
        }
        void Save()
        {
            var passed=completed&&failures.Count==0;
            var report=new{mode="preview-native-render",fixture=true,productionViewer=true,syntheticFrameOnly=true,inertTransportOnly=true,nativeInputTargetConstructed=false,
                frameSource="checked-in fixture-desktop.jpg",realDesktopCaptured=false,phases,captureRequests,channel.BeginCalls,channel.EndCalls,channel.CommandCount,failures,passed};
            File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine(JsonSerializer.Serialize(new{mode="preview-native-render",phaseCount=phases.Count,failures,passed}));
        }
        viewer.Show();viewer.Position=new PixelPoint(-5000,-5000);
        try
        {
            await WaitFor(()=>viewer.Bounds.Width>0,"Production preview window did not open.");
            await Capture("light-viewing",false,1040,680,PreviewControlPhase.Viewing);
            await Capture("dark-viewing",true,1040,680,PreviewControlPhase.Viewing);
            await Capture("light-narrow-viewing",false,420,520,PreviewControlPhase.Viewing);
            channel.BeginGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.ShowFrame(Frame(),1);var enabling=session.EnableAsync();
            await WaitFor(()=>channel.BeginCalls==1&&session.Phase==PreviewControlPhase.Preparing,"Preparing phase was not exposed by the real session.");
            await Capture("light-preparing",false,1040,680,PreviewControlPhase.Preparing);
            viewer.ShowFrame(Frame(),1);channel.BeginGate.TrySetResult();await enabling;
            await Capture("dark-controlling",true,1040,680,PreviewControlPhase.Controlling);
            await Capture("dark-narrow-controlling",true,420,520,PreviewControlPhase.Controlling);
            channel.EndGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopping=session.StopAsync("正在释放操作，请稍候…");
            await WaitFor(()=>channel.EndCalls==1&&session.Phase==PreviewControlPhase.Stopping,"Stopping phase was not exposed by the real session.");
            await Capture("dark-stopping",true,1040,680,PreviewControlPhase.Stopping);
            channel.EndGate.TrySetResult();await stopping;
            await Capture("light-disconnected",false,1040,680,PreviewControlPhase.Viewing,false);
            if(channel.CommandCount!=0)failures.Add("Rendering must not issue any input command, including into the inert transport.");
            if(!captureRequests.SequenceEqual(new[]{true,false}))failures.Add("Temporary capture did not restore after stopping.");
            completed=true;Save();
        }
        catch(Exception error){failures.Add(error.ToString());Save();}
        finally
        {
            channel.BeginGate?.TrySetCanceled();channel.EndGate?.TrySetResult();
            await session.StopAsync("Fixture finished");viewer.Close();application.Shutdown(completed&&failures.Count==0?0:2);
        }
    }
    private sealed class PreviewRenderChannel:IPreviewControlChannel
    {
        internal TaskCompletionSource? BeginGate,EndGate;
        internal int BeginCalls,EndCalls,CommandCount;
        public Task BeginAsync(CancellationToken token){Interlocked.Increment(ref BeginCalls);return BeginGate?.Task.WaitAsync(token)??Task.CompletedTask;}
        public Task SendAsync(PreviewInputCommand command,CancellationToken token){Interlocked.Increment(ref CommandCount);return Task.CompletedTask;}
        public Task HeartbeatAsync(CancellationToken token)=>Task.CompletedTask;
        public Task EndAsync(CancellationToken token){Interlocked.Increment(ref EndCalls);return EndGate?.Task.WaitAsync(token)??Task.CompletedTask;}
    }
}
