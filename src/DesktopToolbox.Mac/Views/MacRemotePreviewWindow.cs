using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;
using Nian.Input;
using Nian.Input.Control;

namespace DesktopToolbox.Mac.Views;

public sealed partial class MacRemotePreviewWindow : Window
{
    private readonly MacPreviewSurface preview;
    private readonly PreviewControlSession? control;
    private readonly DeskflowPreviewSession? handoff;
    private readonly string viewerComputer,targetComputer;
    private readonly bool candidateBlue;
    private readonly CancellationTokenSource viewLifetime=new();
    private readonly TextBlock deviceName=new(){Text="Windows 桌面",FontSize=14,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock modeLabel=new(){FontSize=11,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock controlStatus=new(){FontSize=12,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock caption=new(){FontSize=11,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(16,0,0,0)};
    private readonly Border modeBadge=new(){CornerRadius=new(8),Padding=new(8,4),Margin=new(10,0,8,0)};
    private readonly Border toolbar=new(){CornerRadius=new(16),BorderThickness=new(1),Padding=new(12,11),Margin=new(12,12,12,10)};
    private readonly Border frameBorder=new(){CornerRadius=new(14),BorderThickness=new(2),Padding=new(1),Margin=new(12,0,12,10),ClipToBounds=true};
    private readonly Button actionButton=MakeButton("开始操作","PreviewControlAction");
    private readonly Button pairingButton=MakeButton("连接设备","PreviewPairingAction");
    private readonly Button shortcutsButton=MakeButton("快捷键⌄","PreviewShortcuts");
    private readonly List<MenuItem> keyItems=[];
    private PreviewControlPalette palette=PreviewControlPalette.ForDark(false);
    private bool? connectionReady;
    private bool? appearanceDark;
    private bool reducedTransparency;
    private string connectionHint="";
    private volatile bool closed;
    public event Action? PairingRequested;
    public MacRemotePreviewWindow():this(null){}
    public MacRemotePreviewWindow(PreviewControlSession? control):this(control,null,"Mac","Windows",false){}
    public MacRemotePreviewWindow(PreviewControlSession? control,bool candidateBlue):this(control,null,"Mac","Windows",candidateBlue){}
    public MacRemotePreviewWindow(DeskflowPreviewSession handoff,string viewerComputer,string targetComputer,bool candidateBlue=false):this(null,handoff,viewerComputer,targetComputer,candidateBlue){}
    private MacRemotePreviewWindow(PreviewControlSession? control,DeskflowPreviewSession? handoff,string viewerComputer,string targetComputer,bool candidateBlue)
    {
        this.control=control;this.handoff=handoff;this.viewerComputer=viewerComputer;this.targetComputer=targetComputer;this.candidateBlue=candidateBlue;preview=new(control);
        Title="Windows · 预览与操作 · 年年互联";Width=1040;Height=680;MinWidth=420;MinHeight=300;
        CanResize=true;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var layout=new DockPanel();BuildToolbar();DockPanel.SetDock(toolbar,Dock.Top);layout.Children.Add(toolbar);
        var footer=new Grid{ColumnDefinitions=new("*,Auto"),Margin=new(16,0,16,12)};footer.Children.Add(controlStatus);Grid.SetColumn(caption,1);footer.Children.Add(caption);DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
        frameBorder.Child=preview;layout.Children.Add(frameBorder);Content=layout;
        actionButton.Click+=async(_,_)=>await OnActionAsync();pairingButton.Click+=(_,_)=>PairingRequested?.Invoke();
        if(control is not null)control.Changed+=OnControlChanged;
        if(handoff is not null){handoff.Changed+=OnControlChanged;ToolTip.SetTip(actionButton,"键鼠沿远端位置移动；按 "+handoff.ReturnShortcut+" 归还");}
        preview.FrameChanged+=OnFrameChanged;
        Deactivated+=(_,_)=>_=StopControlAsync("窗口已失焦，操作已停止");
        PropertyChanged+=(_,e)=>{if(e.Property==IsVisibleProperty&&!IsVisible||e.Property==WindowStateProperty&&WindowState==WindowState.Minimized){if(handoff?.UsesLivePreview==true){liveSurfaceEpoch=null;preview.ClearLivePresentation("画面已暂停");}_=StopControlAsync("窗口已隐藏，操作已停止");}};
        AddHandler(KeyDownEvent,(_,e)=>{if(e.Key==Key.Escape&&CanStopControl){e.Handled=true;_=StopControlAsync("正在收回键鼠…");}},RoutingStrategies.Tunnel);
        Closed+=async(_,_)=>
        {
            closed=true;viewLifetime.Cancel();if(control is not null)control.Changed-=OnControlChanged;
            preview.FrameChanged-=OnFrameChanged;preview.Dispose();
            await DisposeLivePresentationAsync();
            if(handoff is not null){handoff.Changed-=OnControlChanged;try{await handoff.DisposeAsync();}catch{}}
            viewLifetime.Dispose();
        };
        SizeChanged+=(_,_)=>UpdateCompactToolbar();SetAppearance(false);InitializeLivePresentation();RefreshControl();
    }
    private static Button MakeButton(string label,string name)=>new(){Name=name,Content=label,FontSize=12,MinHeight=34,Padding=new(12,7),BorderThickness=new(1),CornerRadius=new(9),Cursor=new Cursor(StandardCursorType.Hand)};
    private void BuildToolbar()
    {
        var row=new Grid{ColumnDefinitions=new("Auto,Auto,*,Auto,Auto,Auto")};
        row.Children.Add(deviceName);modeBadge.Child=modeLabel;Grid.SetColumn(modeBadge,1);row.Children.Add(modeBadge);
        pairingButton.Margin=new(0,0,8,0);Grid.SetColumn(pairingButton,3);row.Children.Add(pairingButton);
        shortcutsButton.Margin=new(0,0,8,0);Grid.SetColumn(shortcutsButton,4);row.Children.Add(shortcutsButton);
        actionButton.MinWidth=88;Grid.SetColumn(actionButton,5);row.Children.Add(actionButton);toolbar.Child=row;
        var menu=new ContextMenu();
        foreach(var (label,key) in new[]{("发送 Enter",InputKey.Enter),("发送 Tab",InputKey.Tab),("发送 Esc",InputKey.Escape)})
        {
            var item=new MenuItem{Header=label,FontSize=12,Padding=new(12,7)};
            item.Click+=async(_,_)=>{if(control?.IsActive==true)await control.SendAsync(PreviewInputKind.KeyPress,code:(int)key);RefreshControl();};keyItems.Add(item);
        }
        menu.ItemsSource=keyItems;shortcutsButton.ContextMenu=menu;shortcutsButton.Click+=(_,_)=>menu.Open(shortcutsButton);
        menu.AddHandler(KeyDownEvent,(_,e)=>{if(e.Key==Key.Escape&&control?.CanStop==true){e.Handled=true;menu.Close();_=StopControlAsync("已退出操作，仅观看画面");}},RoutingStrategies.Tunnel);
        ToolTip.SetTip(shortcutsButton,"发送到 Windows；本机 Esc 始终停止操作");ToolTip.SetTip(pairingButton,"连接或管理允许预览操作的设备");
    }
    public void SetAppearance(bool dark,bool reduceTransparency=false)
    {
        if(appearanceDark==dark&&reducedTransparency==reduceTransparency)return;
        appearanceDark=dark;reducedTransparency=reduceTransparency;
        palette=candidateBlue?new("#F6F9FF","#FFFFFF","#EDF4FF","#D9E5F4","#1B2C43","#526D8E","#356FEB","#356FEB","#FFFFFF","#35527B"):PreviewControlPalette.ForDark(dark);
        RequestedThemeVariant=candidateBlue||!dark?ThemeVariant.Light:ThemeVariant.Dark;
        Background=Brush(palette.Glass);Foreground=Brush(palette.Text);preview.SetPalette(candidateBlue?false:dark);
        toolbar.Background=candidateBlue||reduceTransparency?Brush(palette.Surface):new LinearGradientBrush{StartPoint=new(0,0,RelativeUnit.Relative),EndPoint=new(1,1,RelativeUnit.Relative),GradientStops=new(){new(Color.Parse(palette.Surface),0),new(Color.Parse(palette.Glass),1)}};
        toolbar.BorderBrush=Brush(palette.Line);toolbar.BoxShadow=reduceTransparency?default:BoxShadows.Parse("0 3 20 0 #20"+palette.Shadow[1..]);frameBorder.Background=Brush(palette.Soft);
        deviceName.Foreground=Brush(palette.Text);caption.Foreground=controlStatus.Foreground=Brush(palette.Muted);
        SetButtonPalette(actionButton,palette.Button,palette.ButtonText,palette.Button);
        SetButtonPalette(pairingButton,"#00000000",palette.Accent,palette.Soft);SetButtonPalette(shortcutsButton,"#00000000",palette.Accent,palette.Soft);
        if(shortcutsButton.ContextMenu is{} menu){menu.Background=Brush(palette.Surface);menu.Foreground=Brush(palette.Text);menu.BorderBrush=Brush(palette.Line);}
        RefreshControl();
    }
    public void SetPalette(bool dark)=>SetAppearance(dark);
    private static void SetButtonPalette(Button button,string background,string foreground,string hover)
    {
        button.Background=Brush(background);button.Foreground=Brush(foreground);button.BorderBrush=Brushes.Transparent;
        foreach(var suffix in new[]{"","PointerOver","Pressed","Disabled"})
        {
            button.Resources["ButtonBackground"+suffix]=Brush(suffix is "PointerOver" or "Pressed"?hover:background);
            button.Resources["ButtonForeground"+suffix]=Brush(foreground);button.Resources["ButtonBorderBrush"+suffix]=Brushes.Transparent;
        }
    }
    public void SetConnectionState(bool ready,string hint)
    {
        if(closed)return;var wasReady=connectionReady;connectionReady=ready;connectionHint=hint??"";
        if(!ready&&wasReady==true&&handoff?.UsesLivePreview==true)
        {liveSurfaceEpoch=null;preview.ClearLivePresentation("设备连接已断开，画面暂停");}
        if(!ready&&(control?.CanStop==true||handoff?.IsActive==true||handoff?.Phase==PreviewControlPhase.Preparing))_=StopControlAsync("设备连接已断开，正在收回键鼠");RefreshControl();
    }
    private async Task OnActionAsync()
    {
        if(handoff is not null)
        {
            if(handoff.CanStop)await StopControlAsync("正在收回键鼠…");
            else if(handoff.CanStart&&(connectionReady??true))try{await handoff.EnableAsync(viewLifetime.Token);}catch{}
            RefreshControl();return;
        }
        if(control is null)return;
        if(control.CanStop)await StopControlAsync(control.Phase==PreviewControlPhase.Preparing?"准备已取消，仅观看画面":"已停止操作，仅观看画面");
        else if(control.CanStart&&(connectionReady??true))try{await control.EnableAsync(viewLifetime.Token);}catch{}
        RefreshControl();
    }
    // This is only called by an explicit user action in the candidate screen page.
    internal async Task StartControlFromUserAsync()
    {
        if(closed)throw new InvalidOperationException("预览窗口已关闭。");
        if(CanStopControl){Activate();return;}
        if(!(handoff?.CanStart??control?.CanStart??false)||connectionReady==false)
            throw new InvalidOperationException("请先等待预览连接就绪，再开始操作。");
        await OnActionAsync();
    }
    private void OnControlChanged()=>Dispatcher.UIThread.Post(()=>{if(!closed)RefreshControl();});
    private void OnFrameChanged(){if(!preview.HasLiveFrame)handoff?.ObserveFrame(null);RefreshControl();}
    private void RefreshControl()
    {
        if(closed)return;SyncLivePresentation();var phase=control?.Phase??PreviewControlPhase.Viewing;
        var view=handoff is not null?DeskflowPreviewPresentation.Create(handoff,connectionReady??handoff.Supported,connectionHint,viewerComputer,targetComputer):
            PreviewControlPresentation.Create(phase,control is not null,connectionReady??control is not null,connectionHint,control?.Status??"");
        modeLabel.Text=view.ModeLabel;modeBadge.Background=Brush(view.EmphasizeViewport?palette.Button:palette.Soft);modeLabel.Foreground=Brush(view.EmphasizeViewport?palette.ButtonText:palette.Muted);
        actionButton.Content=view.ActionLabel;actionButton.IsEnabled=view.ActionEnabled&&(handoff?.CanStart==true||handoff?.CanStop==true||control?.CanStart==true||control?.CanStop==true);actionButton.Opacity=actionButton.IsEnabled?1:.5;
        pairingButton.IsVisible=view.ShowPairing;shortcutsButton.IsVisible=view.CanUseKeys;foreach(var item in keyItems)item.IsEnabled=view.CanUseKeys;
        controlStatus.Text=view.Message;caption.Text=preview.FrameStatus;frameBorder.BorderBrush=Brush(view.EmphasizeViewport?palette.Accent:palette.Line);UpdateCompactToolbar();
    }
    private void UpdateCompactToolbar()
    {var compact=(Bounds.Width>0?Bounds.Width:Width)<560;deviceName.Text=compact?"Windows":"Windows 桌面";pairingButton.Content=compact?"连接":"连接设备";shortcutsButton.Content=compact?"按键⌄":"快捷键⌄";caption.IsVisible=!compact;}
    private bool CanStopControl=>handoff?.CanStop??control?.CanStop??false;
    private async Task StopControlAsync(string reason){if(handoff is not null)await handoff.StopAsync(reason);else await preview.StopControlAsync(reason);RefreshControl();}
    public void ShowFrame(PreviewFrame frame,int refreshSeconds=PreviewCaptureCadence.DefaultRefreshSeconds)
    {
        if(closed)return;SyncLivePresentation();
        if(frame.State=="local-off"&&handoff?.UsesLivePreview==true)
        {
            if(handoff.CanStop)_=handoff.StopAfterLiveFailureAsync("画面已关闭，正在收回键鼠");
            liveSurfaceEpoch=null;preview.ClearLivePresentation("画面已关闭，可在小窗手动开启");
            return;
        }
        if(SuppressPassiveFrame)return;preview.ShowFrame(frame,refreshSeconds);handoff?.ObserveFrame(preview.HasLiveFrame?frame:null);
    }
    private static SolidColorBrush Brush(string hex)=>new(Color.Parse(hex));
}
