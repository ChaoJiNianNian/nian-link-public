using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;

namespace DesktopToolbox.Mac.Views;

public sealed partial class MacPreviewSurface
{
    private Guid? liveEpoch;
    private LiveBitmapImage? liveImage;
    private DateTimeOffset lastCapturedAt,lastLiveCapturedAt;
    private bool retainedLiveFrame,hasLiveTimestampFloor;
    private bool passiveGenerationSupported;
    private Guid? passiveRequestId;
    private PreviewSourceStatus? passiveSource;
    private long lastPassiveCaptureSequence;

    public void SetPassiveContext(bool supported,Guid? requestId,PreviewSourceStatus? source)
    {
        if(passiveGenerationSupported!=supported||passiveRequestId!=requestId||!Equals(passiveSource,source))
        {freshness.BeginGeneration();frameBytes=null;}
        if(passiveGenerationSupported!=supported||passiveRequestId!=requestId||passiveSource?.InstanceId!=source?.InstanceId)
            lastPassiveCaptureSequence=0;
        passiveGenerationSupported=supported;passiveRequestId=requestId;passiveSource=source;
    }

    /// <summary>A stable image identity prevents equal-size stream frames from invalidating layout.</summary>
    private sealed class LiveBitmapImage(Bitmap initial) : IImage
    {
        private Bitmap current=initial;
        public Size Size {get;}=initial.Size;
        public void Replace(Bitmap next)=>current=next;
        public void Draw(DrawingContext context,Rect sourceRect,Rect destRect)=>context.DrawImage(current,sourceRect,destRect);
    }

    public void BeginLivePresentation(Guid epoch)
    {
        if(disposed||epoch==Guid.Empty||liveEpoch==epoch)return;
        // Set the generation before notifying the owner: Clear raises FrameChanged.
        liveEpoch=epoch;freshness.Invalidate();Clear("正在建立实时画面…");
    }

    /// <summary>A successful call takes ownership of an already decoded image; rejection leaves ownership with the caller.</summary>
    public bool SetLiveBitmap(Guid epoch,LivePreviewCapturePacket packet,Bitmap decoded)
    {
        if(disposed||liveEpoch!=epoch||packet.Kind!=LivePreviewCaptureKind.Image||
            packet.Width!=decoded.PixelSize.Width||packet.Height!=decoded.PixelSize.Height)return false;
        var previous=bitmap;var changed=FrameStatus!="实时画面";
        bitmap=decoded;
        if(liveImage is null||liveImage.Size!=decoded.Size)
        {liveImage=new(decoded);image.Source=liveImage;}
        else {liveImage.Replace(decoded);image.InvalidateVisual();}
        frameBytes=null;displayedWidth=packet.Width;displayedHeight=packet.Height;
        if(packet.CapturedAtUtc>lastCapturedAt)lastCapturedAt=packet.CapturedAtUtc;
        lastLiveCapturedAt=packet.CapturedAtUtc;
        retainedLiveFrame=false;hasLiveTimestampFloor=true;
        message.IsVisible=false;FrameStatus="实时画面";previous?.Dispose();
        // Business state and toolbar layout change once, rather than at the stream's frame rate.
        if(changed)FrameChanged?.Invoke();
        return true;
    }

    public void EndLivePresentation(bool keepLastFrame,string text="当前仅观看画面")
    {
        if(liveEpoch is null)return;
        liveEpoch=null;freshness.Invalidate();
        if(keepLastFrame&&bitmap is not null&&lastLiveCapturedAt!=default)
        {
            retainedLiveFrame=true;FrameStatus="更新于 "+lastLiveCapturedAt.ToLocalTime().ToString("HH:mm:ss");
            FrameChanged?.Invoke();return;
        }
        retainedLiveFrame=false;Clear(text);
    }

    public void ClearLivePresentation(string text)
    {
        liveEpoch=null;retainedLiveFrame=false;freshness.Invalidate();Clear(text);
        // Retain the capture-time floor: a delayed poll must not resurrect an older image.
    }
}
