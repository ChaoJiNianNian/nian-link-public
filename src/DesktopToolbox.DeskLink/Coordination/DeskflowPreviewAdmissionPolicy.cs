namespace DesktopToolbox.DeskLink.Coordination;

/// <summary>A read-only, expiring focus observation. It never switches or stops a keyboard core.</summary>
public sealed class DeskflowPreviewAdmissionPolicy(string localDeviceName,bool isWindowsPrimary,TimeProvider? timeProvider=null)
{
    private readonly object gate=new();
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    private DeskLinkState? observed;
    private long observedAt;
    private bool otherCore,isolated;
    public bool Update(DeskLinkState state,bool otherCoreRunning,bool previewMarkerCompatible)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock(gate)
        {
            if(observed is{ProcessState:DeskLinkProcessState.Running} previous&&state.ProcessState==DeskLinkProcessState.Running&&
                previous.ProcessId==state.ProcessId&&state.Revision<previous.Revision)
            {observed=null;return true;}
            var changed=observed is null||observed.ProcessId!=state.ProcessId||observed.ProcessState!=state.ProcessState||
                observed.ActiveComputer!=state.ActiveComputer||observed.NativeControlAvailable!=state.NativeControlAvailable||
                observed.Revision!=state.Revision||otherCore!=otherCoreRunning||isolated!=previewMarkerCompatible;
            observed=state;observedAt=clock.GetTimestamp();otherCore=otherCoreRunning;isolated=previewMarkerCompatible;
            return changed;
        }
    }
    public (bool Allowed,string? Reason) Check(bool sending,string peerDeviceName)
    {
        lock(gate)
        {
            if(string.IsNullOrWhiteSpace(localDeviceName)||string.IsNullOrWhiteSpace(peerDeviceName)||
                string.Equals(localDeviceName,peerDeviceName,StringComparison.OrdinalIgnoreCase))return (false,"设备名称未确认，暂时仅观看");
            if(observed is not{} state)return (false,"正在核对日常键鼠状态，请稍候");
            var elapsed=clock.GetElapsedTime(observedAt,clock.GetTimestamp());
            if(elapsed<TimeSpan.Zero||elapsed>TimeSpan.FromSeconds(1))return (false,"日常键鼠状态暂未更新，请稍后重试");
            if(otherCore)return (false,"检测到其他键鼠控制程序，请先结束冲突的共享");
            if(state.ProcessState==DeskLinkProcessState.Stopped&&state.ProcessId is null)return (true,null);
            if(state.ProcessState!=DeskLinkProcessState.Running||!state.NativeControlAvailable||state.ProcessId is not >0||state.Revision<0)
                return (false,"日常键鼠正在连接或结束，暂时仅观看");
            var viewer=sending?localDeviceName:peerDeviceName;
            if(!string.Equals(state.ActiveComputer,viewer,StringComparison.Ordinal))return (false,"请先把日常键鼠留在查看预览的电脑");
            if(isWindowsPrimary&&!sending&&!isolated)return (false,"Windows 键鼠核心需要更新，才能接收预览操作");
            return (true,null);
        }
    }
    public void Invalidate(){lock(gate)observed=null;}
}
