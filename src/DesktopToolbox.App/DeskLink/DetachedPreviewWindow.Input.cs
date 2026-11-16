using System.Windows.Input;
using Nian.Input.Control;

namespace DesktopToolbox.App.DeskLink;

internal sealed partial class DetachedPreviewWindow
{
    private readonly PreviewPointerGesture pointer=new();
    private void ConfigurePointerInput()
    {
        stage.MouseDown+=(_,e)=>
        {
            if(control?.IsActive!=true)return;
            var button=ButtonCode(e.ChangedButton);if(button<0)return;
            var point=e.GetPosition(stage);
            if(!Map(point.X,point.Y,out var u,out var v))return;
            if(!pointer.TryDown(button,u,v,out var action)){_ = StopControlAsync("鼠标按键状态变化，控制已停止");return;}
            e.Handled=true;stage.Focus();
            if(!Mouse.Capture(stage)){_ = StopControlAsync("无法捕获预览内拖动，控制已停止");return;}
            _ = SendPointerAsync(action);
        };
        stage.MouseMove+=(_,e)=>
        {
            if(control?.IsActive!=true)return;
            var point=e.GetPosition(stage);
            if(Map(point.X,point.Y,out var u,out var v)&&pointer.TryMove(u,v,Environment.TickCount64,out var action))_ = SendPointerAsync(action);
        };
        stage.MouseUp+=(_,e)=>
        {
            var button=ButtonCode(e.ChangedButton);if(pointer.HeldButton!=button)return;
            var point=e.GetPosition(stage);PreviewPointerAction action;
            var mapped=Map(point.X,point.Y,out var u,out var v);
            var release=mapped?pointer.TryUp(button,u,v,out action):pointer.TryCancel(out action);
            if(!release)return;e.Handled=true;
            if(stage.IsMouseCaptured)stage.ReleaseMouseCapture();
            _ = SendPointerAsync(action);
        };
        stage.LostMouseCapture+=async(_,_)=>
        {
            if(!pointer.TryCancel(out var release))return;
            _ = SendPointerAsync(release);await StopControlAsync("拖动捕获已丢失，控制已停止");
        };
        stage.MouseWheel+=async(_,e)=>
        {
            if(control?.IsActive!=true||e.Delta==0)return;
            var point=e.GetPosition(stage);if(!Map(point.X,point.Y,out var u,out var v))return;
            e.Handled=true;await control.SendAsync(PreviewInputKind.Wheel,u,v,delta:Math.Clamp(e.Delta,-12000,12000));RefreshControl();
        };
    }
    private bool Map(double x,double y,out double u,out double v)
    {
        u=v=0;
        return image.Source is not null&&PreviewViewport.TryMap(stage.ActualWidth,stage.ActualHeight,displayedWidth,displayedHeight,x,y,out u,out v);
    }
    private static int ButtonCode(MouseButton button)=>button switch{MouseButton.Left=>0,MouseButton.Right=>1,_=>-1};
    private async Task SendPointerAsync(PreviewPointerAction action)
    {
        if(control is not null)await control.SendAsync(action.Kind,action.U,action.V,action.Code);
        RefreshControl();
    }
    private void ResetPointerCapture(){pointer.Reset();if(stage.IsMouseCaptured)stage.ReleaseMouseCapture();}
    private async Task StopControlAsync(string reason)
    {
        ResetPointerCapture();
        if(handoff is not null)try{await handoff.StopAsync(reason);}catch{}
        else if(control is not null)try{await control.StopAsync(reason);}catch{ }
        RefreshControl();
    }
}
