using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;
using DesktopToolbox.Mac.Models;
using Nian.Input.Runtime;

namespace DesktopToolbox.Mac.Services;

/// <summary>Client events cannot renew an old network focus observation.</summary>
internal sealed class MacPreviewAdmissionObserver
{
    private readonly object gate=new();
    private readonly TimeProvider clock;
    private DeskflowPreviewAdmissionPolicy? policy;
    private DeskLinkState? remote;
    private MacLinkSnapshot snapshot=new();
    private string localName="";
    private bool? previousOtherCore;
    private bool awaitingObservation;
    internal MacPreviewAdmissionObserver(TimeProvider? clock=null)=>this.clock=clock??TimeProvider.System;
    internal MacLinkSnapshot Snapshot {get{lock(gate)return snapshot;}}
    internal bool Observe(MacLinkSnapshot value,bool otherCoreRunning)=>ObserveCore(value,otherCoreRunning,true);
    private bool ObserveCore(MacLinkSnapshot value,bool otherCoreRunning,bool legacyObservation)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock(gate)
        {
            if(snapshot.LocalSnapshotVersion>0 &&
                (value.LocalSnapshotVersion==0 || value.LocalSnapshotVersion<snapshot.LocalSnapshotVersion)) return false;
            snapshot=value;
            if(legacyObservation)awaitingObservation=false;
            var changed=false;
            if(policy is null||localName!=value.Settings.MacName)
            {
                localName=value.Settings.MacName;policy=new(localName,false,clock);remote=null;previousOtherCore=null;changed=true;
            }
            if(previousOtherCore!=otherCoreRunning){policy.Invalidate();changed=true;}
            previousOtherCore=otherCoreRunning;
            if(value.PreviewControlState is{} authenticated)
            {
                if(!ReferenceEquals(remote,authenticated))
                {remote=authenticated;changed|=policy.Update(authenticated,otherCoreRunning,true);}
            }
            else
            {
                if(remote is not null){remote=null;policy.Invalidate();changed=true;}
                var local=value.PreviewLocalCoreState;
                if(!awaitingObservation&&local.ProcessState==DeskLinkProcessState.Stopped&&local.ProcessId is null)
                    changed|=policy.Update(local,otherCoreRunning,true);
                else {policy.Invalidate();changed=true;}
            }
            return changed;
        }
    }
    internal bool RefreshLocalProbe(bool otherCoreRunning){lock(gate)return ObserveCore(snapshot,otherCoreRunning,false);}
    internal bool RefreshLocalProbe(MacLinkSnapshot expected,bool otherCoreRunning)
    {
        lock(gate)return ReferenceEquals(snapshot,expected)&&ObserveCore(snapshot,otherCoreRunning,false);
    }
    internal PreviewAdmission Check(bool sending,string peerName)
    {
        lock(gate)
        {
            if(peerName!=snapshot.Settings.WindowsName)return new(false,"请连接当前预览的 Windows 设备");
            if(policy is null)return new(false,"正在核对日常键鼠状态，请稍候");
            var result=policy.Check(sending,peerName);return new(result.Allowed,result.Reason);
        }
    }
    internal void Invalidate(){lock(gate){policy?.Invalidate();awaitingObservation=true;}}
}
