using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace DesktopToolbox.Mac.Views;

/// <summary>A small independent window visible in the controlled Mac's captured desktop.</summary>
public sealed class MacRemotePreviewReturnWindow : Window
{
    // The capture helper includes only this exact window from this application's PID.
    public const string CaptureTitle="NianPreviewReturnOverlay";
    private readonly Button button;
    private readonly Func<string,Task> returnInput;
    private readonly Func<bool>? isUnconfirmed;
    private bool pending, closed;
#if NIAN_MAC_RETURN_RESEARCH
    private readonly bool persistentResearch;
    private bool researchAllowClose, researchCollapsed;
    private Button? researchFold, researchDismiss;
    private readonly Func<Task<bool>>? researchReturnAttempt;
    private readonly Action? researchDismissAction;
    private bool researchClosed, researchStopped;
    private long researchRevision;
#endif
    public string SessionId { get; }

    public MacRemotePreviewReturnWindow(string sessionId,Func<string,Task> returnInput,bool candidateBlue=false,Func<bool>? isUnconfirmed=null
#if NIAN_MAC_RETURN_RESEARCH
        ,bool persistentResearch=false,Func<Task<bool>>? researchReturnAttempt=null,Action? researchDismissAction=null
#endif
        )
    {
        SessionId=sessionId;this.returnInput=returnInput;this.isUnconfirmed=isUnconfirmed;
        Title=CaptureTitle;Width=184;Height=52;CanResize=false;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
        WindowDecorations=WindowDecorations.None;WindowStartupLocation=WindowStartupLocation.Manual;
        TransparencyLevelHint=[WindowTransparencyLevel.Transparent];Background=Brushes.Transparent;
        button=new Button
        {
            Content="↩  归还键鼠", HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,
            HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center,
            FontSize=14,FontWeight=FontWeight.SemiBold,Foreground=Brushes.White,
            Background=new SolidColorBrush(Color.Parse(candidateBlue?"#356FEB":"#DD263C5C")),BorderBrush=new SolidColorBrush(Color.Parse(candidateBlue?"#356FEB":"#6684B4F7")),
            BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(16)
        };
        button.Click+=async (_,_)=>await ReturnAsync();
        Closed+=(_,_)=>closed=true;
#if NIAN_MAC_RETURN_RESEARCH
        this.persistentResearch=persistentResearch;
        this.researchReturnAttempt=researchReturnAttempt;
        this.researchDismissAction=researchDismissAction;
        if(persistentResearch)
        {
            button.Content="连接及归还未确认\n此处不发送归还"; button.Width=190; Width=260; Height=68;
            researchFold=new Button { Content="收起", Width=68 };
            ToolTip.SetTip(researchFold,researchReturnAttempt is null?"未确认；点击展开查看，研究入口不发送归还命令。":"未确认；展开可尝试最近观察到的会话，尝试不能确认键鼠已归还。");
            researchFold.Click+=(_,_)=>ToggleResearchCollapsed();
            researchDismiss=new Button { Content="关闭提示",Width=68,IsVisible=false };
            researchDismiss.Click+=(_,_)=>researchDismissAction?.Invoke();
            var controls=new StackPanel { Orientation=Orientation.Horizontal, Children={button,researchFold} };
            Content=new StackPanel { Children={controls,researchDismiss} };
            Closing+=(_,e)=>{if(!researchAllowClose)e.Cancel=true;};
            Closed+=(_,_)=>researchClosed=true;
        }
        else Content=button;
#else
        Content=button;
#endif
    }

#if NIAN_MAC_RETURN_RESEARCH
    internal bool IsResearchEntry => persistentResearch;
    internal bool IsResearchClosed => researchClosed;
    internal void ResearchBindingChanged(bool hasSession)
    {
        researchRevision++;
        researchStopped=false;
        if(!pending)button.IsEnabled=true;
        if(researchDismiss is not null)researchDismiss.IsVisible=false;
        if(researchFold is not null)ToolTip.SetTip(researchFold,researchReturnAttempt is null?"未确认；点击展开查看，研究入口不发送归还命令。":hasSession?"未确认；展开可尝试最近观察到的会话，尝试不能确认键鼠已归还。":"无会话编号，暂不能发送归还；请检查键鼠所在设备。");
        Height=68;
        if(researchReturnAttempt is not null)
        {
            button.Content=hasSession?"归还未确认\n尝试已知会话":"无会话编号 · 未确认";
            ToolTip.SetTip(button,hasSession?"仅能尝试最近观察到的会话；该会话可能已过期，尝试不证明键鼠已归还。":"当前没有可尝试的会话编号，请检查键鼠所在设备。");
        }
    }
    internal void PresentResearchStopped()
    {
        researchRevision++;
        researchStopped=true;button.IsEnabled=false;
        button.Content="连接已停止或不可用\n键鼠未确认";
        ToolTip.SetTip(button,"当前连接无法发送归还，请检查两台设备上的键鼠。关闭提示不代表键鼠已归还。");
        if(researchFold is not null)ToolTip.SetTip(researchFold,"连接已停止或不可用，不能在此归还；请检查两台设备上的键鼠。关闭提示不代表键鼠已归还。");
        if(researchDismiss is not null)researchDismiss.IsVisible=true;
        Height=104;
    }
    internal void SetResearchDismissAvailable(bool available)
    {
        if(researchDismiss is not null)researchDismiss.IsEnabled=available;
    }
    private void ToggleResearchCollapsed()
    {
        if(!persistentResearch || researchFold is null)return;
        researchCollapsed=!researchCollapsed; button.IsVisible=!researchCollapsed;
        researchFold.Content=researchCollapsed?"未确认":"收起"; Width=researchCollapsed?68:260;
    }
    internal bool ReleaseResearch()
    {
        if(!persistentResearch)throw new InvalidOperationException("Not a research entry.");
        researchAllowClose=true;
        try { Close(); return researchClosed; }
        finally { researchAllowClose=false; }
    }
#endif
    private async Task ReturnAsync()
    {
#if NIAN_MAC_RETURN_RESEARCH
        if(persistentResearch)
        {
            if(researchStopped)return;
            if(researchReturnAttempt is not null)
            {
                if(pending)return;
                var revision=researchRevision;pending=true;button.IsEnabled=false;button.Content="正在尝试 · 未确认";
                try
                {
                    var attempted=await researchReturnAttempt();
                    if(revision==researchRevision)button.Content=attempted?"尝试结束 · 尚未确认":"无会话编号 · 未确认";
                }
                catch { if(revision==researchRevision)button.Content="归还未确认 · 检查键鼠"; }
                finally
                {
                    pending=false;button.IsEnabled=!researchStopped;
                }
                return;
            }
            button.Content="连接及归还未确认\n此处不发送归还";
            ToolTip.SetTip(button,"键鼠状态尚未确认；此研究入口不发送归还命令。请检查键鼠所在设备。");
            return;
        }
#endif
        if(pending)return;
        pending=true;button.IsEnabled=false;button.Content="正在归还…";
        try
        {
            await returnInput(SessionId);
            Close();
            if(!closed)
            {
                button.Content="归还未确认 · 检查键鼠";
                ToolTip.SetTip(button,"归还窗口未能关闭，请检查键鼠所在设备。");
            }
        }
        catch(Exception)
        {
            var unconfirmed=false;var statusUnavailable=false;
            try { unconfirmed=isUnconfirmed?.Invoke()==true; } catch { statusUnavailable=true; }
            if(unconfirmed)
            {
                button.Content="尚未确认 · 检查键鼠";
                ToolTip.SetTip(button,"键鼠归还尚未确认，请检查键鼠所在设备；先前请求可能仍在处理。");
            }
            else if(statusUnavailable)
            {
                button.Content="状态不可用 · 未确认";
                ToolTip.SetTip(button,"无法读取键鼠归还状态，请检查键鼠所在设备；先前请求可能仍在处理。");
            }
            else
            {
                button.Content="归还未确认 · 检查键鼠";
                ToolTip.SetTip(button,"键鼠归还尚未确认，请检查键鼠所在设备；先前请求可能仍在处理。");
            }
        }
        finally { pending=false;button.IsEnabled=true; }
    }
}
