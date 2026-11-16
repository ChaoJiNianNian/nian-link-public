using DesktopToolbox.DeskLink.Collaboration;
using Nian.Input.Control;

namespace DesktopToolbox.App.DeskLink;

public sealed partial class WindowsCollaborationHost
{
    private readonly object previewSessionsGate=new();
    private readonly List<WeakReference<PreviewControlSession>> previewSessions=[];
    private readonly HashSet<object> fastPreviewLeases=[];
    private readonly TimeProvider? controlClock;
    private readonly INativePreviewControl? explicitPreviewControl;
    private INativePreviewControl? NativePreviewControl=>explicitPreviewControl??keyboard as INativePreviewControl;
    public bool PreviewControlReady=>(Snapshot.Connected||HasProtectedLiveOperation)&&Snapshot.PreviewEnabled&&
        (DeskflowHandoff?.PreviewHandoffSupported ?? NativePreviewControl?.PreviewControlReady)==true&&
        (DeskflowHandoff is null||HasProtectedLiveOperation||server?.LivePreview.RemotePeerSupportsLivePreview==true&&passivePeerSupported&&server?.RemotePreviewSourceStatus?.Availability=="enabled");
    public string PreviewControlHint=>!Snapshot.Connected&&!HasProtectedLiveOperation?"请先连接 Mac，再开启预览操作。":
        !Snapshot.PreviewEnabled?"请先开启桌面预览。":DeskflowHandoff is{} handoff?
        (!HasProtectedLiveOperation&&(server?.LivePreview.RemotePeerSupportsLivePreview!=true||!passivePeerSupported)?"请将 Mac 更新到支持实时预览的版本。":
        !HasProtectedLiveOperation&&server?.RemotePreviewSourceStatus?.Availability!="enabled"?"请在 Mac 开启画面预览并解锁。":handoff.PreviewHandoffHint):
        NativePreviewControl is not{} native?"当前版本仅支持观看，更新后可连接预览操作。":
        native.PreviewControlHint;
    private int EffectivePreviewRefreshSeconds {get{lock(previewSessionsGate)return fastPreviewLeases.Count>0?1:settings.PreviewRefreshSeconds;}}

    public PreviewControlSession? CreatePreviewControlSession()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(DeskflowHandoff is not null || NativePreviewControl is not{} native)return null;
        try
        {
            var lease=new object();
            var session=new PreviewControlSession(native.CreatePreviewChannel(),(enabled,token)=>SetTemporaryPreviewCaptureAsync(lease,enabled,token),controlClock);
            lock(previewSessionsGate){previewSessions.RemoveAll(item=>!item.TryGetTarget(out _));previewSessions.Add(new(session));}
            return session;
        }
        catch(Exception error){LogThrottled(error);return null;}
    }
    private async Task SetTemporaryPreviewCaptureAsync(object lease,bool enabled,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock(previewSessionsGate)
        {
            if(enabled)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
                if(!settings.PreviewEnabled||!previewVisible)throw new InvalidOperationException("请先开启并显示预览。");
                fastPreviewLeases.Add(lease);
            }
            else fastPreviewLeases.Remove(lease);
        }
        UpdatePreviewDemand(server);
        if(enabled)await RefreshAsync(token);
    }
    private PreviewFrame BindCapturedPreview(PreviewFrame frame)
    {
        if(frame.State!="live"){InvalidateCapturedPreview();return frame with{Geometry=null,ControlProof=null};}
        try{return frame with{ControlProof=NativePreviewControl?.BindCapturedFrame(frame.JpegBytes,frame.Geometry)};}
        catch(Exception error){LogThrottled(error);InvalidateCapturedPreview();return frame with{ControlProof=null};}
    }
    private void InvalidateCapturedPreview(){try{NativePreviewControl?.InvalidateCapturedFrames();}catch(Exception error){LogThrottled(error);}}
    private PreviewControlSession[] PreviewSessions()
    {
        lock(previewSessionsGate)
        {
            previewSessions.RemoveAll(item=>!item.TryGetTarget(out _));
            return previewSessions.Select(item=>item.TryGetTarget(out var session)?session:null).OfType<PreviewControlSession>().ToArray();
        }
    }
    private void StopPreviewSessions(string reason,bool activeOnly=false,bool preserveLive=false)
    {StopHandoffSessions(reason,activeOnly,preserveLive);foreach(var session in PreviewSessions())if(!activeOnly||session.IsActive)_=session.StopAsync(reason);}
    private async Task DisposePreviewSessionsAsync()
    {
        await DisposeHandoffSessionsAsync();
        foreach(var session in PreviewSessions())await session.DisposeAsync();
        lock(previewSessionsGate){previewSessions.Clear();fastPreviewLeases.Clear();}
    }
}
