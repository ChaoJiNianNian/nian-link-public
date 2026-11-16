using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Preview;
using DesktopToolbox.Mac.Models;
using Nian.Input.Control;

namespace DesktopToolbox.Mac.Services;

public sealed partial class MacCollaborationService
{
    private readonly INativePreviewControl? nativeControl;
    private readonly IDeskflowPreviewControl? deskflowPreviewControl;
    private readonly TimeProvider? controlClock;
    private readonly object previewSessionsGate=new();
    private readonly List<WeakReference<PreviewControlSession>> previewSessions=[];
    private readonly List<WeakReference<DeskflowPreviewSession>> deskflowPreviewSessions=[];
    private readonly HashSet<object> fastPreviewLeases=[];
    private long liveViewerRevision;
    private int EffectivePreviewRefreshSeconds{get{lock(previewSessionsGate)return fastPreviewLeases.Count>0?1:Snapshot.Settings.PreviewRefreshSeconds;}}
    public bool PreviewControlReady=>Snapshot.IsOnline&&Snapshot.Settings.PreviewEnabled&&
        (deskflowPreviewControl is not null?deskflowPreviewControl.PreviewHandoffSupported&&Volatile.Read(ref remoteSupportsLivePreview)&&Snapshot.PassivePreviewGenerationSupported&&Snapshot.RemotePreviewSourceStatus?.Availability=="enabled":nativeControl?.PreviewControlReady??false);
    public string PreviewControlHint=>!Snapshot.Settings.PreviewEnabled?"请先开启画面预览":!Snapshot.IsOnline?"对方设备已断开，请重新连接":
        deskflowPreviewControl is not null&&(!Volatile.Read(ref remoteSupportsLivePreview)||!Snapshot.PassivePreviewGenerationSupported)?"请更新 Windows 互联后使用实时操作":
        deskflowPreviewControl is not null&&Snapshot.RemotePreviewSourceStatus?.Availability!="enabled"?"请先在 Windows 开启并允许画面预览":deskflowPreviewControl?.PreviewHandoffHint??nativeControl?.PreviewControlHint??"连接设备后，可在放大预览中操作";
    public void NotifyPreviewControlChanged(){if(Volatile.Read(ref disposed)==0)Publish();}

    public DeskflowPreviewSession? CreateDeskflowPreviewSession()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(deskflowPreviewControl is null)return null;
        var source=new RemoteSubscriptionControlLivePreviewSource(()=>Volatile.Read(ref client),()=>(Volatile.Read(ref disposed)==0)&&
            Snapshot.IsOnline&&Snapshot.Settings.PreviewEnabled&&Volatile.Read(ref remoteSupportsLivePreview)&&Snapshot.PassivePreviewGenerationSupported&&Snapshot.RemotePreviewSourceStatus?.Availability=="enabled"&&
            MacPreviewPresentation.ShouldSubscribe(true,miniVisible,detachedVisible,true));
        return RegisterDeskflowSession(new DeskflowPreviewSession(deskflowPreviewControl,source,controlClock));
    }

    // Explicit inert legacy path for historical rendering and cadence regression fixtures only.
    internal DeskflowPreviewSession? CreateLegacyDeskflowPreviewSessionForFixture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(deskflowPreviewControl is null)return null;
        var lease=new object();var session=new DeskflowPreviewSession(deskflowPreviewControl,(enabled,token)=>SetTemporaryPreviewCaptureAsync(lease,enabled,token),controlClock);
        return RegisterDeskflowSession(session);
    }
    private DeskflowPreviewSession RegisterDeskflowSession(DeskflowPreviewSession session)
    {
        session.Changed+=OnDeskflowPreviewSessionChanged;
        lock(previewSessionsGate){deskflowPreviewSessions.RemoveAll(item=>!item.TryGetTarget(out _));deskflowPreviewSessions.Add(new(session));}
        return session;
    }
    internal DeskflowPreviewSession RegisterDeskflowSessionForFixture(DeskflowPreviewSession session)=>RegisterDeskflowSession(session);
    private void OnDeskflowPreviewSessionChanged(){Interlocked.Increment(ref liveViewerRevision);ObserveViewerPhaseForPassiveGeneration();NotifyPreviewControlChanged();}
    internal (int Generation,long ViewerRevision) CapturePreviewPollVersion()=>
        (Volatile.Read(ref previewGeneration),Interlocked.Read(ref liveViewerRevision));
    internal bool TryApplyPolledPreview(PreviewFrame frame,int generation,long viewerRevision)
    {
        bool Current()=>generation==Volatile.Read(ref previewGeneration)&&viewerRevision==Interlocked.Read(ref liveViewerRevision)&&
            Snapshot.Settings.PreviewEnabled&&!HasProtectedLiveSession()&&
            (frame.State!="live"||!Snapshot.PassivePreviewGenerationSupported||
                frame.RequestId==Snapshot.PassivePreviewRequestId&&frame.RequestId is not null&&frame.SourceStatus==Snapshot.RemotePreviewSourceStatus&&frame.SourceStatus?.Availability=="enabled");
        if(!Current())return false;
        if(frame.State!="live")StopPreviewSessions("没有可操作画面，鼠标控制已停止",activeOnly:frame.State is "loading" or "waiting",preserveProtectedLive:true);
        var applied=false;
        UpdateState(current=>
        {
            if(!Current()){applied=false;return current;}
            applied=true;return current with{Preview=frame};
        });
        return applied;
    }

    public PreviewControlSession? CreatePreviewControlSession()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(nativeControl is null)return null;
        try
        {
            var lease=new object();var session=new PreviewControlSession(nativeControl.CreatePreviewChannel(),(enabled,token)=>SetTemporaryPreviewCaptureAsync(lease,enabled,token),controlClock);
            lock(previewSessionsGate){previewSessions.RemoveAll(item=>!item.TryGetTarget(out _));previewSessions.Add(new(session));}
            return session;
        }
        catch{return null;}
    }
    private async Task SetTemporaryPreviewCaptureAsync(object lease,bool enabled,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock(previewSessionsGate)
        {
            if(enabled)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
                if(!MacPreviewPresentation.ShouldSubscribe(Snapshot.Settings.PreviewEnabled,miniVisible,detachedVisible,true))throw new InvalidOperationException("请先开启并显示预览。");
                fastPreviewLeases.Add(lease);
            }
            else fastPreviewLeases.Remove(lease);
        }
        if(client is{} connection)
        {
            var subscribe=MacPreviewPresentation.ShouldSubscribe(Snapshot.Settings.PreviewEnabled,miniVisible,detachedVisible,true);
            await SetRemotePreviewRequestAsync(connection,subscribe,Volatile.Read(ref previewGeneration),token,detachedVisible?1920:960,detachedVisible?1080:540,EffectivePreviewRefreshSeconds);
        }
    }
    private PreviewFrame BindCapturedPreview(PreviewFrame frame)
    {
        if(frame.State!="live"){InvalidateCapturedPreview();return frame with{Geometry=null,ControlProof=null};}
        try{return frame with{ControlProof=nativeControl?.BindCapturedFrame(frame.JpegBytes,frame.Geometry)};}
        catch{InvalidateCapturedPreview();return frame with{ControlProof=null};}
    }
    private void InvalidateCapturedPreview(){try{nativeControl?.InvalidateCapturedFrames();}catch{}}
    private PreviewControlSession[] PreviewSessions()
    {
        lock(previewSessionsGate)
        {
            previewSessions.RemoveAll(item=>!item.TryGetTarget(out _));
            return previewSessions.Select(item=>item.TryGetTarget(out var session)?session:null).OfType<PreviewControlSession>().ToArray();
        }
    }
    private void StopPreviewSessions(string reason,bool activeOnly=false,bool preserveProtectedLive=false)
    {
        foreach(var session in PreviewSessions())if(!activeOnly||session.IsActive)_=session.StopAsync(reason);
        foreach(var session in DeskflowPreviewSessions())if((!activeOnly||session.IsActive)&&(!preserveProtectedLive||!session.HasProtectedLiveOperation))_=session.StopAsync(reason);
    }
    private DeskflowPreviewSession[] DeskflowPreviewSessions()
    {
        lock(previewSessionsGate)
        {
            deskflowPreviewSessions.RemoveAll(item=>!item.TryGetTarget(out _));
            return deskflowPreviewSessions.Select(item=>item.TryGetTarget(out var session)?session:null).OfType<DeskflowPreviewSession>().ToArray();
        }
    }
    private async Task DisposePreviewSessionsAsync()
    {
        foreach(var session in PreviewSessions())await session.DisposeAsync();
        foreach(var session in DeskflowPreviewSessions()){session.Changed-=OnDeskflowPreviewSessionChanged;await session.DisposeAsync();}
        lock(previewSessionsGate){previewSessions.Clear();deskflowPreviewSessions.Clear();fastPreviewLeases.Clear();}
    }
}
