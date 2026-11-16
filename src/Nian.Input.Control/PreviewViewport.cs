namespace Nian.Input.Control;

public static class PreviewViewport
{
    public static bool TryMap(double viewportWidth,double viewportHeight,int imageWidth,int imageHeight,double x,double y,out double u,out double v)
    {
        u=v=0;
        if(!double.IsFinite(viewportWidth)||!double.IsFinite(viewportHeight)||viewportWidth<=0||viewportHeight<=0||
            imageWidth<=0||imageHeight<=0||!double.IsFinite(x)||!double.IsFinite(y))return false;
        var scale=Math.Min(viewportWidth/imageWidth,viewportHeight/imageHeight);
        var width=imageWidth*scale;var height=imageHeight*scale;
        var left=(viewportWidth-width)/2;var top=(viewportHeight-height)/2;
        if(!double.IsFinite(scale)||scale<=0||x<left||x>=left+width||y<top||y>=top+height)return false;
        u=(x-left)/width;v=(y-top)/height;return true;
    }
}
