using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;
using Nian.Input;
using Nian.Input.Control;
using Button=System.Windows.Controls.Button;
using Image=System.Windows.Controls.Image;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using HorizontalAlignment=System.Windows.HorizontalAlignment;
using MenuItem=System.Windows.Controls.MenuItem;
using ContextMenu=System.Windows.Controls.ContextMenu;
using Binding=System.Windows.Data.Binding;
using ColorConverter=System.Windows.Media.ColorConverter;

namespace DesktopToolbox.App.DeskLink;

/// <summary>A native, independently resizable viewer. Opening or reconnecting always remains read-only.</summary>
internal sealed partial class DetachedPreviewWindow : Window
{
    private readonly PreviewControlSession? control;
    private readonly DeskflowPreviewSession? handoff;
    private readonly string viewerComputer,targetComputer;
    private readonly Func<bool>? expectedCaptureForeground;
    private readonly DispatcherTimer? foregroundWatch;
    private readonly CancellationTokenSource viewLifetime=new();
    private readonly Image image=new(){Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};
    private readonly Grid stage=new(){Background=Brushes.Transparent,Focusable=true};
    private readonly TextBlock placeholder=new(){FontSize=14,TextAlignment=TextAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(24),IsHitTestVisible=false};
    private readonly TextBlock caption=new(){FontSize=11,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(16,0,0,0)};
    private readonly TextBlock controlStatus=new(){FontSize=12,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock deviceName=new(){Text="Mac 桌面",FontSize=14,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock modeLabel=new(){FontSize=11,VerticalAlignment=VerticalAlignment.Center};
    private readonly Border modeBadge=new(){CornerRadius=new CornerRadius(8),Padding=new Thickness(8,4,8,4),Margin=new Thickness(10,0,8,0)};
    private readonly Border toolbar=new(){CornerRadius=new CornerRadius(16),BorderThickness=new Thickness(1),Padding=new Thickness(12,11,12,11),Margin=new Thickness(12,12,12,10)};
    private readonly Border frameBorder=new(){CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(2),Padding=new Thickness(1),Margin=new Thickness(12,0,12,10),ClipToBounds=true};
    private readonly Button actionButton=MakeButton("开始操作","PreviewControlAction");
    private readonly Button pairingButton=MakeButton("连接设备","PreviewPairingAction");
    private readonly Button shortcutsButton=MakeButton("快捷键⌄","PreviewShortcuts");
    private readonly List<MenuItem> keyItems=[];
    private PreviewControlPalette palette=PreviewControlPalette.ForDark(false);
    private bool? connectionReady;
    private bool? pictureConnected;
    private bool? appearanceDark;
    private bool reducedTransparency;
    private string connectionHint="";
    private bool closed;
    private int displayedWidth,displayedHeight;

    public event Action? PairingRequested;
    public DetachedPreviewWindow():this(null){}
    public DetachedPreviewWindow(PreviewControlSession? control):this(control,null,"Windows","Mac",null){}
    public DetachedPreviewWindow(DeskflowPreviewSession handoff,string viewerComputer,string targetComputer):this(null,handoff,viewerComputer,targetComputer,null){}
    public DetachedPreviewWindow(DeskflowPreviewSession handoff,string viewerComputer,string targetComputer,Func<bool> expectedCaptureForeground):this(null,handoff,viewerComputer,targetComputer,expectedCaptureForeground){}
    private DetachedPreviewWindow(PreviewControlSession? control,DeskflowPreviewSession? handoff,string viewerComputer,string targetComputer,Func<bool>? expectedCaptureForeground)
    {
        this.control=control;this.handoff=handoff;this.viewerComputer=viewerComputer;this.targetComputer=targetComputer;this.expectedCaptureForeground=expectedCaptureForeground;
        Title=targetComputer+" · 预览与操作 · 年年互联";Width=1040;Height=680;MinWidth=420;MinHeight=300;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;UseLayoutRounding=true;SnapsToDevicePixels=true;
        var layout=new DockPanel();BuildToolbar();DockPanel.SetDock(toolbar,Dock.Top);layout.Children.Add(toolbar);
        var footer=new Grid{Margin=new Thickness(16,0,16,12)};footer.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        footer.Children.Add(controlStatus);Grid.SetColumn(caption,1);footer.Children.Add(caption);DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
        stage.Children.Add(image);stage.Children.Add(placeholder);frameBorder.Child=stage;layout.Children.Add(frameBorder);Content=layout;
        InitializeLivePresentation();
        if(handoff is null)ConfigurePointerInput();
        actionButton.Click+=async(_,_)=>await OnActionAsync();
        pairingButton.Click+=(_,_)=>PairingRequested?.Invoke();
        if(control is not null)control.Changed+=OnControlChanged;
        if(handoff is not null)
        {
            handoff.Changed+=OnControlChanged;ToolTipService.SetToolTip(actionButton,"键鼠沿远端位置移动；按 "+handoff.ReturnShortcut+" 归还");
            foregroundWatch=new(){Interval=TimeSpan.FromMilliseconds(250)};
            foregroundWatch.Tick+=OnForegroundWatchTick;foregroundWatch.Start();
        }
        Deactivated+=(_,_)=>OnViewerDeactivated();
        IsVisibleChanged+=(_,_)=>{UpdateLiveVisibility();if(!IsVisible)_ = StopControlAsync("窗口已隐藏，操作已停止");};
        StateChanged+=(_,_)=>{UpdateLiveVisibility();if(WindowState==WindowState.Minimized)_ = StopControlAsync("窗口已最小化，操作已停止");};
        Closed+=async(_,_)=>
        {
            Volatile.Write(ref closed,true);await DisposeLivePresentationAsync();if(foregroundWatch is{} watch){watch.Stop();watch.Tick-=OnForegroundWatchTick;}
            viewLifetime.Cancel();if(control is not null)control.Changed-=OnControlChanged;
            if(handoff is not null){handoff.Changed-=OnControlChanged;try{await handoff.DisposeAsync();}catch{}}
            else await StopControlAsync("窗口已关闭，操作已停止");viewLifetime.Dispose();
        };
        PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape&&CanStopControl){e.Handled=true;_ = StopControlAsync("正在收回键鼠…");}};
        SizeChanged+=(_,_)=>UpdateCompactToolbar();
        SetAppearance(false);RefreshControl();
    }
    private static Button MakeButton(string label,string name)
    {
        var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(9));
        foreach(var property in new[]{Border.BackgroundProperty,Border.BorderBrushProperty,Border.BorderThicknessProperty,Border.PaddingProperty})
            border.SetBinding(property,new Binding(property.Name){RelativeSource=RelativeSource.TemplatedParent});
        var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center);
        border.AppendChild(content);
        var template=new ControlTemplate(typeof(Button)){VisualTree=border};
        var style=new Style(typeof(Button));style.Setters.Add(new Setter(TemplateProperty,template));
        foreach(var (property,value,opacity) in new[]{(IsMouseOverProperty,true,.87),(Button.IsPressedProperty,true,.74),(IsEnabledProperty,false,.45)})
        {var trigger=new Trigger{Property=property,Value=value};trigger.Setters.Add(new Setter(OpacityProperty,opacity));style.Triggers.Add(trigger);}
        return new(){Name=name,Content=label,Style=style,FontSize=12,MinHeight=34,Padding=new Thickness(12,7,12,7),BorderThickness=new Thickness(1),Cursor=System.Windows.Input.Cursors.Hand,VerticalContentAlignment=VerticalAlignment.Center};
    }
    private void BuildToolbar()
    {
        var row=new Grid();foreach(var width in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto,GridLength.Auto})row.ColumnDefinitions.Add(new(){Width=width});
        row.Children.Add(deviceName);modeBadge.Child=modeLabel;Grid.SetColumn(modeBadge,1);row.Children.Add(modeBadge);
        pairingButton.Margin=new Thickness(0,0,8,0);Grid.SetColumn(pairingButton,3);row.Children.Add(pairingButton);
        shortcutsButton.Margin=new Thickness(0,0,8,0);Grid.SetColumn(shortcutsButton,4);row.Children.Add(shortcutsButton);
        actionButton.MinWidth=88;Grid.SetColumn(actionButton,5);row.Children.Add(actionButton);toolbar.Child=row;
        var menu=new ContextMenu{Placement=PlacementMode.Bottom};
        foreach(var (label,key) in new[]{("发送 Enter",InputKey.Enter),("发送 Tab",InputKey.Tab),("发送 Esc",InputKey.Escape)})
        {
            var item=new MenuItem{Header=label,FontSize=12,Padding=new Thickness(12,7,12,7)};
            item.Click+=async(_,_)=>{if(control?.IsActive==true)await control.SendAsync(PreviewInputKind.KeyPress,code:(int)key);RefreshControl();};
            keyItems.Add(item);menu.Items.Add(item);
        }
        shortcutsButton.ContextMenu=menu;shortcutsButton.Click+=(_,_)=>{menu.PlacementTarget=shortcutsButton;menu.IsOpen=true;};
        menu.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape&&control?.CanStop==true){e.Handled=true;menu.IsOpen=false;_=StopControlAsync("已退出操作，仅观看画面");}};
        ToolTipService.SetToolTip(shortcutsButton,"发送到 Mac；本机 Esc 始终停止操作");
        ToolTipService.SetToolTip(pairingButton,"连接或管理允许预览操作的设备");
    }
    public void SetAppearance(bool dark,bool reduceTransparency=false)
    {
        if(appearanceDark==dark&&reducedTransparency==reduceTransparency)return;
        appearanceDark=dark;reducedTransparency=reduceTransparency;
        palette=PreviewControlPalette.ForDark(dark);Background=Brush(palette.Glass);Foreground=Brush(palette.Text);
        toolbar.Background=reduceTransparency?Brush(palette.Surface):new LinearGradientBrush(ColorOf(palette.Surface),ColorOf(palette.Glass),110);
        toolbar.BorderBrush=Brush(palette.Line);toolbar.Effect=reduceTransparency?null:new DropShadowEffect{Color=ColorOf(palette.Shadow),BlurRadius=20,ShadowDepth=3,Opacity=.12};
        frameBorder.Background=Brush(palette.Soft);stage.Background=Brush(palette.Soft);
        deviceName.Foreground=Brush(palette.Text);modeLabel.Foreground=Brush(palette.Muted);placeholder.Foreground=caption.Foreground=controlStatus.Foreground=Brush(palette.Muted);
        pairingButton.Foreground=shortcutsButton.Foreground=Brush(palette.Accent);pairingButton.Background=shortcutsButton.Background=Brushes.Transparent;
        pairingButton.BorderBrush=shortcutsButton.BorderBrush=Brushes.Transparent;
        actionButton.Background=Brush(palette.Button);actionButton.Foreground=Brush(palette.ButtonText);actionButton.BorderBrush=Brushes.Transparent;
        if(shortcutsButton.ContextMenu is{} menu){menu.Background=Brush(palette.Surface);menu.Foreground=Brush(palette.Text);menu.BorderBrush=Brush(palette.Line);}
        RefreshControl();
    }
    public void SetConnectionState(bool ready,string hint)
        => SetConnectionState(ready,hint,ready);
    public void SetConnectionState(bool ready,string hint,bool pictureAvailable)
    {
        if(closed)return;
        var reconnecting=pictureAvailable&&pictureConnected==false;
        connectionReady=ready;connectionHint=hint??"";
        pictureConnected=pictureAvailable;
        if(reconnecting&&handoff?.UsesLivePreview==true)BeginNewLiveConnection();
        if(handoff?.UsesLivePreview==true){UpdateLiveVisibility();if(!pictureAvailable)ClearLivePresentation(connectionHint);}
        if(!ready&&(control?.CanStop==true||handoff?.IsActive==true||handoff?.Phase==PreviewControlPhase.Preparing))_=StopControlAsync("设备连接已断开，正在收回键鼠");
        RefreshControl();
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
        else if(control.CanStart&&(connectionReady??true))try{await control.EnableAsync(viewLifetime.Token);}catch{ }
        RefreshControl();
    }
    public void UpdateFrame(BitmapSource? frame,string state)=>UpdateFrame(frame,state,null);
    public void UpdateFrame(BitmapSource? frame,string state,PreviewFrame? sourceFrame)
    {
        if(closed)return;
        if(HandlePassiveLiveFrame(frame,state,sourceFrame)){RefreshControl();return;}
        ApplyDisplayedFrame(frame,state,sourceFrame);
        if(frame is not null&&sourceFrame?.State=="live")control?.ObserveDisplayedFrame(sourceFrame.ControlProof,sourceFrame.JpegBytes,sourceFrame.Width,sourceFrame.Height);
        else{var active=control?.IsActive==true;control?.ObserveDisplayedFrame(null,[],0,0);if(active)_ = StopControlAsync("画面暂不可用，操作已停止");}
        handoff?.ObserveFrame(frame is not null&&sourceFrame?.State=="live"?sourceFrame:null);
        RefreshControl();
    }
    private void OnControlChanged(){if(!closed&&!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(new Action(RefreshControl));}
    private void RefreshControl()
    {
        if(closed)return;
        RefreshLivePresentationState();
        var phase=control?.Phase??PreviewControlPhase.Viewing;
        var view=handoff is not null?DeskflowPreviewPresentation.Create(handoff,connectionReady??handoff.Supported,connectionHint,viewerComputer,targetComputer):
            PreviewControlPresentation.Create(phase,control is not null,connectionReady??control is not null,connectionHint,control?.Status??"");
        modeLabel.Text=view.ModeLabel;modeBadge.Background=Brush(view.EmphasizeViewport?palette.Button:palette.Soft);modeLabel.Foreground=Brush(view.EmphasizeViewport?palette.ButtonText:palette.Muted);
        actionButton.Content=view.ActionLabel;actionButton.IsEnabled=view.ActionEnabled&&(handoff?.CanStart==true||handoff?.CanStop==true||control?.CanStart==true||control?.CanStop==true);
        controlStatus.Text=view.Message;pairingButton.Visibility=view.ShowPairing?Visibility.Visible:Visibility.Collapsed;
        shortcutsButton.Visibility=view.CanUseKeys?Visibility.Visible:Visibility.Collapsed;foreach(var item in keyItems)item.IsEnabled=view.CanUseKeys;
        frameBorder.BorderBrush=Brush(view.EmphasizeViewport?palette.Accent:palette.Line);
        if(!view.CanUseKeys)ResetPointerCapture();UpdateCompactToolbar();
    }
    private void UpdateCompactToolbar()
    {var compact=(ActualWidth>0?ActualWidth:Width)<560;deviceName.Text=compact?targetComputer:targetComputer+" 桌面";pairingButton.Content=compact?"连接":"连接设备";shortcutsButton.Content=compact?"按键⌄":"快捷键⌄";caption.Visibility=compact?Visibility.Collapsed:Visibility.Visible;}
    private static Color ColorOf(string hex)=>(Color)ColorConverter.ConvertFromString(hex);
    private bool CanStopControl=>handoff?.CanStop??control?.CanStop??false;
    public async Task CloseForDeviceChangeAsync()
    {
        if(handoff is not null&&handoff.CanStop)await handoff.StopAsync("正在收回键鼠…");
        else if(control is not null&&control.CanStop)await control.StopAsync("正在收回键鼠…");
        var phase=handoff?.Phase??control?.Phase??PreviewControlPhase.Viewing;
        if(phase is not (PreviewControlPhase.Viewing or PreviewControlPhase.Closed))throw new InvalidOperationException("键鼠尚未确认归还，请先结束预览操作。");
        Close();
    }
    private void OnViewerDeactivated()
    {
        if(handoff is null){_=StopControlAsync("窗口已失焦，操作已停止");return;}
        // Deskflow's own primary capture window becomes foreground on handoff.
        // The host validates its owned PID and class; an arbitrary blur is not exempt.
        Dispatcher.BeginInvoke(new Action(RecheckHandoffForeground));
    }
    private void OnForegroundWatchTick(object? sender,EventArgs args)=>RecheckHandoffForeground();
    private void RecheckHandoffForeground()
    {
        if(closed||IsActive||handoff is null||handoff.Phase is not (PreviewControlPhase.Preparing or PreviewControlPhase.Controlling))return;
        // The viewer remains inactive after Deskflow captures input; a later local focus change
        // will not raise Deactivated again. Revalidate the same strict host proof until return.
        var expected=false;
        try{expected=expectedCaptureForeground?.Invoke()==true;}catch{}
        if(!expected)_=StopControlAsync("窗口已失焦，正在收回键鼠");
    }
    private static SolidColorBrush Brush(string hex)=>new(ColorOf(hex));
}
