using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Services;

public sealed partial class MacLinkService
{
    private DeskflowPreviewState previewHandoff=new();
    private string? ownedPreviewId;
    private bool previewBeginPending;
    // A failed status poll must not turn an input-only operation into ordinary display recovery on exit.
    private bool previewDisplayIntent;
    private (int ProcessId,long ThroughRevision)? previewIntentCore;
    public bool PreviewHandoffSupported=>controlReady&&Volatile.Read(ref windowsState) is{PreviewHandoffSupported:true}&&
        IsTrustedConnected(CurrentClientState);
    public string PreviewReturnShortcut=>Volatile.Read(ref previewHandoff).ReturnShortcut is{Length:>0} value?value:control?.PreviewReturnShortcut??"Ctrl+Alt+F12";
    public string PreviewHandoffHint=>pairing is null?"请先连接已配对的 Windows":!controlReady||!IsTrustedConnected(CurrentClientState)?
        "请先启动并连接键鼠共享":windowsState?.PreviewHandoffSupported!=true?"请更新 Windows 工具箱；原配对可继续使用":"只交接键鼠，显示器输入保持不变";
    public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken cancellationToken=default)=>PreviewRequestAsync(PreviewRequest.Begin,null,cancellationToken);
    public Task<DeskflowPreviewState> RenewPreviewAsync(string id,CancellationToken cancellationToken=default)=>PreviewRequestAsync(PreviewRequest.Renew,id,cancellationToken);
    public Task<DeskflowPreviewState> EndPreviewAsync(string id,CancellationToken cancellationToken=default)=>PreviewRequestAsync(PreviewRequest.End,id,cancellationToken);
    public Task<DeskflowPreviewState> ReturnRemotePreviewAsync(string id,CancellationToken cancellationToken=default)=>PreviewRequestAsync(PreviewRequest.EndRemote,id,cancellationToken);
    public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken=default)=>PreviewRequestAsync(PreviewRequest.Read,null,cancellationToken);
    private enum PreviewRequest {Begin,Renew,End,EndRemote,Read}
    private async Task<DeskflowPreviewState> PreviewRequestAsync(PreviewRequest action,string? id,CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();await LoadAsync(token);
            if(pairing is null||control is null)throw new InvalidOperationException("请先连接已配对的 Windows，无需另建输入配对。");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(action==PreviewRequest.Begin?8:3));
            if(action==PreviewRequest.Read)
            {
                windowsState=await ReadWindowsStateAsync(deadline.Token);controlReady=IsNativeReady(windowsState);
                if(!windowsState.PreviewHandoffSupported)throw new NotSupportedException("请更新 Windows 工具箱；原配对无需重新导入。");
                Publish();return previewHandoff;
            }
            if(action==PreviewRequest.Begin)
            {
                windowsState=await ReadWindowsStateAsync(deadline.Token);controlReady=IsNativeReady(windowsState);
                if(!windowsState.PreviewHandoffSupported)throw new NotSupportedException("Windows 尚不支持只交接键鼠，请先更新。");
                if(!controlReady||!IsTrustedConnected(CurrentClientState))throw new InvalidOperationException("请先连接 Mac 键鼠共享并确认指纹。");
                if(windowsState.ActiveComputer!=pairing.MacName)throw new InvalidOperationException("请先把键鼠留在 Mac，再开启 Windows 预览操作。");
                if(displaySwitch.IsWriteInFlight||displays.HasUnfinishedChildren)throw new InvalidOperationException("上一条显示器命令尚未结束，请稍候再开启预览操作。");
                displaySwitch.SuppressThrough(windowsState);previewBeginPending=true;PreservePreviewDisplayIntent(windowsState);
            }
            else
            {
                if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("预览会话标识无效。",nameof(id));
                if(action==PreviewRequest.Renew&&!IsTrustedConnected(CurrentClientState))
                    throw new InvalidOperationException("键鼠连接已变化或指纹尚未确认，不能延长预览操作，请归还键鼠。");
                if(action==PreviewRequest.EndRemote)
                {
                    windowsState=await ReadWindowsStateAsync(deadline.Token);controlReady=IsNativeReady(windowsState);
                    var current=previewHandoff;
                    if(!controlReady||CurrentClientState.ConnectionState!=DeskLinkConnectionState.Connected||!windowsState.PreviewHandoffSupported||
                        current.Phase is not (DeskflowPreviewPhase.Active or DeskflowPreviewPhase.Recovery)||current.Id!=id||current.ViewerComputer!=pairing.WindowsName||
                        current.TargetComputer!=pairing.MacName||(current.Phase==DeskflowPreviewPhase.Active&&windowsState.ActiveComputer!=pairing.MacName))
                        throw new InvalidOperationException("当前预览已结束或连接已变化，未发送归还命令。");
                }
                var known=ownedPreviewId==id||previewHandoff.Id==id;
                if(!known||action==PreviewRequest.Renew&&ownedPreviewId!=id)throw new InvalidOperationException("此预览会话已结束或不属于当前 Mac。");
            }
            var result=action switch
            {
                PreviewRequest.Begin=>await control.BeginPreviewAsync(deadline.Token),
                PreviewRequest.Renew=>await control.RenewPreviewAsync(id!,deadline.Token),
                _=>await control.EndPreviewAsync(id!,deadline.Token)
            };
            ValidatePreviewResult(result,action is not (PreviewRequest.End or PreviewRequest.EndRemote));
            Volatile.Write(ref previewHandoff,result);
            if(action==PreviewRequest.Begin&&result.HasSession){ownedPreviewId=result.Id;previewBeginPending=false;}
            if(result.Phase==DeskflowPreviewPhase.Viewing&&!result.HasSession){ownedPreviewId=null;previewBeginPending=false;}
            await ReadAfterInputOnlyInsideAsync(deadline.Token);Publish();
            return action is PreviewRequest.Begin or PreviewRequest.Renew?previewHandoff:result;
        }
        catch(Exception failure)when(IsControlError(failure))
        {controlReady=false;controlError="预览键鼠交接尚未确认，请检查连接或按 "+PreviewReturnShortcut+" 归还";Publish();throw;}
        finally{gate.Release();}
    }
    private void ValidatePreviewResult(DeskflowPreviewState value,bool macViewer)
    {
        if(pairing is null||!Enum.IsDefined(value.Phase)||value.ReturnShortcut is not{Length:<=100}||value.ReturnShortcut.Any(char.IsControl))throw new InvalidDataException("Windows 返回了无效预览状态。");
        if(!value.HasSession)
        {if(value.Phase!=DeskflowPreviewPhase.Viewing||value.Id is not null)throw new InvalidDataException("Windows 未确认预览归还。");return;}
        if(!Guid.TryParseExact(value.Id,"N",out _)||value.ViewerComputer!=pairing.WindowsName&&value.ViewerComputer!=pairing.MacName||
            value.TargetComputer!=pairing.WindowsName&&value.TargetComputer!=pairing.MacName||value.TargetComputer==value.ViewerComputer||macViewer&&value.ViewerComputer!=pairing.MacName)
            throw new InvalidDataException("预览状态不属于当前配对设备。");
    }
    private void ObservePreviewState(DeskLinkState state)
    {
        if(state.InputPreview is{} observed)
        {
            ValidatePreviewResult(observed,false);Volatile.Write(ref previewHandoff,observed);
            if(observed.Phase==DeskflowPreviewPhase.Viewing&&!observed.HasSession&&state.PreviewHandoffSupported){ownedPreviewId=null;previewBeginPending=false;}
            else if(ownedPreviewId is{} owned&&observed.Id!=owned)ownedPreviewId=null;
        }
        if(state.DisplaySuppressed||state.InputPreview?.HasSession==true)PreservePreviewDisplayIntent(state);
        else if(IsConfirmedNewNormalFocus(state))ClearPreviewDisplayIntent();
        if(previewBeginPending||ownedPreviewId is not null||state.InputPreview?.HasSession==true||IsDisplaySuppressed(state))displaySwitch.SuppressThrough(state);
    }
    private void PreservePreviewDisplayIntent(DeskLinkState? state)
    {
        previewDisplayIntent=true;
        if(state is not{ProcessId:>0,Revision:>=0})return;
        var through=Math.Max(state.Revision,state.DisplaySuppressedThroughRevision);
        if(previewIntentCore is{} previous&&previous.ProcessId==state.ProcessId.Value)through=Math.Max(through,previous.ThroughRevision);
        previewIntentCore=(state.ProcessId.Value,through);
    }
    private void ClearPreviewDisplayIntent(){previewDisplayIntent=false;previewIntentCore=null;}
    private bool IsConfirmedNewNormalFocus(DeskLinkState state)=>previewDisplayIntent&&previewIntentCore is{} previous&&pairing is{} approved&&
        !previewBeginPending&&ownedPreviewId is null&&state.ProcessId==previous.ProcessId&&
        state.ProcessState==DeskLinkProcessState.Running&&state.NativeControlAvailable&&
        IsTrustedConnected(CurrentClientState)&&state.ConnectedComputers?.Contains(approved.MacName)==true&&
        (state.ActiveComputer==approved.WindowsName||state.ActiveComputer==approved.MacName)&&
        state.InputPreview is{Phase:DeskflowPreviewPhase.Viewing,HasSession:false}&&!state.DisplaySuppressed&&
        state.DisplaySuppressedThroughRevision>=0&&state.Revision>Math.Max(previous.ThroughRevision,state.DisplaySuppressedThroughRevision);
    private bool HasPreviewExitIntent=>previewDisplayIntent||previewBeginPending||ownedPreviewId is not null||previewHandoff.HasSession||windowsState?.DisplaySuppressed==true;
    private static bool IsDisplaySuppressed(DeskLinkState? state)=>state is{} value&&(value.DisplaySuppressed||value.Revision<=value.DisplaySuppressedThroughRevision);
    private async Task ReadAfterInputOnlyInsideAsync(CancellationToken token)
    {
        PreservePreviewDisplayIntent(windowsState);
        try{windowsState=await ReadWindowsStateAsync(token);PreservePreviewDisplayIntent(windowsState);displaySwitch.SuppressThrough(windowsState);controlReady=IsNativeReady(windowsState);}
        catch(Exception failure)when(IsControlError(failure)){controlReady=false;controlError="键鼠交接已返回状态，正在等待共享连接确认";}
    }
    private async Task<MacSwitchOutcome?> TryReturnPreviewForSwitchInsideAsync(string target,CancellationToken token)
    {
        if(previewBeginPending||ownedPreviewId is not null||previewHandoff.HasSession)
        {
            windowsState=await ReadWindowsStateAsync(token);controlReady=IsNativeReady(windowsState);
            var current=previewHandoff;
            if(current.HasSession)
            {
                if(target!=current.ViewerComputer)throw new InvalidOperationException("请先归还预览键鼠，再执行日常切换。");
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var returned=await control!.EndPreviewAsync(current.Id!,deadline.Token);ValidatePreviewResult(returned,false);Volatile.Write(ref previewHandoff,returned);
                if(returned.Phase==DeskflowPreviewPhase.Viewing&&!returned.HasSession){ownedPreviewId=null;previewBeginPending=false;}
                await ReadAfterInputOnlyInsideAsync(deadline.Token);
                var confirmed=returned.Phase==DeskflowPreviewPhase.Viewing&&!returned.HasSession;
                return new(confirmed,target,confirmed,"suppressed",confirmed?"":"键鼠归还尚未确认，请按 "+PreviewReturnShortcut);
            }
            if(previewBeginPending)throw new InvalidOperationException("预览交接尚未确认，请先按 "+PreviewReturnShortcut+" 归还。");
            // The master already returned this owned preview. Do not convert a repeated return click into display intent.
            if(IsDisplaySuppressed(windowsState)&&target==windowsState?.ActiveComputer)return new(true,target,true,"suppressed","");
        }
        return null;
    }
    private async Task<bool> ReturnPreviewBeforeStopInsideAsync()
    {
        if(!HasPreviewExitIntent)return false;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            if(control is not null)
            {
                windowsState=await ReadWindowsStateAsync(deadline.Token);
                if(!HasPreviewExitIntent)return false;
                displaySwitch.SuppressThrough(windowsState);
                if(previewHandoff.HasSession)
                {
                    var returned=await control.EndPreviewAsync(previewHandoff.Id!,deadline.Token);ValidatePreviewResult(returned,false);Volatile.Write(ref previewHandoff,returned);
                    if(returned.Phase==DeskflowPreviewPhase.Viewing&&!returned.HasSession){ownedPreviewId=null;previewBeginPending=false;}
                }
            }
            displayMessage="预览操作仅归还键鼠，未发送切屏命令。";displayRecoveryError=false;
        }
        catch(Exception failure)when(IsControlError(failure)){controlError="预览键鼠归还尚未确认，请按 "+PreviewReturnShortcut+"；未发送切屏命令。";}
        return true;
    }
}
