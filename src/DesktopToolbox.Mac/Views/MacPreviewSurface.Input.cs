using Avalonia.Input;
using Nian.Input.Control;

namespace DesktopToolbox.Mac.Views;

public sealed partial class MacPreviewSurface
{
    private readonly PreviewPointerGesture pointer=new();
    private IPointer? capturedPointer;
    private void ConfigurePointerInput()
    {
        PointerPressed+=(_,e)=>
        {
            if(control?.IsActive!=true||e.Pointer.Type!=PointerType.Mouse)return;
            var button=e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
            {PointerUpdateKind.LeftButtonPressed=>0,PointerUpdateKind.RightButtonPressed=>1,_=>-1};
            if(button<0)return;
            var point=e.GetPosition(this);if(!Map(point.X,point.Y,out var u,out var v))return;
            if(!pointer.TryDown(button,u,v,out var action)){_ = StopControlAsync("鼠标按键状态变化，控制已停止");return;}
            e.Handled=true;Focus();capturedPointer=e.Pointer;e.Pointer.Capture(this);
            if(e.Pointer.Captured!=this){_ = StopControlAsync("无法捕获预览内拖动，控制已停止");return;}
            _ = SendPointerAsync(action);
        };
        PointerMoved+=(_,e)=>
        {
            if(control?.IsActive!=true||e.Pointer.Type!=PointerType.Mouse)return;
            var point=e.GetPosition(this);
            if(Map(point.X,point.Y,out var u,out var v)&&pointer.TryMove(u,v,Environment.TickCount64,out var action))_ = SendPointerAsync(action);
        };
        PointerReleased+=(_,e)=>
        {
            var button=e.InitialPressMouseButton switch{MouseButton.Left=>0,MouseButton.Right=>1,_=>-1};
            if(pointer.HeldButton!=button)return;
            var point=e.GetPosition(this);PreviewPointerAction action;
            var mapped=Map(point.X,point.Y,out var u,out var v);
            var release=mapped?pointer.TryUp(button,u,v,out action):pointer.TryCancel(out action);
            if(!release)return;e.Handled=true;ReleasePointerCapture();_ = SendPointerAsync(action);
        };
        PointerCaptureLost+=async(_,_)=>
        {
            capturedPointer=null;if(!pointer.TryCancel(out var release))return;
            _ = SendPointerAsync(release);await StopControlAsync("拖动捕获已丢失，控制已停止");
        };
        PointerWheelChanged+=async(_,e)=>
        {
            if(control?.IsActive!=true||!double.IsFinite(e.Delta.Y)||e.Delta.Y==0)return;
            var point=e.GetPosition(this);if(!Map(point.X,point.Y,out var u,out var v))return;
            var delta=(int)Math.Clamp(Math.Round(e.Delta.Y*120,MidpointRounding.AwayFromZero),-12000,12000);
            if(delta==0)return;e.Handled=true;await control.SendAsync(PreviewInputKind.Wheel,u,v,delta:delta);
        };
    }
    private bool Map(double x,double y,out double u,out double v)
    {
        u=v=0;
        return image.Source is not null&&PreviewViewport.TryMap(Bounds.Width,Bounds.Height,displayedWidth,displayedHeight,x,y,out u,out v);
    }
    private async Task SendPointerAsync(PreviewPointerAction action)
    {if(control is not null)await control.SendAsync(action.Kind,action.U,action.V,action.Code);}
    private void ReleasePointerCapture(){var previous=capturedPointer;capturedPointer=null;if(previous?.Captured==this)previous.Capture(null);}
    private void ResetPointerCapture(){pointer.Reset();ReleasePointerCapture();}
    internal async Task StopControlAsync(string reason)
    {ResetPointerCapture();if(control is not null)try{await control.StopAsync(reason);}catch{ }}
}
