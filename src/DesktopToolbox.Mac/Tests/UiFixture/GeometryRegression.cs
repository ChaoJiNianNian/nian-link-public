using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.Views;

internal static partial class Program
{
    [DllImport("libDeskLinkWeb.dylib",CallingConvention=CallingConvention.Cdecl)]private static extern void nian_web_probe_geometry(nint view,[MarshalAs(UnmanagedType.LPUTF8Str)]string command,[MarshalAs(UnmanagedType.LPUTF8Str)]string path);
    private static async Task RunGeometryAsync(IClassicDesktopStyleApplicationLifetime application)
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"geometry-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"collaboration.json"),JsonSerializer.Serialize(new MacCollaborationSettings{PreviewEnabled=true}));
        var collaboration=new MacCollaborationService(directory,_=>Task.FromResult<DesktopToolbox.DeskLink.DeskLinkPairingBundle?>(null),new FakeMedia());
        var owner=new MainWindow(new StableRecordingLink(),new FakeWireless(),collaboration){ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Position=new PixelPoint(-5000,-5000),RequestedThemeVariant=ThemeVariant.Dark};
        var control=(MacCollaborationControl)Field(owner,"collaborationControl")!;
        var handleField=typeof(MacWebViewHost).GetField("view",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var ready=typeof(MacWebViewHost).GetProperty("BridgeReady",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var phases=new List<object>();var failures=new List<string>();var completed=false;var serial=0;
        var resultDirectory=Path.GetDirectoryName(output)!;Directory.CreateDirectory(resultDirectory);
        void PushSynthetic(bool? previewEnabled=null)
        {
            var state=collaboration.Snapshot;
            var next=state with{IsOnline=true,Settings=state.Settings with{PreviewEnabled=previewEnabled??state.Settings.PreviewEnabled},Preview=new PreviewFrame{State="live",Width=960,Height=540,JpegBytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixture-desktop.jpg")),CapturedAtUtc=DateTimeOffset.UtcNow}};
            typeof(MacCollaborationService).GetField("snapshot",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(collaboration,next);
            typeof(MacCollaborationService).GetMethod("Publish",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(collaboration,null);
        }
        async Task<JsonElement> ReadGeometry(string name,string command="inspect")
        {
            var path=Path.Combine(resultDirectory,$"geometry-{serial++:00}-{name}.json");if(File.Exists(path))File.Delete(path);
            nian_web_probe_geometry((nint)handleField.GetValue(control)!,command,path);
            for(var i=0;i<100&&!File.Exists(path);i++)await Task.Delay(50);
            if(!File.Exists(path))throw new TimeoutException("Native geometry did not complete: "+name);
            using var json=JsonDocument.Parse(File.ReadAllText(path));return json.RootElement.Clone();
        }
        static double Number(JsonElement item,string key)=>item.GetProperty(key).GetDouble();
        async Task Check(string name)
        {
            PushSynthetic();await Task.Delay(100);
            // Off-screen WebKit may suspend requestAnimationFrame. Drive the exact production
            // resize message from measured DOM bounds, rather than pretending the old client
            // size covers an enabled preview or an expanded panel.
            var measure=await ReadGeometry(name+"-measure");var measuredCard=measure.GetProperty("dom").GetProperty("card");
            var resizeJson=JsonSerializer.Serialize(new{id=Guid.NewGuid().ToString("N"),action="window.resize",payload=new{width=Math.Round(Number(measuredCard,"width")+24),height=Math.Ceiling(Number(measuredCard,"height")+24)}});
            MacWebRequest.TryParse(resizeJson,out var resizeRequest);typeof(MacCollaborationControl).GetMethod("OnWebRequest",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(control,[resizeRequest]);
            await Task.Delay(250);owner.UpdateLayout();await Task.Delay(100);
            var geometry=await ReadGeometry(name);var chain=geometry.GetProperty("chain");var web=chain[0];var bounds=web.GetProperty("bounds");var visible=web.GetProperty("visibleRect");var viewport=geometry.GetProperty("dom").GetProperty("viewport");var card=geometry.GetProperty("dom").GetProperty("card");
            var inContent=geometry.GetProperty("webInContentView");var content=geometry.GetProperty("contentViewBounds");
            var expectedWidth=owner.ClientSize.Width;var expectedHeight=owner.ClientSize.Height;
            var errors=new List<string>();
            if(Math.Abs(Number(viewport,"width")-expectedWidth)>1||Math.Abs(Number(viewport,"height")-expectedHeight)>1)errors.Add("WK viewport differs from actual Window.ClientSize");
            if(Number(visible,"width")+1<Number(bounds,"width")||Number(visible,"height")+1<Number(bounds,"height"))errors.Add("WK visibleRect clips its own bounds");
            if(Number(inContent,"x")<Number(content,"x")-1||Number(inContent,"y")<Number(content,"y")-1||Number(inContent,"x")+Number(inContent,"width")>Number(content,"x")+Number(content,"width")+1||Number(inContent,"y")+Number(inContent,"height")>Number(content,"y")+Number(content,"height")+1)errors.Add("WK rectangle extends outside NSWindow content bounds");
            if(Number(card,"x")<0||Number(card,"y")<0||Number(card,"x")+Number(card,"width")>Number(viewport,"width")+1||Number(card,"y")+Number(card,"height")>Number(viewport,"height")+1)errors.Add("HTML card itself is cut by WK viewport");
            foreach(var error in errors)failures.Add(name+": "+error);
            phases.Add(new{name,position=owner.Position.ToString(),windowClientWidth=expectedWidth,windowClientHeight=expectedHeight,controlBounds=control.Bounds.ToString(),geometry,errors});
        }
        void Save()
        {
            var result=new{mode="real-main-window-geometry",fixture=true,syntheticMediaOnly=true,offscreenRafResizeDrivenViaRealMessageContract=true,phases,failures,passed=completed&&failures.Count==0};
            Environment.ExitCode=completed&&failures.Count==0?0:2;File.WriteAllText(output,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(new{mode="geometry",phases=phases.Count,failures,passed=completed&&failures.Count==0}));
        }
        owner.Show();owner.Position=new PixelPoint(-5000,-5000);
        try
        {
            for(var i=0;i<100&&(!(bool)ready.GetValue(control)!||Field(owner,"compactMode") is not true);i++)await Task.Delay(100);
            if(!(bool)ready.GetValue(control)!)throw new InvalidOperationException("Real MainWindow WK did not become ready.");
            ((DispatcherTimer)Field(owner,"refreshTimer")!).Stop();((DispatcherTimer)Field(owner,"wirelessTimer")!).Stop();
            ((CancellationTokenSource)typeof(MacCollaborationService).GetField("lifetime",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(collaboration)!).Cancel();await Task.Delay(150);
            await Check("preview-on");
            await ReadGeometry("transfer-command","transfer");await Check("transfer-expanded");
            await ReadGeometry("transfer-close-command","transfer");await Check("transfer-collapsed");
            await ReadGeometry("web-settings-command","settings");await Check("web-settings-expanded");
            await ReadGeometry("web-settings-close-command","settings");await Check("web-settings-collapsed");
            var dispatch=typeof(MacCollaborationControl).GetMethod("OnWebRequest",BindingFlags.NonPublic|BindingFlags.Instance)!;
            MacWebRequest.TryParse("{\"id\":\"geometry-settings\",\"action\":\"connection.openSettings\",\"payload\":{}}",out var settingsRequest);dispatch.Invoke(control,[settingsRequest]);await Task.Delay(150);
            ((Button)Field(owner,"settingsToggle")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Check("native-settings-return");
            // A restored position must not change the child view's local viewport or clipping.
            var state=collaboration.Snapshot;var screen=owner.Screens.ScreenFromWindow(owner)!;var area=screen.WorkingArea;
            typeof(MacCollaborationService).GetField("snapshot",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(collaboration,state with{Settings=state.Settings with{WindowX=area.X+120,WindowY=area.Y+160,DockEdge=""}});
            typeof(MacCollaborationControl).GetField("placementRestored",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(control,false);
            await Check("restored-placement");
            await ReadGeometry("restored-transfer-command","transfer");await Check("restored-transfer-expanded");
            await ReadGeometry("restored-transfer-close-command","transfer");await Check("restored-transfer-collapsed");
            PushSynthetic(false);await Check("preview-off");PushSynthetic(true);await Check("preview-on-again");
            completed=true;Save();
        }
        catch(Exception error){failures.Add(error.ToString());Save();}
        finally{owner.RequestExit();application.Shutdown(failures.Count==0&&completed?0:2);}
    }
}
