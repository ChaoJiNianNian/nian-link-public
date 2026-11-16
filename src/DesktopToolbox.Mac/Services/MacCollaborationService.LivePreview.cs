using System.Diagnostics;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;

namespace DesktopToolbox.Mac.Services;

public sealed partial class MacCollaborationService
{
    private readonly SemaphoreSlim liveUploadGate=new(1,1);
    private readonly HashSet<Guid> attemptedLiveUploads=[];
    private CancellationTokenSource? liveUploadCancellation;
    private Task? liveUploadTask;
    private Guid? liveUploadId;
    private bool remoteSupportsLivePreview;
    private readonly SemaphoreSlim previewRemoteGate=new(1,1);
    private Guid? lastRenewedPassiveRequest;
    private long lastPassiveRenewalAt;

    private async Task SetRemotePreviewRequestAsync(CollaborationClient connection,bool requested,int generation,CancellationToken token,int width=960,int height=540,int seconds=PreviewCaptureCadence.DefaultRefreshSeconds)
    {
        await previewRemoteGate.WaitAsync(token);
        try
        {
            if(generation!=Volatile.Read(ref previewGeneration)||!ReferenceEquals(connection,client)||requested&&!Snapshot.Settings.PreviewEnabled)return;
            var requestId=Snapshot.PassivePreviewGenerationSupported?Snapshot.PassivePreviewRequestId:null;
            if(requested&&requestId is{} prior&&lastRenewedPassiveRequest==prior&&lastPassiveRenewalAt!=0&&
                Stopwatch.GetElapsedTime(lastPassiveRenewalAt)>=TimeSpan.FromSeconds(4))
            {RotatePassiveRequest();requestId=Snapshot.PassivePreviewRequestId;}
            await connection.SetPreviewRequestedAsync(requested,token,width,height,seconds,requestId);
            if(requested&&requestId is not null){lastRenewedPassiveRequest=requestId;lastPassiveRenewalAt=Stopwatch.GetTimestamp();}
        }
        finally{previewRemoteGate.Release();}
    }
    private async Task PublishRemotePreviewAsync(CollaborationClient connection,PreviewFrame frame,int generation,CancellationToken token)
    {
        await previewRemoteGate.WaitAsync(token);
        try
        {
            if(generation!=Volatile.Read(ref shareGeneration)||!ReferenceEquals(connection,client)||
                frame.State=="live"&&!Snapshot.Settings.ShareScreenEnabled)return;
            await connection.PublishPreviewAsync(frame,token);
        }
        finally{previewRemoteGate.Release();}
    }

    private async Task UpdateLiveUploadAsync(CollaborationClient connection,LivePreviewDemand? demand,CancellationToken token)
    {
        await liveUploadGate.WaitAsync(token);
        try
        {
            var enabled=Snapshot.Settings.ShareScreenEnabled&&liveCapture.IsSupported&&ReferenceEquals(connection,client)&&Volatile.Read(ref disposed)==0&&
                (!Snapshot.PassivePreviewGenerationSupported||LocalSourceStatus.Availability=="enabled");
            var generation=Volatile.Read(ref shareGeneration);
            if(enabled&&demand is not null&&liveUploadId==demand.StreamId)return;
            var wasActive=liveUploadId is not null;
            await StopLiveUploadLockedAsync();
#if NIAN_PREVIEW_TEST_HOOKS
            if(wasActive&&TestAfterLiveUploadStopped is{} afterStop)await afterStop();
#endif
            if(wasActive)captureCadence.Reset();
            await previewPreferenceGate.WaitAsync(token);
            try
            {
            if(!enabled||generation!=Volatile.Read(ref shareGeneration)||!Snapshot.Settings.ShareScreenEnabled||shareCycle.IsCancellationRequested||!ReferenceEquals(connection,client)||Volatile.Read(ref disposed)!=0||demand is null||demand.StreamId==Guid.Empty||!attemptedLiveUploads.Add(demand.StreamId))return;
#if NIAN_PREVIEW_TEST_HOOKS
            TestLiveUploadAuthorized?.Invoke();
#endif
            liveUploadId=demand.StreamId;
            var owned=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,shareCycle.Token);
            liveUploadCancellation=owned;
            // The owned stream runs independently of the status/clipboard request deadline.
            liveUploadTask=Task.Run(async()=>
            {
                try{await connection.LivePreview.UploadAsync(demand,liveCapture,owned.Token);}
                catch(OperationCanceledException) when(owned.IsCancellationRequested){}
                catch(Exception error){Trace.TraceWarning("Mac live preview upload stopped ({0}).",error.GetType().Name);}
            });
            }
            finally{previewPreferenceGate.Release();}
        }
        finally{liveUploadGate.Release();}
    }
    private async Task StopLiveUploadAsync()
    {
        await liveUploadGate.WaitAsync();
        try{await StopLiveUploadLockedAsync();}
        finally{liveUploadGate.Release();}
    }
    private async Task StopLiveUploadIfSharingStillDisabledAsync(int generation)
    {
        await liveUploadGate.WaitAsync();
        try
        {
            if(generation==Volatile.Read(ref shareGeneration)&&!Snapshot.Settings.ShareScreenEnabled)
                await StopLiveUploadLockedAsync();
        }
        finally{liveUploadGate.Release();}
    }
    private async Task StopLiveUploadLockedAsync()
    {
        var cancellation=liveUploadCancellation;var task=liveUploadTask;
        liveUploadCancellation=null;liveUploadTask=null;liveUploadId=null;
        if(cancellation is null)return;
        cancellation.Cancel();
        try{if(task is not null)await task;}
        finally{cancellation.Dispose();}
    }
    private bool HasProtectedLiveSession()=>DeskflowPreviewSessions().Any(session=>session.HasProtectedLiveOperation);
}
