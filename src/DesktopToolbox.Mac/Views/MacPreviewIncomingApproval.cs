using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Nian.Input.Control;
using Nian.Input.Security;
using static DesktopToolbox.Mac.Views.MacPreviewPairingStyle;

namespace DesktopToolbox.Mac.Views;

internal sealed class MacPreviewIncomingApproval : Window
{
    private readonly Action requireLocalApproval;
    private readonly PairingApproval approval;
    private readonly CheckBox verified=new(){Name="PreviewIncomingVerified",Content="完整指纹一致，允许双方预览操作",FontSize=12};
    private readonly Button accept=Button("一致，允许连接","PreviewIncomingAccept");
    private readonly Button reject=Button("拒绝","PreviewIncomingReject");
    private readonly TextBlock feedback=Text("",12);
    private readonly DispatcherTimer expiry=new(){Interval=TimeSpan.FromMilliseconds(250)};
    private bool closed;
    private MacPreviewIncomingApproval(PairingApproval approval,Action requireLocalApproval,ThemeVariant theme)
    {
        this.approval=approval;this.requireLocalApproval=requireLocalApproval;RequestedThemeVariant=theme;
        Title="核对预览操作设备 · 年年互联";Width=520;Height=420;MinWidth=440;MinHeight=350;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;CanResize=true;
        var heading=Text($"允许 {approval.DeviceName} 连接？",21);heading.FontWeight=FontWeight.SemiBold;
        var fingerprint=Fingerprint("PreviewIncomingFingerprint");fingerprint.Text=approval.Fingerprint;
        var card=Card(Stack(Text("对方安全指纹 · SHA-256",12),fingerprint,Text($"实际连接来源：{approval.SourceAddress}:{approval.ReceiverPort}",12)));
        var actions=new Grid{ColumnDefinitions=new("*,Auto,Auto"),ColumnSpacing=10};Grid.SetColumn(reject,1);Grid.SetColumn(accept,2);actions.Children.Add(reject);actions.Children.Add(accept);
        var content=Stack(heading,Text("请与对方电脑显示的完整指纹核对。此授权只适用于预览操作，每次控制仍需手动开启。",12),card,verified,feedback,actions);content.Margin=new(24);content.Spacing=16;
        Content=new ScrollViewer{Content=content,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
        verified.IsCheckedChanged+=(_,_)=>Refresh();
        accept.Click+=(_,_)=>
        {
            if(closed||verified.IsChecked!=true||DateTimeOffset.UtcNow>=approval.ExpiresAt||approval.RequestedPermission!=DevicePermission.PreviewControl)return;
            try{requireLocalApproval();Close(true);}
            catch{feedback.Text="请在这台 Mac 上亲自点击允许，预览操作不能代为授权";}
        };
        reject.Click+=(_,_)=>Close(false);
        expiry.Tick+=(_,_)=>{if(DateTimeOffset.UtcNow>=approval.ExpiresAt)Close(false);else Refresh();};
        Opened+=(_,_)=>expiry.Start();Closed+=(_,_)=>{closed=true;expiry.Stop();};
        var palette=PreviewControlPalette.ForDark(theme==ThemeVariant.Dark);ApplyWindow(this,palette,card);ApplyButton(accept,palette,true);ApplyButton(reject,palette);feedback.Foreground=Brush(palette.Accent);Refresh();
    }
    private void Refresh()=>accept.IsEnabled=!closed&&verified.IsChecked==true&&DateTimeOffset.UtcNow<approval.ExpiresAt&&approval.RequestedPermission==DevicePermission.PreviewControl;
    internal static async Task<bool> ShowAsync(Window owner,PairingApproval approval,Action requireLocalApproval,CancellationToken token)
    {
        Dispatcher.UIThread.VerifyAccess();
        if(token.IsCancellationRequested||approval.RequestedPermission!=DevicePermission.PreviewControl||DateTimeOffset.UtcNow>=approval.ExpiresAt||
            approval.ReceiverPort is not(>=1 and <=65535)||!IPAddress.TryParse(approval.SourceAddress,out var source)||source.Equals(IPAddress.Any)||source.Equals(IPAddress.IPv6Any))return false;
        var dialog=new MacPreviewIncomingApproval(approval,requireLocalApproval,owner.ActualThemeVariant);
        using var cancellation=token.Register(()=>Dispatcher.UIThread.Post(()=>{if(!dialog.closed)dialog.Close(false);}));
        var accepted=await dialog.ShowDialog<bool>(owner);
        return accepted&&!token.IsCancellationRequested&&DateTimeOffset.UtcNow<approval.ExpiresAt;
    }
}
