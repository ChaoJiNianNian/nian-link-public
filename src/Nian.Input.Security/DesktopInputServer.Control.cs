namespace Nian.Input.Security;

public sealed partial class DesktopInputServer
{
    private async Task<DesktopControlRpcReply> ApplyControlRpcAsync(Connection state,DesktopRequest request,string session,CancellationToken cancellationToken)
    {
        DesktopControlRpcReply Rejected(string error)=>new(2,false,request.Action,session,Error:error);
        if(!IsTrusted(state.Fingerprint,DevicePermission.Control))return Rejected("This device is not approved for full desktop control.");
        if(options is null)return Rejected("The native control service is unavailable.");
        if(request.Action=="state"&&options.ReadStateAsync is null||request.Action=="full-switch"&&options.FullSwitchAsync is null)
            return Rejected("The requested native control operation is unavailable.");
        if(request.Action=="full-switch")
        {
            if(request.FullSwitch is not{} command||!DesktopControlValidation.Request(command)||command.Sequence<=state.SwitchSequence)
                return Rejected("The full-switch request or sequence is invalid.");
            // Consume even on a failed/canceled host operation: control effects are never automatically retried.
            state.SwitchSequence=command.Sequence;
        }
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
        deadline.CancelAfter(request.Action=="state"?TimeSpan.FromSeconds(2):TimeSpan.FromSeconds(10));
        void PermissionChanged(string fingerprint)
        {
            if(fingerprint==state.Fingerprint&&!IsTrusted(fingerprint,DevicePermission.Control))
                try{deadline.Cancel();}catch(ObjectDisposedException){}
        }
        trust.Changed+=PermissionChanged;
        try
        {
            if(!IsTrusted(state.Fingerprint,DevicePermission.Control))return Rejected("Device control permission was revoked.");
            if(request.Action=="state")
            {
                var snapshot=await options.ReadStateAsync!(state.Fingerprint,deadline.Token).WaitAsync(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if(!IsTrusted(state.Fingerprint,DevicePermission.Control))return Rejected("Device control permission was revoked.");
                if(!DesktopControlValidation.State(snapshot))return Rejected("The native control state is invalid.");
                return new(2,true,request.Action,session,State:snapshot);
            }
            var result=await options.FullSwitchAsync!(state.Fingerprint,request.FullSwitch!,deadline.Token).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if(!IsTrusted(state.Fingerprint,DevicePermission.Control))return Rejected("Device control permission was revoked.");
            if(!DesktopControlValidation.Result(result))return Rejected("The native full-switch result is invalid.");
            var target=request.FullSwitch!.Target==DesktopSwitchTarget.Local?result.State.LocalName:result.State.RemoteName;
            if(result.InputTransferred&&result.State.ActiveComputer!=target)return Rejected("The native input target was not confirmed.");
            return new(2,true,request.Action,session,SwitchResult:result);
        }
        catch{return Rejected("The native control operation was canceled or could not be confirmed.");}
        finally{trust.Changed-=PermissionChanged;}
    }
}
