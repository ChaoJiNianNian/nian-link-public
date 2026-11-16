namespace Nian.Input.Control;

public readonly record struct PreviewPointerAction(PreviewInputKind Kind,double U,double V,int Code=0);

/// <summary>Per-viewer button ledger and motion pacing. Coordinates are normalized within the image, never the letterbox.</summary>
public sealed class PreviewPointerGesture
{
    private int? button;
    private double u,v;
    private long? movedAt;
    public bool IsDragging=>button.HasValue;
    public int? HeldButton=>button;

    public bool TryDown(int code,double x,double y,out PreviewPointerAction action)
    {
        action=default;
        if(button.HasValue||code is <0 or >1||!Valid(x,y))return false;
        button=code;u=x;v=y;movedAt=null;
        action=new(PreviewInputKind.PointerDown,u,v,code);return true;
    }
    public bool TryMove(double x,double y,long nowMilliseconds,out PreviewPointerAction action)
    {
        action=default;
        if(!Valid(x,y))return false;
        u=x;v=y;
        // Keep the newest valid position even if it is not sent. A capture-loss
        // release must not use an older point just because motion was paced.
        if(movedAt is{} previous&&(nowMilliseconds<previous||nowMilliseconds-previous<34))return false;
        movedAt=nowMilliseconds;action=new(PreviewInputKind.PointerMove,u,v);return true;
    }
    public bool TryUp(int code,double x,double y,out PreviewPointerAction action)
    {
        action=default;
        if(button!=code||!Valid(x,y))return false;
        button=null;u=x;v=y;action=new(PreviewInputKind.PointerUp,u,v,code);return true;
    }
    public bool TryCancel(out PreviewPointerAction action)
    {
        action=default;
        if(button is not{} held)return false;
        button=null;action=new(PreviewInputKind.PointerUp,u,v,held);return true;
    }
    public void Reset(){button=null;u=v=0;movedAt=null;}
    private static bool Valid(double x,double y)=>double.IsFinite(x)&&double.IsFinite(y)&&x is >=0 and <=1&&y is >=0 and <=1;
}
