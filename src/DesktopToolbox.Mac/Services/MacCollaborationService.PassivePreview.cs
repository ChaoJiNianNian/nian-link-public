using DesktopToolbox.DeskLink.Collaboration;

namespace DesktopToolbox.Mac.Services;

public sealed partial class MacCollaborationService
{
    private readonly object sourceStateGate=new();
    private PreviewSourceStatus localSourceStatus=new(Guid.NewGuid(),1,"unknown");
    private bool hadLiveViewer;
    private readonly SemaphoreSlim pollGate=new(1,1);
    private Guid? sequencedRequest,sequencedSource;
    private long captureSequence;

    private long? NextCaptureSequence(Guid? requestId,PreviewSourceStatus source)
    {
        if(requestId is null)return null;
        lock(sourceStateGate)
        {
            if(sequencedRequest!=requestId||sequencedSource!=source.InstanceId)
            {sequencedRequest=requestId;sequencedSource=source.InstanceId;captureSequence=0;}
            return checked(++captureSequence);
        }
    }

    private PreviewSourceStatus LocalSourceStatus {get{lock(sourceStateGate)return localSourceStatus;}}
    private PreviewSourceStatus SetLocalSourceAvailability(string availability)
    {
        lock(sourceStateGate)
        {
            if(!Snapshot.Settings.ShareScreenEnabled)availability="disabled";
            if(localSourceStatus.Availability!=availability)localSourceStatus=localSourceStatus with{Revision=checked(localSourceStatus.Revision+1),Availability=availability};
            return localSourceStatus;
        }
    }
    private async Task RefreshLocalSourceStatusAsync(CollaborationClient connection,bool capable,CancellationToken token)
    {
        if(!capable)return;
        var availability="disabled";
        if(Snapshot.Settings.ShareScreenEnabled)
        {
            try
            {
                using var native=await media.RequestAsync(new{action="status"},token);
                availability=native.RootElement.TryGetProperty("status",out var state)?state.GetString() switch
                {"ready"=>"enabled","locked"=>"locked","permissionRequired" or "sourceUnavailable"=>"unavailable",_=>"unknown"}:"unknown";
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch{availability="unknown";}
        }
        var status=SetLocalSourceAvailability(availability);
        if(status.Availability!="enabled")await StopLiveUploadAsync();
        await PublishLocalSourceStatusAsync(connection,token);
    }
    private async Task PublishLocalSourceStatusAsync(CollaborationClient connection,CancellationToken token)
    {
        await previewRemoteGate.WaitAsync(token);
        try
        {
            if(ReferenceEquals(connection,client))await connection.SetPreviewSourceAsync(LocalSourceStatus,token);
        }
        finally{previewRemoteGate.Release();}
    }
    private void InvalidateLocalSourceForChoice(bool enabled)=>SetLocalSourceAvailability(enabled?"unknown":"disabled");
    private void RotatePassiveRequest()
    {
        if(!Snapshot.PassivePreviewGenerationSupported)return;
        UpdateState(state=>state with{PassivePreviewRequestId=Guid.NewGuid()});
        captureCadence.Reset();
    }
    private void ObserveViewerPhaseForPassiveGeneration()
    {
        var active=DeskflowPreviewSessions().Any(session=>session.UsesLivePreview&&
            session.Phase is Nian.Input.Control.PreviewControlPhase.Preparing or Nian.Input.Control.PreviewControlPhase.Controlling or Nian.Input.Control.PreviewControlPhase.Stopping);
        lock(sourceStateGate)
        {
            if(hadLiveViewer&&!active)RotatePassiveRequest();
            hadLiveViewer=active;
        }
    }
    private void ApplyRemoteSourceStatus(CollaborationStatus status)
    {
        var current=Snapshot;var capability=status.PassivePreviewGenerationSupported;
        var remote=capability?status.LocalPreviewSourceStatus:null;
        var sourceChanged=current.RemotePreviewSourceStatus is not null&&remote is not null&&
            (current.RemotePreviewSourceStatus.InstanceId!=remote.InstanceId||current.RemotePreviewSourceStatus.Revision!=remote.Revision);
        var changed=current.PassivePreviewGenerationSupported!=capability||
            current.RemotePreviewSourceStatus?.InstanceId!=remote?.InstanceId||sourceChanged;
        UpdateState(state=>state with{PassivePreviewGenerationSupported=capability,RemotePreviewSourceStatus=remote,
            PassivePreviewRequestId=capability?(changed||state.PassivePreviewRequestId is null?Guid.NewGuid():state.PassivePreviewRequestId):null,
            Preview=capability&&remote?.Availability!="enabled"?PreviewFrame.Off(remote?.Availability=="locked"?"locked":"waiting"):state.Preview});
        if(sourceChanged||current.PassivePreviewGenerationSupported&&!capability||capability&&remote?.Availability!="enabled")
        {
            foreach(var session in DeskflowPreviewSessions())if(session.UsesLivePreview&&session.CanStop)
                _=session.StopAfterLiveFailureAsync("对方画面已关闭或不可用，正在收回键鼠");
        }
    }
}
