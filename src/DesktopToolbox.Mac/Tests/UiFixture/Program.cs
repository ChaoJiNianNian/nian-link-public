using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.ViewModels;
using DesktopToolbox.Mac.Views;

/// <summary>Actual native NSView embedding probe; runs only in its own isolated Mac cache directory.</summary>
internal static partial class Program
{
    private static string output = "";
    private static bool mainWindowRegression;
    private static bool geometryRegression;
    private static bool previewRendering;
    private static bool deskflowPreviewRendering;
    private static bool livePreviewRendering;
    [DllImport("libDeskLinkWeb.dylib", CallingConvention=CallingConvention.Cdecl)] private static extern void nian_web_snapshot(IntPtr view,[MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("libDeskLinkWeb.dylib", CallingConvention=CallingConvention.Cdecl)] private static extern int nian_web_is_attached(IntPtr view);
    [DllImport("libDeskLinkWeb.dylib", CallingConvention=CallingConvention.Cdecl)] private static extern int nian_web_is_visible(IntPtr view);
    [STAThread] private static void Main(string[] args)
    {
        geometryRegression=args.Length==2 && args[0]=="--geometry";
        mainWindowRegression=args.Length==2 && args[0]=="--main-window";
        previewRendering=args.Length==2 && args[0]=="--preview-render";
        deskflowPreviewRendering=args.Length==2 && args[0]=="--deskflow-preview-render";
        livePreviewRendering=args.Length==2 && args[0]=="--live-preview-render";
        var target=mainWindowRegression||geometryRegression||previewRendering||deskflowPreviewRendering||livePreviewRendering?args[1]:args.Length==1?args[0]:"";
        if (!Path.IsPathFullyQualified(target)) throw new ArgumentException("Provide [--main-window|--geometry|--preview-render|--deskflow-preview-render|--live-preview-render] absolute-owned-cache-output-path.");
        output=target; if(!Path.GetRelativePath(AppContext.BaseDirectory,output).StartsWith("..",StringComparison.Ordinal) && File.Exists(output))File.Delete(output);
        AppBuilder.Configure<TestApplication>().UsePlatformDetect().With(new MacOSPlatformOptions { ShowInDock = false }).StartWithClassicDesktopLifetime([],ShutdownMode.OnExplicitShutdown);
    }
    private sealed class TestApplication:Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            if(previewRendering||deskflowPreviewRendering||livePreviewRendering)Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://NianDeskLink/")){Source=new Uri("avares://NianDeskLink/Themes/DeskLinkTheme.axaml")});
        }
        public override void OnFrameworkInitializationCompleted()
        {
            base.OnFrameworkInitializationCompleted();
            Dispatcher.UIThread.Post(async ()=>{var lifetime=(IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;if(livePreviewRendering)await RunLivePreviewRenderAsync(lifetime);else if(deskflowPreviewRendering)await RunDeskflowPreviewRenderAsync(lifetime);else if(previewRendering)await RunPreviewRenderAsync(lifetime);else if(geometryRegression)await RunGeometryAsync(lifetime);else if(mainWindowRegression)await RunMainWindowAsync(lifetime);else await RunAsync(lifetime);});
        }
    }
    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime application)
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"fixture-profile");Directory.CreateDirectory(directory);
        await using var service=new MacCollaborationService(directory,_=>Task.FromResult<DesktopToolbox.DeskLink.DeskLinkPairingBundle?>(null),new FakeMedia());
        using var link=new MacLinkViewModel(new FakeLink());await link.InitializeAsync();
        await using var wireless=new MacWirelessViewModel(new FakeWireless());
        var owner=new Window {Width=398,Height=310,Position=new PixelPoint(-5000,-5000),WindowDecorations=WindowDecorations.None,TransparencyLevelHint=[WindowTransparencyLevel.Transparent],Background=Brushes.Transparent,ShowActivated=false,ShowInTaskbar=false,RequestedThemeVariant=ThemeVariant.Dark};
        using var control=new MacCollaborationControl(owner,service,link,wireless);owner.Content=control;
        control.LayoutRequested+=()=>{owner.Width=control.RequestedSize.Width;owner.Height=control.RequestedSize.Height;};
        var handleField=typeof(MacWebViewHost).GetField("view",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var readyProperty=typeof(MacWebViewHost).GetProperty("BridgeReady",BindingFlags.Instance|BindingFlags.NonPublic)!;
        owner.Show();owner.Position = new PixelPoint(-5000,-5000);
        try
        {
            for(var i=0;i<100 && !(bool)readyProperty.GetValue(control)!;i++)await Task.Delay(100);
            owner.Width=398;owner.Height=310;owner.UpdateLayout();
            var ready=(bool)readyProperty.GetValue(control)!;var handle=(IntPtr)handleField.GetValue(control)!;
            if(!ready || handle==IntPtr.Zero)throw new InvalidOperationException("Native WK NSView did not complete its real JSON ready handshake.");
            var attached=nian_web_is_attached(handle)==1;if(!attached)throw new InvalidOperationException("WK is not attached to the Avalonia NSWindow.");
            nian_web_snapshot(handle,output);
            for(var i=0;i<100 && !File.Exists(output);i++)await Task.Delay(100);
            if(!File.Exists(output))throw new IOException("WK embedded snapshot did not complete.");
            Console.WriteLine(JsonSerializer.Serialize(new{nativeEmbedded=attached,ready,snapshotCreated=true,width=owner.Width,height=owner.Height,bounds=control.Bounds.ToString(),fixture=true}));
            control.IsVisible=false;owner.UpdateLayout();await Task.Delay(100);if(nian_web_is_visible(handle)!=0)throw new InvalidOperationException("Hidden collaboration view still covers the settings page.");
            control.IsVisible=true;owner.UpdateLayout();await Task.Delay(100);if(nian_web_is_visible(handle)!=1)throw new InvalidOperationException("Returned collaboration view is still hidden.");
            owner.Hide(); await Task.Delay(100); owner.Show(); owner.Position=new PixelPoint(-5000,-5000); owner.UpdateLayout();
            if(nian_web_is_attached(handle)!=1)throw new InvalidOperationException("WK lost its NSWindow after hide/show.");
            var dispatch=typeof(MacCollaborationControl).GetMethod("OnWebRequest",BindingFlags.Instance|BindingFlags.NonPublic)!;
            MacWebRequest.TryParse("{\"id\":\"fixture-collapse\",\"action\":\"window.collapse\",\"payload\":{}}",out var collapse);dispatch.Invoke(control,[collapse]);owner.UpdateLayout();await Task.Delay(100);
            if(control.RequestedSize!=new Size(83,164))throw new InvalidOperationException("Capsule viewport changed.");
            MacWebRequest.TryParse("{\"id\":\"fixture-expand\",\"action\":\"window.expand\",\"payload\":{}}",out var expand);dispatch.Invoke(control,[expand]);owner.UpdateLayout();await Task.Delay(100);
            if(control.RequestedSize.Width!=398 || nian_web_is_attached(handle)!=1)throw new InvalidOperationException("Expanded WK is not attached.");
            owner.Content=null;await Task.Delay(100);if((IntPtr)handleField.GetValue(control)! != IntPtr.Zero)throw new InvalidOperationException("Native WK did not release on detach.");
            Console.WriteLine("{\"settingsHidesWeb\":true,\"returnShowsWeb\":true,\"hideShow\":true,\"collapseExpand\":true,\"releasedOnDetach\":true}");owner.Close();application.Shutdown(0);
        }
        catch(Exception error){Console.Error.WriteLine(error.ToString());owner.Content=null;owner.Close();application.Shutdown(2);}
    }
    private sealed class FakeMedia : IMacMediaBridge
    {
        public Task<JsonDocument> RequestAsync(object request,CancellationToken cancellationToken=default)=>Task.FromResult(JsonDocument.Parse("{\"status\":\"ready\"}"));
        public Task<MacScreenCaptureReply> CaptureAsync(int width=960,int height=540,uint? windowId=null,CancellationToken cancellationToken=default)=>Task.FromResult(new MacScreenCaptureReply());
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class FakeWireless : IMacWirelessModeService
    {
        public Task<MacWirelessSnapshot> RefreshAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new MacWirelessSnapshot(MacAwdlState.Disabled,"已开启",""));
        public Task<MacWirelessSnapshot> SetSmoothModeAsync(bool enabled,CancellationToken cancellationToken=default)=>RefreshAsync(cancellationToken);
    }
    private sealed class FakeLink : IMacLinkService
    {
        public event Action<MacLinkSnapshot>? Changed { add {} remove {} }
        public Task<MacLinkSnapshot> RefreshAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new MacLinkSnapshot {HasApprovedPairing=true,IsRunning=true,IsConnected=true,ControlConnected=true,ActiveComputer="My-Mac"});
        public Task StartAsync(MacConnectionSettings settings,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task ConfirmPeerAsync(string fingerprint,bool accept,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task<PairingPreview> PreviewPairingAsync(string json,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task AcceptPairingAsync(string previewId,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task<MacSwitchOutcome> SwitchAsync(string target,CancellationToken cancellationToken=default)=>Task.FromResult(new MacSwitchOutcome(true,target,true,"disabled",""));
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}







