using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.DeskLink.Preview;
using Nian.Input.Control;

namespace DesktopToolbox.Mac.Views;

public sealed partial class MacRemotePreviewWindow
{
    private LivePreviewPresenter<Bitmap>? livePresenter;
    private Guid? liveSurfaceEpoch;
    private bool liveFailureObserved;
    private PreviewSourceStatus? knownPassiveSource;
    private bool passiveSourceKnown,lastPassiveSupport;

    public void SetPassiveContext(bool supported,Guid? requestId,PreviewSourceStatus? source)
    {
        if(closed)return;
        preview.SetPassiveContext(supported,requestId,source);
        var changed=!passiveSourceKnown||lastPassiveSupport!=supported||!Equals(knownPassiveSource,source);
        var sourceChanged=passiveSourceKnown&&lastPassiveSupport&&supported&&knownPassiveSource is not null&&!Equals(knownPassiveSource,source);
        passiveSourceKnown=true;lastPassiveSupport=supported;knownPassiveSource=source;
        if(supported&&changed&&(source?.Availability!="enabled"||sourceChanged))
        {
            if(handoff?.UsesLivePreview==true&&handoff.CanStop)_=handoff.StopAfterLiveFailureAsync("对方画面已关闭或不可用，正在收回键鼠");
            liveSurfaceEpoch=null;preview.ClearLivePresentation(source?.Availability=="locked"?"Windows 已锁屏，画面暂停":"对方画面已关闭或不可用");
        }
    }

    private void InitializeLivePresentation()
    {
        if(handoff?.UsesLivePreview!=true)return;
        livePresenter=new(DecodeLiveBitmap,action=>Dispatcher.UIThread.Post(action,DispatcherPriority.Render),
            PresentLiveBitmap,IsCurrentLiveEpoch,image=>image.Dispose(),OnLivePresentationFailed);
        handoff.LivePacketReceived+=OnLivePacket;
        SyncLivePresentation();
    }

    // LivePreviewPresenter invokes this on its single background decoder worker.
    private static Task<Bitmap> DecodeLiveBitmap(LivePreviewCapturePacket packet,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream=new MemoryStream(packet.JpegBytes);var decoded=new Bitmap(stream);
        if(token.IsCancellationRequested||decoded.PixelSize.Width!=packet.Width||decoded.PixelSize.Height!=packet.Height)
        {
            decoded.Dispose();token.ThrowIfCancellationRequested();
            throw new InvalidDataException("实时画面编码尺寸不一致。");
        }
        return Task.FromResult(decoded);
    }

    private bool IsCurrentLiveEpoch(Guid epoch)
    {
        // This predicate also runs on the decoder worker. Session state is locked; Avalonia properties are UI-only.
        return !closed&&handoff?.LiveSourceFailed==false&&handoff.LiveOperationId==epoch&&
            handoff.Phase is PreviewControlPhase.Preparing or PreviewControlPhase.Controlling;
    }

    private void OnLivePacket(Guid epoch,LivePreviewCapturePacket packet)=>livePresenter?.Push(epoch,packet);

    private void PresentLiveBitmap(Guid epoch,LivePreviewCapturePacket packet,Bitmap decoded)
    {
        if(!IsCurrentLiveEpoch(epoch)||connectionReady==false){decoded.Dispose();return;}
        SyncLivePresentation();
        if(!preview.SetLiveBitmap(epoch,packet,decoded)){decoded.Dispose();return;}
        // The session can hand off input only after the native Image actually owns the new bitmap.
        handoff!.NotifyLiveFramePresented(epoch,packet);
    }

    private void SyncLivePresentation()
    {
        if(handoff?.UsesLivePreview!=true)return;
        if(handoff.LiveSourceFailed)
        {
            if(!liveFailureObserved)
            {
                liveFailureObserved=true;liveSurfaceEpoch=null;
                preview.ClearLivePresentation("实时画面已停止");
            }
            return;
        }
        liveFailureObserved=false;
        if(connectionReady==false||!IsVisible||WindowState==WindowState.Minimized)return;
        var next=handoff.Phase is PreviewControlPhase.Preparing or PreviewControlPhase.Controlling?handoff.LiveOperationId:null;
        if(liveSurfaceEpoch==next)return;
        liveSurfaceEpoch=next;
        if(next is{} epoch)preview.BeginLivePresentation(epoch);
        else preview.EndLivePresentation(connectionReady!=false,connectionReady==false?"设备连接已断开，画面暂停":"正在等待新的观看画面…");
    }

    private bool SuppressPassiveFrame=>handoff?.UsesLivePreview==true&&
        handoff.Phase is PreviewControlPhase.Preparing or PreviewControlPhase.Controlling or PreviewControlPhase.Stopping;

    private void OnLivePresentationFailed(Exception error)
    {if(!closed&&handoff is not null)_=handoff.StopAfterLiveFailureAsync("实时画面无法显示，正在收回键鼠");}

    private ValueTask DisposeLivePresentationAsync()
    {
        if(handoff is not null)handoff.LivePacketReceived-=OnLivePacket;
        return livePresenter?.DisposeAsync()??ValueTask.CompletedTask;
    }
}
