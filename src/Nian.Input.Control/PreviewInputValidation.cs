namespace Nian.Input.Control;

public static class PreviewInputValidation
{
    public static bool Command(PreviewInputCommand value)=>value.Sequence>0 &&
        value.FrameId is {Length:32} && value.FrameId.All(Uri.IsHexDigit) &&
        value.ContentSha256 is {Length:64} && value.ContentSha256.All(Uri.IsHexDigit) &&
        double.IsFinite(value.U)&&double.IsFinite(value.V)&&value.U is >=0 and <=1&&value.V is >=0 and <=1&&
        value.Kind switch
        {
            PreviewInputKind.LeftClick or PreviewInputKind.RightClick or PreviewInputKind.DoubleClick or PreviewInputKind.PointerMove=>value.Code==0&&value.Delta==0,
            PreviewInputKind.PointerDown or PreviewInputKind.PointerUp=>value.Code is >=0 and <=2&&value.Delta==0,
            PreviewInputKind.Wheel=>value.Code==0&&value.Delta!=0&&Math.Abs((long)value.Delta)<=12000,
            PreviewInputKind.KeyPress=>value.U==0&&value.V==0&&value.Delta==0&&value.Code is (int)InputKey.Enter or (int)InputKey.Tab or (int)InputKey.Escape,
            _=>false
        };
}
