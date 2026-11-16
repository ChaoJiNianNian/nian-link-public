using Nian.Input;
using Nian.Input.Control;
using Nian.Input.Runtime;
using Nian.Input.Windows;

namespace DesktopToolbox.Windows.DeskSwitch;

public sealed partial class NativeIntegratedDeskSwitchService
{
    private sealed class ReceiverTarget(WindowsDesktopInputTarget target,Action changed) : IPreviewInputTarget
    {
        private readonly object gate=new();
        private long activeConsent;
        private bool active;
        public bool Active {get{lock(gate)return active&&target.IsReady&&target.ConsentVersion==activeConsent;}}
        public bool IsReady=>target.IsReady;
        public string UnavailableReason=>target.UnavailableReason;
        public long ConsentVersion=>target.ConsentVersion;
        internal bool Prepare()
        {lock(gate){target.SetArmed(true,TimeSpan.FromSeconds(2));active=target.IsReady;activeConsent=target.ConsentVersion;if(active)changed();return active;}}
        internal bool Renew(long consent){lock(gate)return active&&activeConsent==consent&&target.RenewLease(consent,TimeSpan.FromSeconds(2));}
        public void Apply(InputPacket packet)=>throw new InvalidOperationException("Windows accepts preview input only in this production topology.");
        public void ApplyPreview(ResolvedPreviewInput input,long expectedConsentVersion)=>target.ApplyPreview(input,expectedConsentVersion);
        public void ReleaseAll(){lock(gate){target.ReleaseAll();if(active){active=false;changed();}}}
        public void ReleaseAll(long expectedConsentVersion)
        {lock(gate){target.ReleaseAll(expectedConsentVersion);if(active&&activeConsent==expectedConsentVersion){active=false;changed();}}}
    }
    public PreviewFrameProof? BindCapturedFrame(byte[] jpeg,PreviewSourceGeometry? geometry)
    {
        var authority=frames;if(authority is null||geometry is null)return null;
        try{return authority.Register(jpeg,geometry);}catch{authority.Invalidate();return null;}
    }
    public void InvalidateCapturedFrames()
    {
        frames?.Invalidate();var target=receiverTarget;
        if(target?.Active==true)target.ReleaseAll(target.ConsentVersion);
    }
    public IPreviewControlChannel CreatePreviewChannel()=>new LazyPreviewChannel(this);
    private NativeInputConnection CreateTransport(DesktopControlMode mode)
    {
        lock(channelsGate)
        {
            var owner=running;
            if(owner is null||!SameRun(owner)||identity is null||trust is null||arbiter is null||Volatile.Read(ref stopping)!=0||(DesktopToolbox.DeskLink.DeskLinkProcessState)Volatile.Read(ref processState)!=DesktopToolbox.DeskLink.DeskLinkProcessState.Running)throw new InvalidOperationException("请先开始原生共享。");
            // Every send and heartbeat must still have this host's local UI protection, not just the original Begin.
            var connection=new NativeInputConnection(identity,trust,()=>Healthy()?CurrentEndpoint():null,mode,mode==DesktopControlMode.Preview?arbiter:null);
            connection.Stopped+=_=>{Interlocked.Increment(ref inputRevision);Publish();};
            channels.Add(connection);return connection;
        }
    }
    private async Task RemoveTransportAsync(NativeInputConnection connection)
    {
        try{await connection.DisposeAsync().ConfigureAwait(false);}finally{lock(channelsGate)channels.Remove(connection);}
    }
    private async Task PreparePreviewAsync(CancellationToken token)
    {
        var owner=running??throw new InvalidOperationException("请先开始原生共享。");
        if(!SameRun(owner)||Volatile.Read(ref stopping)!=0||!Healthy()||CurrentEndpoint() is not{} peer||!Allows(peer.Fingerprint,Nian.Input.Security.DevicePermission.PreviewControl))
            throw new InvalidOperationException("请先在两端批准当前设备的预览控制权限。");
        // This newer, locally authorized action invalidates an emergency already waiting for the switch gate.
        Interlocked.Exchange(ref pendingEmergencyReturn,null);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,owner.Token);
        await switchGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if(!SameRun(owner)||!Healthy()||CurrentEndpoint() is not{} endpoint||!Allows(endpoint.Fingerprint,Nian.Input.Security.DevicePermission.PreviewControl))
                throw new InvalidOperationException("请先在两端批准当前设备的预览控制权限。");
            // A remote action already holding this gate may have prepared its context after our entry invalidation.
            Interlocked.Exchange(ref pendingEmergencyReturn,null);
            if(physical is{} keyboard)await keyboard.StopAsync("preview-requested",linked.Token).ConfigureAwait(false);
        }
        finally{switchGate.Release();}
    }
    private sealed class LazyPreviewChannel(NativeIntegratedDeskSwitchService owner) : IPreviewControlChannel
    {
        private NativeInputConnection? current;
        public async Task BeginAsync(CancellationToken token)
        {
            await owner.PreparePreviewAsync(token).ConfigureAwait(false);
            var connection=owner.CreateTransport(DesktopControlMode.Preview);
            var previous=Interlocked.Exchange(ref current,connection);
            if(previous is not null){previous.Abort("preview-replaced");await owner.RemoveTransportAsync(previous).ConfigureAwait(false);}
            try{await ((IPreviewControlChannel)connection).BeginAsync(token).ConfigureAwait(false);}
            catch{Interlocked.CompareExchange(ref current,null,connection);await owner.RemoveTransportAsync(connection).ConfigureAwait(false);throw;}
        }
        public Task SendAsync(PreviewInputCommand command,CancellationToken token)=>((IPreviewControlChannel)(Volatile.Read(ref current)??throw new InvalidOperationException("Preview control is inactive."))).SendAsync(command,token);
        public Task HeartbeatAsync(CancellationToken token)=>((IPreviewControlChannel)(Volatile.Read(ref current)??throw new InvalidOperationException("Preview control is inactive."))).HeartbeatAsync(token);
        public async Task EndAsync(CancellationToken token)
        {
            var connection=Interlocked.Exchange(ref current,null);if(connection is null)return;
            try{await ((IPreviewControlChannel)connection).EndAsync(token).ConfigureAwait(false);}
            finally{await owner.RemoveTransportAsync(connection).ConfigureAwait(false);}
        }
    }
}
