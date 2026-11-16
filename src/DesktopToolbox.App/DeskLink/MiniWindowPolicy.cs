namespace DesktopToolbox.App.DeskLink;

public readonly record struct MiniRect(int Left, int Top, int Width, int Height)
{
    public int Right => checked(Left + Width);
    public int Bottom => checked(Top + Height);
}
public readonly record struct MiniRememberedPosition(string Side, double X, double Y);

public static class MiniWindowPolicy
{
    public static MiniRect Place(MiniRect work, double dpi, double width, double height, string side, double x, double y)
    {
        if (work.Width <= 0 || work.Height <= 0 || !double.IsFinite(dpi) || dpi is < 48 or > 768
            || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
            || side is not ("left" or "right" or "none") || !double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentException("小窗工作区或位置无效。");
        var scale=dpi/96d;
        var margin=Math.Min((int)Math.Round(8*scale),Math.Min((work.Width-1)/2,(work.Height-1)/2));
        var w=Math.Clamp((int)Math.Round(width*scale),1,work.Width-margin*2);
        var h=Math.Clamp((int)Math.Round(height*scale),1,work.Height-margin*2);
        var freeX=work.Width-margin*2-w;
        var freeY=work.Height-margin*2-h;
        var left=side=="left" ? work.Left+margin : side=="right" ? work.Right-margin-w : work.Left+margin+(int)Math.Round(freeX*Math.Clamp(x,0,1));
        return new(left,work.Top+margin+(int)Math.Round(freeY*Math.Clamp(y,0,1)),w,h);
    }
    public static MiniRememberedPosition Remember(MiniRect work,double dpi,MiniRect current)
    {
        if (work.Width<=0 || work.Height<=0 || !double.IsFinite(dpi) || dpi is < 48 or > 768) throw new ArgumentException("小窗工作区无效。");
        var threshold=(int)Math.Round(32*dpi/96d);
        var leftDistance=Math.Abs((long)current.Left-work.Left);
        var rightDistance=Math.Abs((long)current.Right-work.Right);
        var side=leftDistance<=threshold && leftDistance<=rightDistance ? "left" : rightDistance<=threshold ? "right" : "none";
        var margin=Math.Min((int)Math.Round(8*dpi/96d),Math.Min((work.Width-1)/2,(work.Height-1)/2));
        var freeX=work.Width-margin*2-current.Width;
        var freeY=work.Height-margin*2-current.Height;
        return new(side,freeX<=0 ? .5 : Math.Clamp((current.Left-work.Left-margin)/(double)freeX,0,1),freeY<=0 ? .5 : Math.Clamp((current.Top-work.Top-margin)/(double)freeY,0,1));
    }
    public static bool ShouldSubscribe(bool enabled,bool miniVisible,bool collapsed,bool detachedVisible)
        => enabled && ((miniVisible && !collapsed) || detachedVisible);
}
