using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using Nian.Input.Control;
using Nian.Input.Security;
using static DesktopToolbox.Mac.Views.MacPreviewPairingStyle;

namespace DesktopToolbox.Mac.Views;

/// <summary>A separate preview permission. Data pairing and ordinary Deskflow remain untouched.</summary>
public sealed class MacPreviewPairingWindow : Window
{
    private readonly MacPreviewControlRuntime runtime;
    private readonly CancellationTokenSource lifetime=new();
    private readonly DispatcherTimer expiry=new(){Interval=TimeSpan.FromMilliseconds(500)};
    private readonly TextBlock connection=Text("正在准备独立预览连接…");
    private readonly TextBlock feedback=Text("",12);
    private readonly TextBlock outgoingHint=Text("连接码仅用于本次配对，两分钟后失效",12);
    private readonly TextBlock incomingHint=Text("粘贴对方的连接码，然后核对完整安全指纹",12);
    private readonly SelectableTextBlock localFingerprint=Fingerprint("PreviewLocalFingerprint");
    private readonly SelectableTextBlock remoteFingerprint=Fingerprint("PreviewRemoteFingerprint");
    private readonly TextBox code=new(){Name="PreviewInvitationCode",PlaceholderText="在这里粘贴另一台电脑的连接码",AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxLength=4096,MinHeight=70,MaxHeight=110,FontSize=12};
    private readonly CheckBox verified=new(){Name="PreviewFingerprintVerified",Content="我已核对完整指纹，允许双方预览操作",FontSize=12};
    private readonly Button copy=Button("生成并复制连接码","PreviewCreateCode");
    private readonly Button pair=Button("核对一致，连接设备","PreviewConfirmPairing");
    private readonly Button retry=Button("重新准备","PreviewPrepareConnection");
    private readonly Button revoke=Button("断开预览操作配对","PreviewRevokePeer");
    private readonly Border localCard,remoteCard;
    private readonly StackPanel revokeRow;
    private DeviceInvitation? outgoing;
    private bool busy,closed;
    public MacPreviewPairingWindow(MacPreviewControlRuntime runtime)
    {
        this.runtime=runtime;
        Title="连接预览操作设备 · 年年互联";Width=560;Height=720;MinWidth=460;MinHeight=480;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;CanResize=true;
        var heading=Text("连接预览操作",23);heading.FontWeight=FontWeight.SemiBold;
        var note=Text("只允许在放大预览里点击、拖动和滚动。每次操作仍需手动开启。",12);
        var identityLabel=Text("这台 Mac 的安全指纹 · SHA-256",12);
        localCard=Card(Stack(Text("让另一台电脑连接我",16),identityLabel,localFingerprint,copy,outgoingHint));
        remoteCard=Card(Stack(Text("连接另一台电脑",16),code,incomingHint,remoteFingerprint,verified,pair));
        revokeRow=Stack(revoke);revokeRow.IsVisible=false;
        var content=Stack(heading,note,connection,retry,localCard,remoteCard,feedback,revokeRow);content.Spacing=14;content.Margin=new(24,20,24,24);
        Content=new ScrollViewer{Content=content,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
        copy.Click+=async(_,_)=>await CopyInvitationAsync();
        pair.Click+=async(_,_)=>await PairClickedAsync();
        retry.Click+=async(_,_)=>await PrepareAsync();
        revoke.Click+=async(_,_)=>await RevokeAsync();
        code.TextChanged+=(_,_)=>{verified.IsChecked=false;feedback.Text="";ReadInvitation();};
        verified.IsCheckedChanged+=(_,_)=>ReadInvitation();
        expiry.Tick+=(_,_)=>{ReadInvitation();RefreshOutgoing();Render();};
        runtime.Changed+=OnRuntimeChanged;
        PropertyChanged+=(_,args)=>{if(args.Property==ActualThemeVariantProperty)ApplyPalette();};
        Opened+=async(_,_)=>{ApplyPalette();expiry.Start();await PrepareAsync();};
        Closed+=(_,_)=>
        {
            closed=true;expiry.Stop();runtime.Changed-=OnRuntimeChanged;lifetime.Cancel();runtime.CancelInvitation();
            outgoing=null;code.Text="";lifetime.Dispose();
        };
        Render();ApplyPalette();
    }
    private async Task PrepareAsync()
    {
        if(closed||busy)return;busy=true;feedback.Text="";Render();
        try{await runtime.InitializeAsync(lifetime.Token);}
        catch(OperationCanceledException){}
        catch{if(!closed)feedback.Text="连接尚未准备好，请检查局域网与 Mac 权限后重试";}
        finally{if(!closed){busy=false;Render();}}
    }
    private async Task CopyInvitationAsync()
    {
        if(closed||busy)return;
        try
        {
            runtime.RequireLocalApproval();outgoing=runtime.CreateInvitation();
            var clipboard=Clipboard??throw new InvalidOperationException();
            await clipboard.SetTextAsync(outgoing.ToCode());
            if(!closed){feedback.Text="已复制。在另一台电脑的预览连接页粘贴，并核对这台 Mac 的完整指纹";RefreshOutgoing();}
        }
        catch{if(!closed)feedback.Text="未能复制连接码，请在这台 Mac 上重新点击";}
    }
    // PairAsync must be invoked on this actual local Click stack, before the first await.
    private async Task PairClickedAsync()
    {
        if(closed||busy||verified.IsChecked!=true)return;
        var view=MacPreviewInvitationPresentation.Read(code.Text,DateTimeOffset.UtcNow);
        if(view.Invitation is null){ReadInvitation();return;}
        Task request;
        try{request=runtime.PairAsync(code.Text!.Trim(),lifetime.Token);}
        catch{feedback.Text="请在这台 Mac 上亲自点击确认，且先准备连接";return;}
        busy=true;feedback.Text="正在连接，请在另一台电脑核对并允许…";Render();
        try{await request;if(!closed){code.Text="";verified.IsChecked=false;feedback.Text="设备已连接。返回预览窗口，手动点“开始操作”";}}
        catch(OperationCanceledException){if(!closed)feedback.Text="此次连接已取消，可重新生成连接码";}
        catch{if(!closed)feedback.Text="未能完成连接，请检查对方是否在线、连接码是否过期，以及两端是否都已确认";}
        finally{if(!closed){busy=false;Render();}}
    }
    private async Task RevokeAsync()
    {
        if(closed||busy)return;
        Task request;
        try{runtime.RequireLocalApproval();request=runtime.RevokePeerAsync(lifetime.Token);}
        catch{feedback.Text="请在这台 Mac 上亲自点击断开";return;}
        busy=true;Render();
        try{await request;if(!closed)feedback.Text="预览操作配对已断开，日常共享不受影响";}
        catch(OperationCanceledException){}
        catch{if(!closed)feedback.Text="断开尚未完成，请重试";}
        finally{if(!closed){busy=false;Render();}}
    }
    private void ReadInvitation()
    {
        if(closed)return;
        var view=MacPreviewInvitationPresentation.Read(code.Text,DateTimeOffset.UtcNow);
        incomingHint.Text=view.Message;remoteFingerprint.Text=view.Invitation?.Fingerprint??"";remoteFingerprint.IsVisible=view.Invitation is not null;
        verified.IsEnabled=!busy&&view.Invitation is not null;
        if(view.Invitation is null)verified.IsChecked=false;
        pair.IsEnabled=!busy&&runtime.Status.Started&&view.Invitation is not null&&verified.IsChecked==true;
    }
    private void RefreshOutgoing()
    {
        if(outgoing is not{} invitation)return;
        var remaining=invitation.ExpiresAt-DateTimeOffset.UtcNow;
        outgoingHint.Text=remaining>TimeSpan.Zero?$"连接码已复制 · {Math.Ceiling(remaining.TotalSeconds):0} 秒后失效":"连接码已过期，请重新生成并复制";
    }
    private void OnRuntimeChanged()=>Dispatcher.UIThread.Post(()=>{if(!closed)Render();});
    private void Render()
    {
        if(closed)return;var status=runtime.Status;
        connection.Text=status.Paired?$"已连接 {status.PeerName} · 当前仅观看":status.Error??status.Message;
        localFingerprint.Text=status.LocalFingerprint??"准备连接后显示";
        copy.IsEnabled=!busy&&status.Started;code.IsEnabled=!busy;
        retry.IsVisible=!status.Started;retry.IsEnabled=!busy;revokeRow.IsVisible=status.Paired;revoke.IsEnabled=!busy;
        ReadInvitation();
    }
    private void ApplyPalette()
    {
        var palette=PreviewControlPalette.ForDark(ActualThemeVariant==ThemeVariant.Dark);
        ApplyWindow(this,palette,localCard,remoteCard);ApplyButton(copy,palette);ApplyButton(pair,palette,true);ApplyButton(retry,palette);ApplyButton(revoke,palette);
        connection.Foreground=localFingerprint.Foreground=remoteFingerprint.Foreground=Brush(palette.Text);
        feedback.Foreground=Brush(palette.Accent);outgoingHint.Foreground=incomingHint.Foreground=Brush(palette.Muted);
        code.Background=Brush(palette.Soft);code.Foreground=Brush(palette.Text);code.BorderBrush=Brush(palette.Line);
        foreach(var suffix in new[]{"","PointerOver","Focused","Disabled"})
        {code.Resources["TextControlBackground"+suffix]=Brush(palette.Soft);code.Resources["TextControlForeground"+suffix]=Brush(palette.Text);code.Resources["TextControlBorderBrush"+suffix]=Brush(palette.Line);}
    }
}
