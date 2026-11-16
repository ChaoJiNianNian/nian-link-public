using Nian.Input.Control;
using Nian.Input.Windows;

namespace DesktopToolbox.Windows.DeskSwitch.Collaboration;

public static class PreviewCapturePolicy
{
    /// <summary>Only unchanged native source snapshots may bind encoded pixels to desktop coordinates.</summary>
    public static PreviewSourceGeometry? BindGeometry(WindowsDesktopSourceGeometry? before, WindowsDesktopSourceGeometry? after, int imageWidth, int imageHeight)
    {
        if (before is null || before != after) return null;
        var geometry = before.ToPreviewGeometry(imageWidth, imageHeight);
        return geometry.IsValid ? geometry : null;
    }

    public static (int Width,int Height) Fit(int width,int height,int maximumWidth,int maximumHeight)
    {
        if (width<=0 || height<=0 || width>32768 || height>32768 || maximumWidth is < 1 or > 1920 || maximumHeight is < 1 or > 1080)
            throw new ArgumentOutOfRangeException(nameof(width),"预览尺寸超出范围。");
        var ratio=Math.Min(1d,Math.Min(maximumWidth/(double)width,maximumHeight/(double)height));
        return (Math.Max(1,(int)Math.Round(width*ratio,MidpointRounding.AwayFromZero)),Math.Max(1,(int)Math.Round(height*ratio,MidpointRounding.AwayFromZero)));
    }
}
