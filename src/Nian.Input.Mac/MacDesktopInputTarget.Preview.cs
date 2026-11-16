using Nian.Input.Control;

namespace Nian.Input.Mac;

public sealed partial class MacDesktopInputTarget
{
    public void ApplyPreview(ResolvedPreviewInput input,long expectedConsentVersion)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            RefreshSafetyCore();
            if(!armed||consentVersion!=expectedConsentVersion)throw new InvalidOperationException("本机桌面控制许可已改变。");
            if(!ValidPreview(input)){Disarm("预览操作无效，控制已停止。");throw new ArgumentException("Invalid positioned preview action.");}
            var failure=NativeFailure(out var displays, preview: true);
            if(failure.Length!=0||!displays.Any(d=>d.Matches(input.Geometry)))
            {Disarm("预览源与当前显示器不一致，控制已停止。");throw new InvalidOperationException("Preview display geometry changed.");}
            var point=new MacDesktopPoint(input.X,input.Y);
            void Emit(InputKind kind,int code=0,int delta=0)=>ApplyCore(new(input.Sequence,kind,code,Delta:delta),point,input.Geometry);
            void Click(int button)
            {
                Emit(InputKind.ButtonDown,button);
                Emit(InputKind.ButtonUp,button);
            }
            // One resolved command owns the target lock through both down and up.
            // Any failed native post uses the existing ledger retry/disarm path.
            switch(input.Kind)
            {
                case PreviewInputKind.LeftClick:Click(0);break;
                case PreviewInputKind.RightClick:Click(1);break;
                case PreviewInputKind.DoubleClick:Click(0);Click(0);break;
                case PreviewInputKind.PointerDown:Emit(InputKind.ButtonDown,input.Code);break;
                case PreviewInputKind.PointerMove:Emit(InputKind.MoveRelative);break;
                case PreviewInputKind.PointerUp:Emit(InputKind.ButtonUp,input.Code);break;
                case PreviewInputKind.Wheel:Emit(InputKind.Wheel,delta:input.Delta);break;
                case PreviewInputKind.KeyPress:Emit(InputKind.KeyDown,input.Code);Emit(InputKind.KeyUp,input.Code);break;
            }
        }
    }
    private static bool ValidPreview(ResolvedPreviewInput value)
    {
        var g=value.Geometry;
        if(g?.IsValid!=true||!double.IsFinite(value.X)||!double.IsFinite(value.Y)||value.X<g.Left||value.X>g.Left+g.Width-1||value.Y<g.Top||value.Y>g.Top+g.Height-1)return false;
        var u=g.Width==1?0:(value.X-g.Left)/(g.Width-1);var v=g.Height==1?0:(value.Y-g.Top)/(g.Height-1);
        if(value.Kind==PreviewInputKind.KeyPress)u=v=0;
        return PreviewInputValidation.Command(new(value.Sequence,new string('0',32),new string('0',64),value.Kind,u,v,value.Code,value.Delta));
    }
}
