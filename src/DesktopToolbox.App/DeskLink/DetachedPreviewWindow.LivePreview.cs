using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.DeskLink.Preview;
using Nian.Input.Control;

namespace DesktopToolbox.App.DeskLink;

internal sealed partial class DetachedPreviewWindow
{
    private LivePreviewPresenter<WpfLivePreviewDecoder.Frame>? livePresenter;
    private WriteableBitmap? liveSurface;
    private BitmapSource? passiveFrame;
    private PreviewFrame? passiveSource;
    private string passiveState="";
    private DateTimeOffset latestCapturedAt;
    private DateTimeOffset lastLiveCapturedAt;
    private Guid? passiveRequestId;
    private PreviewSourceStatus? passiveSourceStatus;
    private long passiveCaptureSequence;
    private readonly HashSet<Guid> retiredPassiveSources=[];
    private volatile bool sourceUnavailable;
    private bool hasPassiveFrame,hasPresentedLive,liveFailureObserved;
    private int liveViewAvailable;

    private void InitializeLivePresentation()
    {
        if(handoff?.UsesLivePreview!=true)return;
        livePresenter=new(WpfLivePreviewDecoder.DecodeAsync,action=>
        {
            if(Dispatcher.HasShutdownStarted||Dispatcher.HasShutdownFinished)throw new InvalidOperationException("The viewer dispatcher is closed.");
            Dispatcher.BeginInvoke(DispatcherPriority.Render,action);
        },PresentLiveFrame,IsCurrentLiveFrame,failed:_=>OnLiveDecodeFailed());
        handoff.LivePacketReceived+=OnLivePacket;
    }
    private bool IsCurrentLiveFrame(Guid epoch)=>Volatile.Read(ref liveViewAvailable)!=0&&!Volatile.Read(ref closed)&&
        handoff is{} session&&!session.LiveSourceFailed&&session.LiveOperationId==epoch&&
        !sourceUnavailable&&
        session.Phase is PreviewControlPhase.Preparing or PreviewControlPhase.Controlling;
    private void OnLivePacket(Guid epoch,LivePreviewCapturePacket packet)=>livePresenter?.Push(epoch,packet);
    private void PresentLiveFrame(Guid epoch,LivePreviewCapturePacket packet,WpfLivePreviewDecoder.Frame decoded)
    {
        // A successful Presenter callback transfers ownership, including an image made stale before UI dispatch.
        using(decoded)
        {
            if(!IsCurrentLiveFrame(epoch))return;
            if(liveSurface is null||liveSurface.PixelWidth!=decoded.Width||liveSurface.PixelHeight!=decoded.Height)
                liveSurface=new(decoded.Width,decoded.Height,96,96,PixelFormats.Pbgra32,null);
            liveSurface.WritePixels(new Int32Rect(0,0,decoded.Width,decoded.Height),decoded.Pixels,decoded.Stride,0);
            if(!ReferenceEquals(image.Source,liveSurface))image.Source=liveSurface;
            placeholder.Visibility=Visibility.Collapsed;
            if(caption.Text!="实时画面")caption.Text="实时画面";
            displayedWidth=decoded.Width;displayedHeight=decoded.Height;
            lastLiveCapturedAt=packet.CapturedAtUtc;
            if(packet.CapturedAtUtc>latestCapturedAt)latestCapturedAt=packet.CapturedAtUtc;
            hasPresentedLive=true;liveFailureObserved=false;
            passiveFrame=null;passiveSource=null;hasPassiveFrame=false;
            // Readiness and the input lease follow pixels that were actually written, never decode/arrival alone.
            handoff!.NotifyLiveFramePresented(epoch,packet);
        }
    }
    private bool HandlePassiveLiveFrame(BitmapSource? frame,string state,PreviewFrame? source)
    {
        if(handoff?.UsesLivePreview!=true)return false;
        if(pictureConnected==false)return true;
        if(sourceUnavailable)return true;
        var phase=handoff.Phase;
        if(frame is null)
        {
            if(phase==PreviewControlPhase.Viewing&&source?.State=="locked")
            {
                ClearLivePresentation("对方已锁屏，画面暂停");
                return true;
            }
            // A delayed passive off/loading result cannot terminate a healthy temporary stream.
            if(phase==PreviewControlPhase.Viewing&&!hasPresentedLive)
            {passiveFrame=null;passiveSource=null;hasPassiveFrame=false;ApplyDisplayedFrame(null,state,source);}
            return true;
        }
        var captured=source?.CapturedAtUtc??default;
        if(passiveRequestId is{} request)
        {
            // Source clocks are for display only; authenticated request and source
            // generations prove whether a passive frame belongs to this operation.
            if(source?.RequestId!=request||passiveSourceStatus?.Availability!="enabled"||source.SourceStatus!=passiveSourceStatus||
                source.CaptureSequence is not{} sequence||sequence<=passiveCaptureSequence)return true;
            passiveCaptureSequence=sequence;
        }
        else
        {
            // Compatibility for old view-only peers without generation metadata.
            if((hasPresentedLive||liveFailureObserved)&&latestCapturedAt!=default&&(captured==default||captured<=latestCapturedAt))return true;
            if(captured!=default)latestCapturedAt=captured;
        }
        passiveFrame=frame;passiveSource=source;passiveState=state;hasPassiveFrame=true;
        if(phase==PreviewControlPhase.Viewing)
        {hasPresentedLive=false;liveSurface=null;ApplyDisplayedFrame(frame,state,source);}
        return true;
    }
    private void RefreshLivePresentationState()
    {
        if(handoff?.UsesLivePreview!=true)return;
        if(pictureConnected==false){ClearLivePresentation(connectionHint);return;}
        if(handoff.LiveSourceFailed)
        {
            if(!liveFailureObserved){liveFailureObserved=true;ClearLivePresentation("实时画面已停止");}
            return;
        }
        liveFailureObserved=false;
        if(handoff.Phase!=PreviewControlPhase.Viewing||Volatile.Read(ref liveViewAvailable)==0)return;
        if(hasPresentedLive)
        {caption.Text=lastLiveCapturedAt==default?"画面已更新":"更新于 "+lastLiveCapturedAt.ToLocalTime().ToString("HH:mm:ss");return;}
        if(hasPassiveFrame)ApplyDisplayedFrame(passiveFrame,passiveState,passiveSource);
    }
    private void UpdateLiveVisibility()
    {
        if(handoff?.UsesLivePreview!=true)return;
        var available=!closed&&IsVisible&&WindowState!=WindowState.Minimized&&pictureConnected!=false;
        Volatile.Write(ref liveViewAvailable,available?1:0);
        if(!available)ClearLivePresentation("画面已暂停");
        else RefreshLivePresentationState();
    }
    private void ClearLivePresentation(string state)
    {
        liveSurface=null;passiveFrame=null;passiveSource=null;hasPassiveFrame=false;hasPresentedLive=false;lastLiveCapturedAt=default;
        ApplyDisplayedFrame(null,state,null);
        // Keep the timestamp floor: a delayed old passive poll must not resurrect cleared desktop pixels.
    }
    private void BeginNewLiveConnection()
    {
        // The prior peer's clock is not an ordering floor for a new connection.
        latestCapturedAt=default;passiveRequestId=null;passiveSourceStatus=null;passiveCaptureSequence=0;sourceUnavailable=false;
        retiredPassiveSources.Clear();
    }
    public void SetPassivePreviewContext(Guid? requestId,PreviewSourceStatus? sourceStatus)
    {
        if(closed||handoff?.UsesLivePreview!=true)return;
        if(passiveSourceStatus is{} old&&sourceStatus is{} incoming)
        {
            if(old.InstanceId==incoming.InstanceId)
            {
                if(incoming.Revision<old.Revision||incoming.Revision==old.Revision&&incoming.Availability!=old.Availability)return;
            }
            else
            {
                if(retiredPassiveSources.Contains(incoming.InstanceId))return;
                if(retiredPassiveSources.Count>=128)
                {
                    sourceUnavailable=true;ClearLivePresentation("来源状态不可用");
                    if(handoff.CanStop)_=handoff.StopAfterLiveFailureAsync("来源状态不可用，正在收回键鼠");
                    return;
                }
                retiredPassiveSources.Add(old.InstanceId);
            }
        }
        else if(passiveSourceStatus is not null&&sourceStatus is null&&pictureConnected==true)
        {
            sourceUnavailable=true;ClearLivePresentation("正在等待来源状态");
            if(handoff.CanStop)_=handoff.StopAfterLiveFailureAsync("来源状态不可用，正在收回键鼠");
            return;
        }
        var changedSource=passiveSourceStatus is{} previous&&sourceStatus is{} next&&previous!=next;
        if(passiveRequestId!=requestId||passiveSourceStatus?.InstanceId!=sourceStatus?.InstanceId)passiveCaptureSequence=0;
        passiveRequestId=requestId;passiveSourceStatus=sourceStatus;
        sourceUnavailable=requestId is not null&&sourceStatus is null||sourceStatus?.Availability is "disabled" or "locked" or "unavailable";
        if(changedSource||sourceUnavailable)
        {
            ClearLivePresentation(sourceStatus?.Availability=="locked"?"对方已锁屏，画面暂停":"对方画面不可用");
            if(handoff.CanStop)_=handoff.StopAfterLiveFailureAsync("对方画面不可用，正在收回键鼠");
        }
    }
    private void OnLiveDecodeFailed()
    {
        if(closed||handoff is null)return;
        liveFailureObserved=true;ClearLivePresentation("实时画面无法显示");
        _=handoff.StopAfterLiveFailureAsync("实时画面无法显示，正在收回键鼠");
    }
    private async ValueTask DisposeLivePresentationAsync()
    {
        Volatile.Write(ref liveViewAvailable,0);
        if(handoff is not null)handoff.LivePacketReceived-=OnLivePacket;
        if(handoff?.UsesLivePreview==true)ClearLivePresentation("预览已关闭");
        if(livePresenter is{} presenter){livePresenter=null;await presenter.DisposeAsync();}
    }
    private void ApplyDisplayedFrame(BitmapSource? frame,string state,PreviewFrame? sourceFrame)
    {
        if(!ReferenceEquals(image.Source,frame))image.Source=frame;
        placeholder.Text=state;placeholder.Visibility=frame is null?Visibility.Visible:Visibility.Collapsed;
        caption.Text=frame is null?"":sourceFrame is null?"画面已更新":"更新于 "+sourceFrame.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        displayedWidth=sourceFrame?.Width??frame?.PixelWidth??0;displayedHeight=sourceFrame?.Height??frame?.PixelHeight??0;
    }
}
