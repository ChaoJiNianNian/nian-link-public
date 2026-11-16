#if NIAN_MAC_RETURN_RESEARCH
using System.Reflection;
using Avalonia.Controls;
using DesktopToolbox.Mac.Views;

internal static partial class Program
{
    private sealed partial class FixtureApp
    {
        private static Task VerifyStoppedEntryAsync()
        {
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
            var main=new MacMultiDeviceWindow(Path.Combine(Path.GetDirectoryName(resultPath)!,"stopped-"+Guid.NewGuid().ToString("N")),false,new FakeVault());
            var type=typeof(MacMultiDeviceWindow);
            var ensure=type.GetMethod("EnsureResearchReturnWindow",flags)!;
            var present=type.GetMethod("PresentResearchStopped",flags) ?? throw new InvalidOperationException("Stopped presentation missing.");
            var dismiss=type.GetMethod("DismissResearchReturn",flags) ?? throw new InvalidOperationException("Explicit dismissal missing.");
            var field=type.GetField("remoteReturn",flags)!;
            ensure.Invoke(main,null);
            var window=(MacRemotePreviewReturnWindow)field.GetValue(main)!;
            var button=(Button)typeof(MacRemotePreviewReturnWindow).GetField("button",flags)!.GetValue(window)!;
            var closeButton=(Button)typeof(MacRemotePreviewReturnWindow).GetField("researchDismiss",flags)!.GetValue(window)!;
            var fold=(Button)typeof(MacRemotePreviewReturnWindow).GetField("researchFold",flags)!.GetValue(window)!;
            var closed=false;
            window.Closed+=(_,_)=>closed=true;
            typeof(MacRemotePreviewReturnWindow).GetMethod("ToggleResearchCollapsed",flags)!.Invoke(window,null);
            present.Invoke(main,null);
            if(!window.IsVisible || button.Content is not string text || !text.Contains("已停止",StringComparison.Ordinal) || !text.Contains("未确认",StringComparison.Ordinal))
                throw new InvalidOperationException("Stopped entry does not visibly disclose its unconfirmed state.");
            if(!closeButton.IsVisible || closeButton.Content is not string closeLabel || !closeLabel.Contains("关闭提示",StringComparison.Ordinal) || window.Height<104)
                throw new InvalidOperationException("Stopped entry does not visibly offer dismissal.");
            if(ToolTip.GetTip(fold) is not string tip || !tip.Contains("不能在此归还",StringComparison.Ordinal))throw new InvalidOperationException("Folded stopped entry offers a stale return action.");
            typeof(MacRemotePreviewReturnWindow).GetMethod("ResearchBindingChanged",flags)!.Invoke(window,[true]);
            if(ToolTip.GetTip(fold) is not string activeTip || !activeTip.Contains("尝试",StringComparison.Ordinal))throw new InvalidOperationException("New active binding did not restore its fold hint.");
            typeof(MacRemotePreviewReturnWindow).GetMethod("ResearchBindingChanged",flags)!.Invoke(window,[false]);
            if(ToolTip.GetTip(fold) is not string emptyTip || !emptyTip.Contains("无会话编号",StringComparison.Ordinal))throw new InvalidOperationException("An empty binding falsely offers a return attempt.");
            present.Invoke(main,null);
            if(mode is "research-stopped-show-failure" or "research-stopped-hidden-veto" or "research-stopped-hide-throw")
            {
                EventHandler<WindowClosingEventArgs> veto=(_,e)=>{window.Hide();if(mode=="research-stopped-hide-throw")throw new InvalidOperationException("synthetic close exception");e.Cancel=true;};
                EventHandler showFail=(_,_)=>throw new InvalidOperationException("synthetic repeated show failure");
                window.Closing+=veto;
                if(mode=="research-stopped-show-failure")window.Opened+=showFail;
                dismiss.Invoke(main,[window]);
                if(!closed)
                {
                    if(!ReferenceEquals(field.GetValue(main),window))throw new InvalidOperationException("Hidden canceled window lost ownership.");
                    if(mode=="research-stopped-show-failure")
                    {
                        var feedback=(TextBlock)type.GetField("feedback",flags)!.GetValue(main)!;
                        if(feedback.Text?.Contains("无法显示",StringComparison.Ordinal)!=true)throw new InvalidOperationException("Repeated Show failure was not reported in the main window.");
                    }
                    else
                    {
                        if(!window.IsVisible)throw new InvalidOperationException("Canceled dismissal left the stopped warning inaccessible.");
                        ensure.Invoke(main,null);
                        if(!ReferenceEquals(field.GetValue(main),window))throw new InvalidOperationException("Existing entry was duplicated.");
                    }
                }
                else if(field.GetValue(main) is not null)throw new InvalidOperationException("Closed window remains owned.");
                window.Closing-=veto;
                window.Opened-=showFail;
                dismiss.Invoke(main,[window]);
            }
            else if(mode=="research-stopped-reentrant")
            {
                var called=0;
                EventHandler<WindowClosingEventArgs> nested=(_,_)=>{called++;dismiss.Invoke(main,[window]);};
                window.Closing+=nested;
                dismiss.Invoke(main,[window]);
                if(called!=1)throw new InvalidOperationException("Reentrant dismissal was not rejected.");
                window.Closing-=nested;
            }
            else
            {
                type.GetField("researchStopInProgress",flags)!.SetValue(main,true);
                closeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                if(closed || !ReferenceEquals(field.GetValue(main),window))throw new InvalidOperationException("Stop-in-progress allowed dismissal.");
                type.GetField("researchStopInProgress",flags)!.SetValue(main,false);
                closeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }
            if(!closed || field.GetValue(main) is not null)throw new InvalidOperationException("Explicit dismissal did not close the stopped entry.");
            if(mode=="research-stopped-old-dismiss")
            {
                ensure.Invoke(main,null);
                var replacement=(MacRemotePreviewReturnWindow)field.GetValue(main)!;
                present.Invoke(main,null);
                dismiss.Invoke(main,[window]);
                if(!replacement.IsVisible || !ReferenceEquals(field.GetValue(main),replacement))throw new InvalidOperationException("Old window dismissal closed the replacement entry.");
                dismiss.Invoke(main,[replacement]);
                if(field.GetValue(main) is not null)throw new InvalidOperationException("Replacement could not be dismissed.");
            }
            return Task.CompletedTask;
        }
    }
}
#endif
