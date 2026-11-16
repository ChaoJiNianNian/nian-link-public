using System.Runtime.InteropServices;
using System.Text;
using DesktopToolbox.DeskLink.Collaboration;
using Microsoft.Win32;

namespace DesktopToolbox.App.DeskLink;

public sealed partial class WindowsCollaborationHost
{
    private readonly PreviewSourceStatus initialSource = new(Guid.NewGuid(),1,"unknown");
    private PreviewSourceStatus? localSource;
    private readonly Func<bool> desktopUnlocked;
    private readonly bool observeSessionEvents;
    private bool sessionEventsRegistered;
    private int sessionLocked;
    private Guid passiveRequestId=Guid.NewGuid();
    private Guid? submittedPassiveRequestId;
    private CollaborationServer? passiveDemandEndpoint;
    private Guid? lastLocalCaptureRequestId;
    private long localCaptureSequence;
    private volatile bool passivePeerSupported;
    private bool lastPeerConnected;

    [DllImport("user32.dll",SetLastError=true)] private static extern nint OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool GetUserObjectInformationW(nint handle,int index,StringBuilder information,int length,out int needed);
    private static bool IsDesktopUnlocked()
    {
        var desktop=OpenInputDesktop(0,false,1);
        if(desktop==0)return false;
        try{var name=new StringBuilder(256);return GetUserObjectInformationW(desktop,2,name,name.Capacity*2,out _)&&string.Equals(name.ToString(),"Default",StringComparison.OrdinalIgnoreCase);}
        finally{CloseDesktop(desktop);}
    }

    private void StartDesktopObservation()
    {
        if(observeSessionEvents)
        {
            try{SystemEvents.SessionSwitch+=OnSessionSwitch;sessionEventsRegistered=true;}
            catch(InvalidOperationException){ }
        }
        RefreshLocalSourceStatus();
    }
    private void OnSessionSwitch(object sender,SessionSwitchEventArgs args)
    {
        if(args.Reason==SessionSwitchReason.SessionLock)Volatile.Write(ref sessionLocked,1);
        else if(args.Reason==SessionSwitchReason.SessionUnlock)Volatile.Write(ref sessionLocked,0);
        else return;
        if(Volatile.Read(ref disposed)==0)RefreshLocalSourceStatus();
    }
    private PreviewSourceStatus RefreshLocalSourceStatus()
    {
        CancellationTokenSource? cancel=null;bool locked=false;PreviewSourceStatus result;
        lock(previewDemandGate)
        {
            var unlocked=false;
            try{unlocked=Volatile.Read(ref sessionLocked)==0&&desktopUnlocked();}catch{ }
            var availability=Volatile.Read(ref liveCaptureAllowed)==0?"disabled":unlocked?"enabled":"locked";
            var current=localSource??initialSource;
            if(current.Availability!=availability)
            {
                result=localSource=new(current.InstanceId,current.Revision+1,availability);
                captureCadence.Reset();
                server?.SetLocalPreviewSource(result);
                server?.LivePreview.SetLocalCaptureProvider(availability=="enabled"?liveCapture:null);
                if(availability=="locked")
                {
                    locked=true;cancel=previewCaptureCycle;previewCaptureCycle=new CancellationTokenSource();
                }
            }
            else result=localSource=current;
        }
        if(cancel is not null){cancel.Cancel();cancel.Dispose();}
        if(locked){StopPreviewSessions("Windows 已锁屏，鼠标控制已停止");InvalidateCapturedPreview();}
        if(result.Availability is "locked" or "enabled")UpdatePreviewDemand(server);
        return result;
    }
    private void ObservePassivePeer(CollaborationServer endpoint)
    {
        var connected=endpoint.IsPeerConnected;
        var supported=endpoint.RemotePassivePreviewGenerationSupported;
        var protectedLive=HasProtectedLiveOperation;
        lock(previewDemandGate)
        {
            if(connected&&!lastPeerConnected){lastPeerConnected=true;passiveRequestId=Guid.NewGuid();}
            else if(!connected&&!protectedLive&&lastPeerConnected){lastPeerConnected=false;passivePeerSupported=false;passiveRequestId=Guid.NewGuid();}
            if(connected&&supported&&!passivePeerSupported){passivePeerSupported=true;passiveRequestId=Guid.NewGuid();}
        }
    }
    private void RotatePassivePreviewRequest()
    {
        lock(previewDemandGate)passiveRequestId=Guid.NewGuid();
        UpdatePreviewDemand(server);
        if(Volatile.Read(ref disposed)==0)Publish(PreviewFrame.Off("waiting"),Snapshot.ConnectionMessage);
    }
    private Guid? CurrentPassivePreviewRequest()
    {lock(previewDemandGate)return passivePeerSupported?passiveRequestId:null;}
    private void StopDesktopObservation()
    {
        if(sessionEventsRegistered){SystemEvents.SessionSwitch-=OnSessionSwitch;sessionEventsRegistered=false;}
    }
}
