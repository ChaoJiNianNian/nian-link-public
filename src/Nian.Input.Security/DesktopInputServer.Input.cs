using Nian.Input.Control;

namespace Nian.Input.Security;

public sealed partial class DesktopInputServer
{
    private static DevicePermission PermissionFor(DesktopControlMode mode)=>mode==DesktopControlMode.Preview?DevicePermission.PreviewControl:DevicePermission.Control;

    private InputReply ApplyNative(Connection state,DesktopRequest request,string session)
    {
        InputReply Reply(bool ok,string? error=null)=>new(ok,request.Action,session,state.Sequence,error);
        if(request.Action is "begin" or "begin-preview")return BeginNative(state,request,session);
        if(request.Action=="end"){Release(state);return Reply(true);}

        DesktopControlLease? lease;
        lock(inputGate)
        {
            if(!state.Begun)
                return request.Action=="heartbeat"&&!state.WasBegun&&IsTrusted(state.Fingerprint)?Reply(true):Reply(false,"Begin desktop input first.");
            lease=state.Lease;
        }
        if(lease is null)return Reply(false,"The local control session ended.");

        string? error=null;
        try
        {
            // The arbiter always precedes inputGate. Its watchdog can synchronously release this same state.
            var executed=arbiter!.Execute(lease,()=>
            {
                lock(inputGate)
                {
                    if(!state.Begun||state.Lease!=lease)throw new InvalidOperationException("The local control session ended.");
                    if(!IsTrusted(state.Fingerprint,PermissionFor(state.Mode)))throw new InvalidOperationException("Device input permission was revoked.");
                    if(target.ConsentVersion!=state.Consent||!target.IsReady)throw new InvalidOperationException("Local input consent changed or is unavailable.");
                    if(state.Mode==DesktopControlMode.Preview&&(options!.PreviewFrames?.HasFreshFrame!=true||state.PreviewGeometry?.SameSource(options.PreviewFrames.CurrentGeometry)!=true))
                        throw new InvalidOperationException("Refresh the controlled preview before operating.");
                    if(options!.RenewInput?.Invoke(state.Fingerprint,state.Mode,state.Consent)==false||target.ConsentVersion!=state.Consent||!target.IsReady)
                        throw new InvalidOperationException("The local input lease could not be renewed.");

                    if(request.Action=="heartbeat")return;
                    if(state.Mode==DesktopControlMode.Keyboard)
                    {
                        if(request.Action!="input"||request.Preview is not null||request.Packet is not{} packet||!InputValidation.Packet(packet,state.Sequence))
                            throw new InvalidOperationException("The keyboard event or sequence is invalid.");
                        target.Apply(packet,state.Consent);state.Sequence=packet.Sequence;
                    }
                    else
                    {
                        if(request.Action!="preview"||request.Packet is not null||request.Preview is not{} preview||preview.Sequence<=state.Sequence||!PreviewInputValidation.Command(preview))
                            throw new InvalidOperationException("The preview event or sequence is invalid.");
                        var resolved=options.PreviewFrames!.Resolve(preview);
                        if(state.PreviewGeometry?.SameSource(resolved.Geometry)!=true)throw new InvalidOperationException("The controlled preview source changed.");
                        ((IPreviewInputTarget)target).ApplyPreview(resolved,state.Consent);state.Sequence=preview.Sequence;
                    }
                }
            });
            if(!executed)return Reply(false,"The local control session ended.");
            if(!arbiter.Renew(lease,options!.InputLease))return Reply(false,"The local control session ended.");
            return Reply(true);
        }
        catch
        {error="The local desktop rejected this input or its control lease.";}
        // Execute has already cleared its token and synchronously released on exceptions.
        return Reply(false,error);
    }

    private InputReply BeginNative(Connection state,DesktopRequest request,string session)
    {
        InputReply Reply(bool ok,string? error=null)=>new(ok,request.Action,session,state.Sequence,error);
        var mode=request.Action=="begin-preview"?DesktopControlMode.Preview:DesktopControlMode.Keyboard;
        lock(inputGate)
        {
            if(state.Begun)return Reply(false,"Input is already active.");
            if(!IsTrusted(state.Fingerprint,PermissionFor(mode)))return Reply(false,"This device is not approved for the requested input mode.");
            if(mode==DesktopControlMode.Preview&&(target is not IPreviewInputTarget||options!.PreviewFrames?.HasFreshFrame!=true))
                return Reply(false,"A fresh controlled preview is unavailable.");
        }

        DesktopControlLease? lease=null;
        try
        {
            lease=arbiter!.Enter(state.Fingerprint,mode,_=>
            {
                lock(inputGate)
                {
                    if(state.Lease!=lease)return;
                    state.Lease=null;ReleaseCore(state);
                }
            },options!.InputLease);
            lock(inputGate)state.Lease=lease;
            var prepared=arbiter.Execute(lease,()=>
            {
                lock(inputGate)
                {
                    if(!IsTrusted(state.Fingerprint,PermissionFor(mode)))throw new InvalidOperationException("Device input permission was revoked.");
                    state.Mode=mode;var before=target.ConsentVersion;var accepted=false;
                    try{accepted=options.PrepareInput?.Invoke(state.Fingerprint,mode)??true;}
                    finally
                    {
                        state.Consent=target.ConsentVersion;
                        // A partially successful preparation can only release the generation it just created.
                        state.Begun=state.Consent!=before;
                        if(state.Begun)active=state;
                    }
                    if(!accepted||!target.IsReady)throw new InvalidOperationException("Local desktop control is not allowed or unavailable.");
                    if(mode==DesktopControlMode.Preview)
                    {
                        state.PreviewGeometry=options.PreviewFrames!.CurrentGeometry;
                        if(state.PreviewGeometry is null||!options.PreviewFrames.HasFreshFrame)throw new InvalidOperationException("The controlled preview source is unavailable.");
                    }
                    state.Mode=mode;state.Begun=true;state.WasBegun=true;active=state;Notify("active");
                }
            });
            return prepared?Reply(true):Reply(false,"The local control session ended.");
        }
        catch
        {
            if(lease is not null)arbiter!.Stop(lease,"begin-rejected");
            return Reply(false,"The local desktop could not begin the requested control mode.");
        }
    }
}
