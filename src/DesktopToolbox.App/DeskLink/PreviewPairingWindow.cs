using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Nian.Input.Control;
using Nian.Input.Runtime;
using Nian.Input.Security;
using Button=System.Windows.Controls.Button;
using CheckBox=System.Windows.Controls.CheckBox;
using Clipboard=System.Windows.Clipboard;
using HorizontalAlignment=System.Windows.HorizontalAlignment;
using Orientation=System.Windows.Controls.Orientation;
using TextBox=System.Windows.Controls.TextBox;
using FontFamily=System.Windows.Media.FontFamily;
using Binding=System.Windows.Data.Binding;
using Color=System.Windows.Media.Color;
using ColorConverter=System.Windows.Media.ColorConverter;
using Control=System.Windows.Controls.Control;

namespace DesktopToolbox.App.DeskLink;

/// <summary>User-opened preview-only pairing. Codes remain in this window, never in status or diagnostic output.</summary>
internal sealed class PreviewPairingWindow : Window
{
    private readonly PreviewOnlyDeviceService device;
    private readonly PreviewControlPalette palette;
    private readonly CancellationTokenSource lifetime=new();
    private readonly DispatcherTimer expiryTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly TextBlock status=new(){FontSize=12,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock deviceState=new(){FontSize=12};
    private readonly TextBlock expires=new(){FontSize=11,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock peerName=new(){FontSize=13,FontWeight=FontWeights.SemiBold};
    private readonly TextBox localFingerprint;
    private readonly TextBox peerFingerprint;
    private readonly TextBox invitationCode;
    private readonly TextBox otherCode;
    private readonly TextBox targetFingerprint;
    private readonly TextBlock targetAddress=new(){FontSize=12,TextWrapping=TextWrapping.Wrap};
    private readonly Border peerCard;
    private readonly Border targetCard;
    private readonly Button generate;
    private readonly Button copy;
    private readonly Button paste;
    private readonly Button connect;
    private readonly Button revoke;
    private readonly CheckBox consent=new(){Margin=new Thickness(0,12,0,12)};
    private DeviceInvitation? invitation,parsed;
    private string parsedCode="";
    private bool busy,closed;

    public PreviewPairingWindow(PreviewOnlyDeviceService device,bool dark=false,bool reduceTransparency=false)
    {
        ArgumentNullException.ThrowIfNull(device);this.device=device;palette=PreviewControlPalette.ForDark(dark);
        Title="连接预览设备 · 年年互联";Width=620;Height=790;MinWidth=480;MinHeight=500;
        MaxHeight=Math.Max(500,SystemParameters.WorkArea.Height-40);WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=PreviewPairingVisuals.Brush(palette.Glass);Foreground=PreviewPairingVisuals.Brush(palette.Text);
        UseLayoutRounding=true;SnapsToDevicePixels=true;
        localFingerprint=PreviewPairingVisuals.TextBox(palette,true);peerFingerprint=PreviewPairingVisuals.TextBox(palette,true);
        invitationCode=PreviewPairingVisuals.TextBox(palette,true);invitationCode.Visibility=Visibility.Collapsed;invitationCode.MaxHeight=62;
        otherCode=PreviewPairingVisuals.TextBox(palette);otherCode.Name="PreviewOtherInvitation";otherCode.Height=66;otherCode.MaxLength=4096;otherCode.AcceptsReturn=true;
        targetFingerprint=PreviewPairingVisuals.TextBox(palette,true);
        generate=PreviewPairingVisuals.Button("生成邀请码",palette);copy=PreviewPairingVisuals.Button("复制邀请码",palette);
        paste=PreviewPairingVisuals.Button("粘贴邀请码",palette);connect=PreviewPairingVisuals.Button("确认连接",palette,true);
        connect.Name="PreviewPairConnect";revoke=PreviewPairingVisuals.Button("移除预览授权",palette);
        var layout=new DockPanel{Margin=new Thickness(22)};
        var header=new StackPanel{Margin=new Thickness(0,0,0,16)};
        header.Children.Add(PreviewPairingVisuals.Text("连接自己的另一台电脑",22,palette.Text,true));
        header.Children.Add(PreviewPairingVisuals.Text("只允许预览画面内的点击、拖动、滚动和快捷键。",12,palette.Muted,margin:new Thickness(0,6,0,10)));
        deviceState.Foreground=PreviewPairingVisuals.Brush(palette.Accent);header.Children.Add(deviceState);DockPanel.SetDock(header,Dock.Top);layout.Children.Add(header);
        var footer=new Grid{Margin=new Thickness(0,14,0,0)};footer.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        status.Foreground=PreviewPairingVisuals.Brush(palette.Muted);status.Margin=new Thickness(0,0,14,0);footer.Children.Add(status);
        var done=PreviewPairingVisuals.Button("完成",palette);done.Click+=(_,_)=>Close();Grid.SetColumn(done,1);footer.Children.Add(done);DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
        var body=new StackPanel();
        var identity=new StackPanel();identity.Children.Add(PreviewPairingVisuals.Text("此电脑的完整安全指纹",12,palette.Muted));localFingerprint.Margin=new Thickness(0,7,0,0);identity.Children.Add(localFingerprint);
        body.Children.Add(PreviewPairingVisuals.Card(identity,palette));
        var paired=new StackPanel();paired.Children.Add(peerName);peerFingerprint.Margin=new Thickness(0,7,0,10);paired.Children.Add(peerFingerprint);revoke.HorizontalAlignment=HorizontalAlignment.Left;paired.Children.Add(revoke);
        peerCard=PreviewPairingVisuals.Card(paired,palette);body.Children.Add(peerCard);
        var outgoing=new StackPanel();outgoing.Children.Add(PreviewPairingVisuals.Text("让另一台电脑连接",15,palette.Text,true));
        outgoing.Children.Add(PreviewPairingVisuals.Text("生成后发送到自己的另一台电脑，在那边粘贴并核对指纹。",12,palette.Muted,margin:new Thickness(0,5,0,10)));
        var inviteActions=new WrapPanel();generate.Margin=new Thickness(0,0,8,0);copy.Margin=new Thickness(0,0,10,0);inviteActions.Children.Add(generate);inviteActions.Children.Add(copy);expires.Foreground=PreviewPairingVisuals.Brush(palette.Muted);inviteActions.Children.Add(expires);outgoing.Children.Add(inviteActions);
        invitationCode.Margin=new Thickness(0,10,0,0);outgoing.Children.Add(invitationCode);body.Children.Add(PreviewPairingVisuals.Card(outgoing,palette));
        var incoming=new StackPanel();incoming.Children.Add(PreviewPairingVisuals.Text("连接另一台电脑",15,palette.Text,true));
        incoming.Children.Add(PreviewPairingVisuals.Text("粘贴对方新生成的预览邀请码；核对后才会连接。",12,palette.Muted,margin:new Thickness(0,5,0,10)));
        incoming.Children.Add(otherCode);paste.HorizontalAlignment=HorizontalAlignment.Left;paste.Margin=new Thickness(0,8,0,10);incoming.Children.Add(paste);
        var target=new StackPanel();target.Children.Add(targetAddress);targetFingerprint.Margin=new Thickness(0,7,0,0);target.Children.Add(targetFingerprint);targetCard=PreviewPairingVisuals.Card(target,palette);targetCard.Margin=new Thickness(0);incoming.Children.Add(targetCard);
        consent.Content=PreviewPairingVisuals.Text("我已核对完整指纹，同意双方仅进行预览画面内操作",12,palette.Text);incoming.Children.Add(consent);
        connect.HorizontalAlignment=HorizontalAlignment.Right;incoming.Children.Add(connect);body.Children.Add(PreviewPairingVisuals.Card(incoming,palette));
        layout.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});Content=layout;
        generate.Click+=OnGenerate;copy.Click+=OnCopy;paste.Click+=OnPaste;connect.Click+=OnPairClick;revoke.Click+=OnRevokeClick;
        otherCode.TextChanged+=(_,_)=>ParseOtherCode();consent.Checked+=(_,_)=>RefreshButtons();consent.Unchecked+=(_,_)=>RefreshButtons();
        device.Changed+=OnDeviceChanged;expiryTimer.Tick+=(_,_)=>UpdateExpiry();expiryTimer.Start();
        Loaded+=async(_,_)=>await StartAsync();
        Closed+=(_,_)=>{closed=true;lifetime.Cancel();device.CancelInvitation();expiryTimer.Stop();device.Changed-=OnDeviceChanged;invitation=parsed=null;parsedCode="";invitationCode.Clear();otherCode.Clear();lifetime.Dispose();};
        UpdateDeviceState();RefreshButtons();
    }
    private async Task StartAsync()
    {
        if(closed||busy)return;busy=true;SetFeedback("正在准备此电脑的预览连接…");RefreshButtons();
        try{await device.StartAsync(lifetime.Token);if(!closed)SetFeedback("默认仅观看；连接后仍需手动开始操作。");}
        catch(Exception failure){if(!closed)SetFeedback(FriendlyFailure(failure),true);}
        finally{busy=false;if(!closed){UpdateDeviceState();RefreshButtons();}}
    }
    private async void OnGenerate(object sender,RoutedEventArgs e)
    {
        if(busy||closed)return;
        if(!device.Status.Started){await StartAsync();return;}
        busy=true;RefreshButtons();
        try
        {
            invitation=device.CreateInvitation();invitationCode.Text=invitation.ToCode();invitationCode.Visibility=Visibility.Visible;
            SetFeedback("邀请码仅在有效期内使用，请在另一台电脑粘贴。");UpdateExpiry();
        }
        catch(Exception failure){SetFeedback(FriendlyFailure(failure),true);}
        finally{busy=false;RefreshButtons();}
    }
    private void OnCopy(object sender,RoutedEventArgs e)
    {
        if(busy||invitation is null||invitation.ExpiresAt<=DateTimeOffset.UtcNow)return;
        try{Clipboard.SetText(invitationCode.Text);SetFeedback("邀请码已复制，请在自己的另一台电脑粘贴。");}
        catch{SetFeedback("剪贴板暂不可用，请稍后点击复制，或手动选择邀请码复制。",true);}
    }
    private void OnPaste(object sender,RoutedEventArgs e)
    {
        if(busy||closed)return;
        try{if(Clipboard.ContainsText()){otherCode.Text=Clipboard.GetText().Trim();otherCode.Focus();}else SetFeedback("剪贴板没有邀请码，请先在另一台电脑复制。",true);}
        catch{SetFeedback("无法读取剪贴板，请在输入框手动粘贴邀请码。",true);}
    }
    private void ParseOtherCode()
    {
        parsed=null;parsedCode="";consent.IsChecked=false;targetCard.Visibility=Visibility.Collapsed;
        var code=otherCode.Text.Trim();
        if(code.Length==0){RefreshButtons();return;}
        try
        {
            var candidate=DeviceInvitation.Parse(code);
            if(candidate.Permissions!=DevicePermission.PreviewControl)throw new InvalidDataException();
            if(candidate.ExpiresAt<=DateTimeOffset.UtcNow||candidate.ExpiresAt-DateTimeOffset.UtcNow>TimeSpan.FromMinutes(2.1))
            {SetFeedback("对方邀请码已到期，请重新生成后粘贴。",true);RefreshButtons();return;}
            if(string.Equals(candidate.Fingerprint,device.Status.LocalFingerprint,StringComparison.OrdinalIgnoreCase))
            {SetFeedback("这是此电脑的邀请码，请粘贴另一台电脑生成的邀请码。",true);RefreshButtons();return;}
            parsed=candidate;parsedCode=code;targetAddress.Text=$"目标设备 · {candidate.Host}:{candidate.Port}\n请与对方窗口显示的完整指纹核对：";
            targetFingerprint.Text=PreviewPairingVisuals.Fingerprint(candidate.Fingerprint);targetCard.Visibility=Visibility.Visible;SetFeedback("邀请码已读取；核对完整指纹并勾选同意后连接。");
        }
        catch{SetFeedback("邀请码无效，请复制对方新生成的预览邀请码。",true);}
        RefreshButtons();
    }
    private async void OnPairClick(object sender,RoutedEventArgs e)
    {
        if(busy||closed||parsed is null||consent.IsChecked!=true||parsed.ExpiresAt<=DateTimeOffset.UtcNow||!device.Status.Started)return;
        var code=parsedCode;busy=true;RefreshButtons();
        try
        {
            // No await, post, or worker hop may precede this call: the platform validates this exact local click.
            var pairing=device.PairAsync(code,lifetime.Token);
            SetFeedback("请在另一台电脑本机核对指纹，并允许预览操作…");
            await pairing;
            if(!closed){otherCode.Clear();consent.IsChecked=false;ClearInvitation();SetFeedback("设备已连接；回到预览后，手动开始操作。");}
        }
        catch(Exception failure){if(!closed)SetFeedback(FriendlyFailure(failure),true);}
        finally{busy=false;if(!closed){UpdateDeviceState();RefreshButtons();}}
    }
    private async void OnRevokeClick(object sender,RoutedEventArgs e)
    {
        if(busy||closed||!device.Status.Paired)return;busy=true;SetFeedback("正在移除预览操作授权…");RefreshButtons();
        try{await device.RevokePeerAsync(lifetime.Token);if(!closed){ClearInvitation();consent.IsChecked=false;SetFeedback("预览操作授权已移除，当前仅观看。");}}
        catch(Exception failure){if(!closed)SetFeedback(FriendlyFailure(failure),true);}
        finally{busy=false;if(!closed){UpdateDeviceState();RefreshButtons();}}
    }
    private void OnDeviceChanged()
    {if(!closed&&!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(new Action(()=>{if(!closed){UpdateDeviceState();RefreshButtons();}}));}
    private void UpdateDeviceState()
    {
        var snapshot=device.Status;localFingerprint.Text=PreviewPairingVisuals.Fingerprint(snapshot.LocalFingerprint);
        deviceState.Text=!snapshot.Started?(busy?"正在准备预览连接":"预览连接尚未准备"):snapshot.Paired?"已连接预览设备 · 默认仅观看":"独立预览连接 · 仅观看";
        peerCard.Visibility=snapshot.Paired?Visibility.Visible:Visibility.Collapsed;peerName.Text="已允许的设备 · "+snapshot.PeerName;
        peerFingerprint.Text=PreviewPairingVisuals.Fingerprint(snapshot.PeerFingerprint);
        if(!busy&&!string.IsNullOrWhiteSpace(snapshot.Error))SetFeedback(snapshot.Error,true);
    }
    private void UpdateExpiry()
    {
        if(closed)return;
        if(invitation is{} current)
        {
            var remaining=current.ExpiresAt-DateTimeOffset.UtcNow;
            if(remaining<=TimeSpan.Zero){ClearInvitation();SetFeedback("邀请码已到期，请重新生成。");}
            else{var seconds=(int)Math.Ceiling(remaining.TotalSeconds);expires.Text=$"有效期 {seconds/60:00}:{seconds%60:00}";}
        }
        if(parsed is{} incoming&&incoming.ExpiresAt<=DateTimeOffset.UtcNow)
        {parsed=null;parsedCode="";consent.IsChecked=false;SetFeedback("对方邀请码已到期，请重新生成后粘贴。",true);}
        RefreshButtons();
    }
    private void ClearInvitation(){invitation=null;invitationCode.Clear();invitationCode.Visibility=Visibility.Collapsed;expires.Text="";}
    private void RefreshButtons()
    {
        var ready=!closed&&!busy&&device.Status.Started;
        generate.IsEnabled=!closed&&!busy;generate.Content=!device.Status.Started?"重试准备":invitation is null?"生成邀请码":"重新生成";
        copy.IsEnabled=ready&&invitation is not null&&invitation.ExpiresAt>DateTimeOffset.UtcNow;
        paste.IsEnabled=otherCode.IsEnabled=consent.IsEnabled=ready;
        connect.IsEnabled=ready&&parsed is not null&&consent.IsChecked==true&&parsed.ExpiresAt>DateTimeOffset.UtcNow;
        connect.Content=busy?"正在处理…":"确认连接";revoke.IsEnabled=ready&&device.Status.Paired;
    }
    private void SetFeedback(string text,bool failure=false)
    {status.Text=text;status.Foreground=PreviewPairingVisuals.Brush(failure?palette.Accent:palette.Muted);}
    internal static string FriendlyFailure(Exception failure)=>failure switch
    {
        OperationCanceledException=>"连接已取消或已到期，请重新生成邀请码后重试。",
        AuthenticationException=>"对方尚未确认或邀请码已到期，请重新生成，并在双方本机确认。",
        SocketException=>"无法连接对方，请检查两台电脑的网络连接，再使用新的邀请码。",
        CryptographicException or UnauthorizedAccessException=>"设备安全资料暂不可用，请在本机允许访问后重试。",
        InvalidDataException or ArgumentException=>"邀请码无效或权限不符，请使用对方新生成的预览邀请码。",
        IOException=>"设备资料暂不可用，请关闭其他预览连接窗口后重试。",
        _=>"未能完成连接，请在本机重新核对邀请码和设备指纹后重试。"
    };
}

internal static class PreviewPairingVisuals
{
    internal static SolidColorBrush Brush(string hex)=>new((Color)ColorConverter.ConvertFromString(hex));
    internal static TextBlock Text(string text,double size,string color,bool strong=false,Thickness? margin=null)=>
        new(){Text=text,FontSize=size,Foreground=Brush(color),FontWeight=strong?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=margin??new Thickness(0)};
    internal static Border Card(UIElement child,PreviewControlPalette palette)=>new(){Child=child,Padding=new Thickness(16),Margin=new Thickness(0,0,0,12),CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1),BorderBrush=Brush(palette.Line),Background=Brush(palette.Surface)};
    internal static TextBox TextBox(PreviewControlPalette palette,bool readOnly=false)=>new(){IsReadOnly=readOnly,TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Consolas"),FontSize=12,Padding=new Thickness(10),BorderThickness=new Thickness(1),BorderBrush=Brush(palette.Line),Background=Brush(palette.Soft),Foreground=Brush(palette.Text),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=40};
    internal static string Fingerprint(string? value)
    {
        if(value is not{Length:64}||!value.All(Uri.IsHexDigit))return "准备完成后显示";
        return string.Join(" ",Enumerable.Range(0,8).Select(index=>value.Substring(index*8,8).ToUpperInvariant()));
    }
    internal static Button Button(string text,PreviewControlPalette palette,bool primary=false)
    {
        var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(9));
        foreach(var property in new[]{Border.BackgroundProperty,Border.BorderBrushProperty,Border.BorderThicknessProperty,Border.PaddingProperty})
            border.SetBinding(property,new Binding(property.Name){RelativeSource=RelativeSource.TemplatedParent});
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(presenter);
        var style=new Style(typeof(Button));style.Setters.Add(new Setter(Control.TemplateProperty,new ControlTemplate(typeof(Button)){VisualTree=border}));
        foreach(var (property,value,opacity) in new[]{(UIElement.IsMouseOverProperty,true,.86),(System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty,true,.74),(UIElement.IsEnabledProperty,false,.45)})
        {var trigger=new Trigger{Property=property,Value=value};trigger.Setters.Add(new Setter(UIElement.OpacityProperty,opacity));style.Triggers.Add(trigger);}
        return new(){Content=text,Style=style,FontSize=12,MinHeight=36,Padding=new Thickness(14,8,14,8),BorderThickness=new Thickness(1),BorderBrush=Brush(primary?palette.Button:palette.Line),
            Background=Brush(primary?palette.Button:palette.Soft),Foreground=Brush(primary?palette.ButtonText:palette.Text),Cursor=System.Windows.Input.Cursors.Hand};
    }
}
