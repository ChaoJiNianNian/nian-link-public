using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopToolbox.App.Drawer;
using DesktopToolbox.Core;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Windows.Organizing;

namespace DesktopToolbox.App.DeskLink;

/// <summary>Native lifetime/docking around the single shared, approved HTML surface.</summary>
public sealed partial class MiniCollaborationWindow : Window
{
    private readonly IMiniCollaborationService service;
    private readonly MiniWindowPreferencesStore preferencesStore;
    private readonly Action<Exception> log;
    private readonly Func<string,string>? displayName;
    private readonly MiniCollaborationWebSurface surface;
    private readonly CancellationTokenSource lifetime=new();
    private readonly DispatcherTimer presenceTimer=new(){Interval=TimeSpan.FromMilliseconds(500)};
    private readonly SemaphoreSlim preferencesGate=new(1,1);
    private readonly MultiRecipientBarrier recipientBarrier=new();
    private MiniWindowPreferences preferences=new();
    private DrawerBehavior quietBehavior=DrawerBehavior.Default();
    private ShellSettings appearance=new();
    private bool started,stopping,allowClose,quiet,frozen,dragging,hiddenByUser;
    private int queued;
    private bool? lastDemand;
    private bool lastHighResolution;
    private nint handle;
    private long revision,lastFrameReceivedAt;
    private DetachedPreviewWindow? detached;
    private byte[]? lastFrame;
    private DateTimeOffset? lastCaptureStamp;
    private BitmapSource? currentImage;
    public event Action<Window?>? PreviewPairingRequested;
    public event Action? ConnectionSettingsRequested;
    internal bool IsPageReadyForSmoke => surface.IsPageReady;

    public MiniCollaborationWindow(IMiniCollaborationService service,JsonFileStore store,Action<Exception> log,string? webviewProfileDirectory=null,Func<string,string>? displayName=null)
    {
        this.service=service;preferencesStore=new(store);this.log=log;this.displayName=displayName;
        Title="年年互联 · 桌面工具箱";Width=398;Height=310;SizeToContent=SizeToContent.Manual;MiniNativeWindow.Configure(this);
        var profile=webviewProfileDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopToolbox","webview-profile");
        surface=new(profile,InvokePageAsync,()=>{recipientBarrier.Publish(null);UpdateFromService();},log);Content=surface.View;
        SourceInitialized+=(_,_)=>{handle=new WindowInteropHelper(this).Handle;MiniNativeWindow.Initialize(handle);HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);};
        service.Changed+=OnServiceChanged;presenceTimer.Tick+=(_,_)=>UpdatePresence();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged+=OnDisplayChanged;IsVisibleChanged+=(_,_)=>UpdateDemand();
    }
    public async Task StartAsync()
    {
        preferences=await preferencesStore.LoadAsync(lifetime.Token);hiddenByUser=!preferences.Visible;started=true;
        new WindowInteropHelper(this).EnsureHandle();Width=preferences.Collapsed?83:398;Height=preferences.Collapsed?164:310;
        UpdatePresence();await surface.InitializeAsync();UpdateFromService();presenceTimer.Start();
        try{await service.RefreshAsync(lifetime.Token);}catch(OperationCanceledException) when(stopping){}catch(Exception error){log(error);}
    }
    public Task ShowMiniAsync(){hiddenByUser=false;preferences=preferences with {Visible=true};UpdatePresence();Activate();_=SavePreferencesAsync();return Task.CompletedTask;}
    public Task ClosePreviewForDeviceChangeAsync()=>detached?.CloseForDeviceChangeAsync()??Task.CompletedTask;
    public void SetQuietBehavior(DrawerBehavior behavior){quietBehavior=behavior;UpdatePresence();}
    public void SetAppearance(ShellSettings settings){appearance=settings;UpdateFromService();}
    public void Freeze(bool value){frozen=value;IsEnabled=!value;UpdatePresence();}
    private void OnServiceChanged(object? sender,EventArgs args)
    {
        if(stopping||Interlocked.Exchange(ref queued,1)!=0)return;
        Dispatcher.BeginInvoke(()=>{Interlocked.Exchange(ref queued,0);if(!stopping)UpdateFromService();});
    }
    private void UpdateFromService()
    {
        if(stopping)return;
        ApplyPinnedState();
        var state=service.Snapshot;var frame=state.PreviewEnabled?state.Preview:PreviewFrame.Off("local-off");
        var recipient=state.MultiDeviceMode&&state.Devices.Any(device=>device.Name==state.SelectedDevice)?state.SelectedDevice:null;
        var deviceVersion=recipientBarrier.Publish(recipient);
        if(detached is { } viewer)ApplyDetachedAppearance(viewer,state.MultiDeviceMode,DetachedUsesDarkAppearance(),appearance.ReduceTransparency);
        var controlReady=service.PreviewControlReady;
        detached?.SetConnectionState(controlReady,service.PreviewControlHint,state.PreviewEnabled&&(state.Connected||controlReady));
        detached?.SetPassivePreviewContext(state.PassivePreviewRequestId,state.RemotePreviewSourceStatus);
        RenderDetachedFrame(frame);
        var maximum=handle==0?1000:MiniNativeWindow.WorkArea(MiniNativeWindow.Screen(handle,preferences.Display)).Height/(MiniNativeWindow.Dpi(handle)/96d)-16;
        surface.PublishState(new {epoch=surface.Epoch,revision=++revision,platform="Windows",connected=state.Connected,connectionMessage=state.ConnectionMessage,activeComputer=state.ActiveComputer,
            keyboardSharingRunning=state.KeyboardSharingRunning,keyboardSharingConnected=state.KeyboardSharingConnected,keyboardCanStart=state.KeyboardCanStart,keyboardCanStop=state.KeyboardCanStop,keyboardSharingMessage=state.KeyboardSharingMessage,canSwitchWindows=state.CanSwitchWindows,canSwitchMac=state.CanSwitchMac,
            previewEnabled=state.PreviewEnabled,previewRefreshSeconds=state.PreviewRefreshSeconds,preview=frame,clipboardEnabled=state.ClipboardEnabled,clipboardMessage=state.ClipboardMessage,receiveDirectory=state.ReceiveDirectory,sharedDirectory=state.SharedDirectory,sharedFolderIsRemote=state.SharedFolderIsRemote,transfers=state.Transfers,devices=MiniDeviceLabels.Apply(state.Devices,displayName),multiDeviceMode=state.MultiDeviceMode,selectedDevice=state.SelectedDevice,localDeviceName=state.LocalDeviceName,deviceVersion,pinned=preferences.Pinned,collapsed=preferences.Collapsed,theme=appearance.Theme,reduceTransparency=appearance.ReduceTransparency,maxHeight=maximum,
            dragThresholdX=SystemParameters.MinimumHorizontalDragDistance,dragThresholdY=SystemParameters.MinimumVerticalDragDistance});
        UpdateDemand();
    }
    private void RenderDetachedFrame(PreviewFrame frame)
    {
        var live=frame.State=="live"&&frame.JpegBytes.Length is >0 and <=1048576&&frame.Width is >0 and <=1920&&frame.Height is >0 and <=1080;
        if(live)
        {
            if(lastCaptureStamp!=frame.CapturedAtUtc){lastCaptureStamp=frame.CapturedAtUtc;lastFrameReceivedAt=Environment.TickCount64;}
            if(Environment.TickCount64-lastFrameReceivedAt>(service.Snapshot.PreviewRefreshSeconds+8)*1000L)live=false;
            else if(detached is not null&&!ReferenceEquals(lastFrame,frame.JpegBytes))
            {
                try{currentImage=DecodeImage(frame.JpegBytes);lastFrame=frame.JpegBytes;}
                catch(Exception error) when(error is NotSupportedException or FileFormatException or ArgumentException or IOException){log(error);live=false;}
            }
        }
        if(!live||detached is null){currentImage=null;lastFrame=null;}
        detached?.UpdateFrame(currentImage,live?$"常规每 {service.Snapshot.PreviewRefreshSeconds} 秒刷新 · 开启控制后临时加快":FrameMessage(frame),frame);
    }
    private static string FrameMessage(PreviewFrame frame)=>frame.State switch
    {"local-off"=>"画面已关闭，可在小窗手动开启","off"=>"对方已关闭画面，请在对方开启","locked"=>"对方已锁屏，解锁后继续","display-unavailable"=>"对方显示器暂不可用","disconnected" or "offline"=>"对方已断开连接","paused"=>"画面已暂停",_=>frame.Error??"正在等待对方画面…"};
    internal static BitmapSource DecodeImage(byte[] bytes)
    {using var memory=new MemoryStream(bytes,false);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=1920;image.StreamSource=memory;image.EndInit();image.Freeze();return image;}
    private void OpenDetachedPreview()
    {
        if(detached is not null){if(detached.WindowState==WindowState.Minimized)detached.WindowState=WindowState.Normal;detached.Activate();return;}
        var handoff=service.CreateDeskflowPreviewSession();
        var target=MiniDeviceLabels.Apply(service.Snapshot.Devices,displayName).FirstOrDefault(device=>device.Name==service.Snapshot.SelectedDevice)?.Label??"Mac";
        detached=handoff is not null?new DetachedPreviewWindow(handoff,"Windows",target,()=>service.PreviewInputOwnsForeground):new DetachedPreviewWindow(service.CreatePreviewControlSession());ApplyDetachedAppearance(detached,service.Snapshot.MultiDeviceMode,DetachedUsesDarkAppearance(),appearance.ReduceTransparency);
        var state=service.Snapshot;var controlReady=service.PreviewControlReady;
        detached.SetConnectionState(controlReady,service.PreviewControlHint,state.PreviewEnabled&&(state.Connected||controlReady));
        detached.SetPassivePreviewContext(state.PassivePreviewRequestId,state.RemotePreviewSourceStatus);
        var viewer=detached;
        detached.PairingRequested+=()=>PreviewPairingRequested?.Invoke(viewer);
        detached.Closed+=(_,_)=>{detached=null;UpdateDemand();};detached.IsVisibleChanged+=(_,_)=>UpdateDemand();detached.StateChanged+=(_,_)=>UpdateDemand();detached.Show();
        RenderDetachedFrame(service.Snapshot.PreviewEnabled?service.Snapshot.Preview:PreviewFrame.Off("local-off"));UpdateDemand();
    }
    private bool DetachedUsesDarkAppearance()
    {
        if(appearance.Theme!="system")return appearance.Theme=="dark";
        try{return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) is int value&&value==0;}
        catch{return false;}
    }
    private static void ApplyDetachedAppearance(DetachedPreviewWindow viewer,bool multiDevice,bool dark,bool reduceTransparency)
    {
#if NIAN_MULTIDEVICE_UI
        if(multiDevice){viewer.SetClearBlueAppearance();return;}
#endif
        viewer.SetAppearance(dark,reduceTransparency);
    }
    private void UpdateDemand()
    {
        var high=detached?.IsVisible==true&&detached.WindowState!=WindowState.Minimized;
        var demand=!stopping&&MiniWindowPolicy.ShouldSubscribe(service.Snapshot.PreviewEnabled,IsVisible&&!quiet&&!frozen,preferences.Collapsed,high);
        if(demand==lastDemand&&high==lastHighResolution)return;lastDemand=demand;lastHighResolution=high;service.SetPreviewVisible(demand,high);
    }
    private void UpdatePresence()
    {
        if(!started||stopping)return;
        try
        {
            ApplyPinnedState();
            quiet=DrawerWindowPolicy.IsQuiet(DrawerDesktopEnvironment.Foreground(),Environment.ProcessId,quietBehavior.HideDuringFullscreen,quietBehavior.QuietApplications??[],quiet);
            if(quiet||frozen||hiddenByUser){if(IsVisible)Hide();}
            else{if(!IsVisible){ShowActivated=false;Show();UpdateSizeAndPlacement();}MiniNativeWindow.Restore(handle);}
            if(detached is not null&&lastFrameReceivedAt!=0&&Environment.TickCount64-lastFrameReceivedAt>(service.Snapshot.PreviewRefreshSeconds+8)*1000L)RenderDetachedFrame(PreviewFrame.Off("waiting"));
            UpdateDemand();
        }
        catch(Win32Exception error){log(error);}
    }
    private void ApplyPinnedState()
    {
        if(Topmost!=preferences.Pinned)Topmost=preferences.Pinned;
    }
    private void UpdateSizeAndPlacement(){if(dragging||stopping||handle==0)return;MiniNativeWindow.Place(handle,Width,Height,preferences);}
    private void SetCollapsed(bool collapsed,bool save=true)
    {preferences=preferences with {Collapsed=collapsed};Width=collapsed?83:398;Height=collapsed?164:310;UpdateFromService();UpdateSizeAndPlacement();if(save)_=SavePreferencesAsync();}
    private async Task SavePreferencesAsync()
    {await preferencesGate.WaitAsync();try{await preferencesStore.SaveAsync(preferences,lifetime.Token);}catch(OperationCanceledException) when(stopping){}catch(Exception error){log(error);}finally{preferencesGate.Release();}}
    private void OnDisplayChanged(object? sender,EventArgs args)=>Dispatcher.BeginInvoke(()=>{UpdateSizeAndPlacement();UpdateFromService();});
    [StructLayout(LayoutKind.Sequential)]private struct WindowPosition{public nint Window,After;public int X,Y,Width,Height;public uint Flags;}
    private nint WindowMessage(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        var keep=started&&!stopping&&!quiet&&!frozen&&!hiddenByUser;
        if(keep&&message==0x0112&&((long)wParam&0xFFF0)==0xF020){handled=true;return 0;}
        if(keep&&message==0x0046&&lParam!=0){var position=Marshal.PtrToStructure<WindowPosition>(lParam);position.Flags&=~0x0080u;Marshal.StructureToPtr(position,lParam,false);}
        if(message is 0x02E0 or 0x001A||keep&&message==0x0005&&wParam==1)Dispatcher.BeginInvoke(()=>{UpdateSizeAndPlacement();UpdatePresence();UpdateFromService();});return 0;
    }
    public async Task StopAsync()
    {
        if(stopping)return;stopping=true;presenceTimer.Stop();lifetime.Cancel();service.Changed-=OnServiceChanged;Microsoft.Win32.SystemEvents.DisplaySettingsChanged-=OnDisplayChanged;
        surface.Dispose();service.SetPreviewVisible(false);detached?.Close();allowClose=true;Close();await preferencesGate.WaitAsync();preferencesGate.Release();lifetime.Dispose();
    }
    protected override void OnClosing(CancelEventArgs args)
    {if(!allowClose){args.Cancel=true;hiddenByUser=true;preferences=preferences with {Visible=false};Hide();UpdateDemand();_=SavePreferencesAsync();}base.OnClosing(args);}
}
