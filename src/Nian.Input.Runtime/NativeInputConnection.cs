using System.Security.Authentication;
using Nian.Input.Control;
using Nian.Input.Security;

namespace Nian.Input.Runtime;

public sealed record NativeInputConnectionStop(long Generation,string Reason);

/// <summary>
/// One fixed input mode. Each Begin creates a new pinned TLS session; cleanup always refers to its original run.
/// End attempts a bounded peer ACK after disabling local events; its fallback local-release confirms local shutdown only.
/// </summary>
public sealed class NativeInputConnection : IPreviewControlChannel,IKeyboardControlChannel,IAsyncDisposable
{
    private static readonly TimeSpan ProvisionalLease=TimeSpan.FromSeconds(5),ActiveLease=TimeSpan.FromSeconds(2);
    private sealed class Run(long generation,TrustedEndpoint endpoint,DesktopInputClient client)
    {
        internal readonly long Generation=generation;
        internal readonly TrustedEndpoint Endpoint=endpoint;
        internal readonly DesktopInputClient Client=client;
        internal readonly CancellationTokenSource Cancel=new();
        internal readonly object CleanupGate=new();
        internal DesktopControlLease? Lease;
        internal Task? Cleanup;
        internal Task<InputReply>? Ending;
        internal string Session="";
        internal int Active,Stopped,EndRequested;
    }
    private readonly DeviceIdentity identity;
    private readonly DeviceTrustStore trust;
    private readonly Func<TrustedEndpoint?> currentEndpoint;
    private readonly DesktopControlArbiter? arbiter;
    private readonly bool ownsArbiter;
    private readonly SemaphoreSlim starting=new(1,1);
    private Run? current;
    private long generation;
    private int disposed;

    public NativeInputConnection(DeviceIdentity identity,DeviceTrustStore trust,Func<TrustedEndpoint?> currentEndpoint,
        DesktopControlMode mode,DesktopControlArbiter? previewArbiter=null,TimeProvider? clock=null)
    {
        ArgumentNullException.ThrowIfNull(identity);ArgumentNullException.ThrowIfNull(trust);ArgumentNullException.ThrowIfNull(currentEndpoint);
        if(!Enum.IsDefined(mode))throw new ArgumentOutOfRangeException(nameof(mode));
        this.identity=identity;this.trust=trust;this.currentEndpoint=currentEndpoint;Mode=mode;
        if(mode==DesktopControlMode.Preview){arbiter=previewArbiter??new DesktopControlArbiter(clock);ownsArbiter=previewArbiter is null;}
        trust.Changed+=TrustChanged;
    }
    public DesktopControlMode Mode {get;}
    public bool IsActive=>Volatile.Read(ref current) is{} run&&Volatile.Read(ref run.Active)!=0&&Volatile.Read(ref run.Stopped)==0;
    public long Generation=>Interlocked.Read(ref generation);
    /// <summary>Always queued outside the input lock. Consumers must match Generation before applying a delayed UI notification.</summary>
    public event Action<NativeInputConnectionStop>? Stopped;

    Task<InputReply> IKeyboardControlChannel.BeginAsync(CancellationToken token){RequireMode(DesktopControlMode.Keyboard);return BeginCoreAsync(token);}
    Task<InputReply> IKeyboardControlChannel.SendAsync(InputPacket packet,CancellationToken token)
    {RequireMode(DesktopControlMode.Keyboard);ArgumentNullException.ThrowIfNull(packet);return RequestAsync((client,ct)=>client.SendAsync(packet,ct),false,token);}
    Task<InputReply> IKeyboardControlChannel.HeartbeatAsync(CancellationToken token)
    {RequireMode(DesktopControlMode.Keyboard);return RequestAsync((client,ct)=>client.HeartbeatAsync(ct),true,token);}
    Task<InputReply> IKeyboardControlChannel.EndAsync(CancellationToken token){RequireMode(DesktopControlMode.Keyboard);return EndCoreAsync(token);}

    async Task IPreviewControlChannel.BeginAsync(CancellationToken token)
    {RequireMode(DesktopControlMode.Preview);RequireSuccess(await BeginCoreAsync(token).ConfigureAwait(false));}
    async Task IPreviewControlChannel.SendAsync(PreviewInputCommand command,CancellationToken token)
    {
        RequireMode(DesktopControlMode.Preview);ArgumentNullException.ThrowIfNull(command);
        RequireSuccess(await RequestAsync((client,ct)=>client.SendPreviewAsync(command,ct),false,token).ConfigureAwait(false));
    }
    async Task IPreviewControlChannel.HeartbeatAsync(CancellationToken token)
    {RequireMode(DesktopControlMode.Preview);RequireSuccess(await RequestAsync((client,ct)=>client.HeartbeatAsync(ct),true,token).ConfigureAwait(false));}
    async Task IPreviewControlChannel.EndAsync(CancellationToken token)
    {RequireMode(DesktopControlMode.Preview);await EndCoreAsync(token).ConfigureAwait(false);}

    private async Task<InputReply> BeginCoreAsync(CancellationToken token)
    {
        await starting.WaitAsync(token).ConfigureAwait(false);
        Run? run=null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
            if(Volatile.Read(ref current) is{} previous){StopRun(previous,"session-replaced");await CleanupAsync(previous).ConfigureAwait(false);}
            token.ThrowIfCancellationRequested();
            var endpoint=ReadApprovedEndpoint();
            run=new(Interlocked.Increment(ref generation),endpoint,new DesktopInputClient(identity,endpoint));
            Volatile.Write(ref current,run);Validate(run,false);
            if(arbiter is not null)
            {
                var captured=run;
                run.Lease=arbiter.Enter(endpoint.Fingerprint,DesktopControlMode.Preview,reason=>StopRun(captured,reason),ProvisionalLease,replaceSamePeer:true);
            }
            Validate(run,false);
            using var begin=CancellationTokenSource.CreateLinkedTokenSource(token,run.Cancel.Token);begin.CancelAfter(ProvisionalLease);
            await run.Client.ConnectAsync(begin.Token).ConfigureAwait(false);Validate(run,false);
            var reply=Mode==DesktopControlMode.Preview?await run.Client.BeginPreviewAsync(begin.Token).ConfigureAwait(false):await run.Client.BeginAsync(begin.Token).ConfigureAwait(false);
            RequireSuccess(reply);begin.Token.ThrowIfCancellationRequested();Validate(run,false);run.Session=reply.Session;
            if(arbiter is not null)
            {
                var captured=run;
                if(!arbiter.Execute(run.Lease!,()=>
                {
                    Validate(captured,false);
                    if(!arbiter.Renew(captured.Lease!,ActiveLease))throw new InvalidOperationException("Preview control ownership changed.");
                    Volatile.Write(ref captured.Active,1);
                }))throw new InvalidOperationException("Preview control ownership changed.");
            }
            else Volatile.Write(ref run.Active,1);
            begin.Token.ThrowIfCancellationRequested();Validate(run,true);return reply;
        }
        catch
        {
            if(run is not null){StopRun(run,"begin-failed");await CleanupAsync(run).ConfigureAwait(false);}
            throw;
        }
        finally{starting.Release();}
    }

    private async Task<InputReply> RequestAsync(Func<DesktopInputClient,CancellationToken,Task<InputReply>> send,bool heartbeat,CancellationToken token)
    {
        var run=Volatile.Read(ref current)??throw new InvalidOperationException("Begin this input mode first.");
        try
        {
            Validate(run,true);CheckOwnership(run);
            using var request=CancellationTokenSource.CreateLinkedTokenSource(token,run.Cancel.Token);
            var reply=await send(run.Client,request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();Validate(run,true);
            if(!reply.Ok){StopRun(run,"input-rejected");return reply;}
            if(arbiter is not null)
            {
                if(!arbiter.Execute(run.Lease!,()=>
                {
                    Validate(run,true);
                    // Only a fresh successful heartbeat ACK can extend this generation's active lease.
                    if(heartbeat&&!arbiter.Renew(run.Lease!,ActiveLease))throw new InvalidOperationException("Preview control ownership changed.");
                }))throw new InvalidOperationException("Preview control ownership changed.");
            }
            return reply;
        }
        catch{StopRun(run,"connection-or-permission-changed");throw;}
    }
    private void CheckOwnership(Run run)
    {
        if(arbiter is not null&&!arbiter.Execute(run.Lease!,()=>Validate(run,true)))throw new InvalidOperationException("Preview control ownership changed.");
    }
    private DevicePermission Permission=>Mode==DesktopControlMode.Preview?DevicePermission.PreviewControl:DevicePermission.Control;
    private TrustedEndpoint ReadApprovedEndpoint()
    {
        var endpoint=currentEndpoint()??throw new InvalidOperationException("Approve a native input endpoint locally first.");
        if(!trust.Allows(endpoint.Fingerprint,Permission))throw new AuthenticationException("The peer does not have locally approved permission for this input mode.");
        return endpoint;
    }
    private void Validate(Run run,bool active)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(!ReferenceEquals(Volatile.Read(ref current),run)||Volatile.Read(ref run.Stopped)!=0||Volatile.Read(ref run.EndRequested)!=0||active&&Volatile.Read(ref run.Active)==0)
            throw new InvalidOperationException("This input generation has stopped.");
        if(ReadApprovedEndpoint()!=run.Endpoint)throw new AuthenticationException("The approved native input endpoint changed.");
    }
    private void RequireMode(DesktopControlMode requested)
    {if(Mode!=requested)throw new InvalidOperationException("This transport is permanently bound to its original input mode.");}
    private static void RequireSuccess(InputReply reply)
    {if(!reply.Ok)throw new InvalidOperationException(reply.Error??"The receiver did not acknowledge input control.");}

    public void Abort(string reason="local-stop")
    {if(Volatile.Read(ref current) is{} run)StopRun(run,string.IsNullOrWhiteSpace(reason)?"local-stop":reason);}
    private void TrustChanged(string fingerprint)
    {
        if(Volatile.Read(ref current) is{} run&&string.Equals(run.Endpoint.Fingerprint,fingerprint,StringComparison.OrdinalIgnoreCase)&&!trust.Allows(fingerprint,Permission))
            StopRun(run,"permission-revoked");
    }
    private void StopRun(Run run,string reason)
    {
        var first=Interlocked.Exchange(ref run.Stopped,1)==0;
        if(first)
        {
            Volatile.Write(ref run.Active,0);
            try{run.Cancel.Cancel();}catch(ObjectDisposedException){}
            run.Client.Abort();
        }
        // A stop can race the assignment immediately following Enter; repeated cleanup must stop that exact token too.
        if(run.Lease is{} lease)arbiter?.Stop(lease,reason);
        if(!first)return;
        ThreadPool.QueueUserWorkItem(_=>NotifyStopped(run,reason));
        ThreadPool.QueueUserWorkItem(_=>{_ = CleanupQuietlyAsync(run);});
    }
    private void NotifyStopped(Run run,string reason)
    {
        if(Generation!=run.Generation||Stopped is not{} handlers)return;
        var value=new NativeInputConnectionStop(run.Generation,reason);
        foreach(Action<NativeInputConnectionStop> handler in handlers.GetInvocationList())try{handler(value);}catch{}
    }
    private async Task CleanupQuietlyAsync(Run run){try{await CleanupAsync(run).ConfigureAwait(false);}catch{}}
    private Task CleanupAsync(Run run)
    {
        TaskCompletionSource completion;
        lock(run.CleanupGate)
        {
            if(run.Cleanup is{} existing)return existing;
            completion=new(TaskCreationOptions.RunContinuationsAsynchronously);run.Cleanup=completion.Task;
        }
        _ = CompleteCleanupAsync(run,completion);return completion.Task;
    }
    private async Task CompleteCleanupAsync(Run run,TaskCompletionSource completion)
    {
        try
        {
            StopRun(run,"session-ended");await run.Client.DisposeAsync().ConfigureAwait(false);
            Interlocked.CompareExchange(ref current,null,run);run.Cancel.Dispose();completion.TrySetResult();
        }
        catch(Exception error){Interlocked.CompareExchange(ref current,null,run);completion.TrySetException(error);}
    }
    private async Task<InputReply> EndCoreAsync(CancellationToken token)
    {
        // Capture before the first await. A delayed old End may never inspect or close a replacement client.
        var run=Volatile.Read(ref current);
        if(run is null)return LocalRelease(null);
        Interlocked.Exchange(ref run.EndRequested,1);Volatile.Write(ref run.Active,0);
        // A pending Begin has no acknowledged peer session; cancel its handshake synchronously.
        if(run.Session.Length==0)StopRun(run,"local-end");
        TaskCompletionSource<InputReply>? completion=null;Task<InputReply> ending;
        lock(run.CleanupGate)
        {
            if(run.Ending is{} existing)ending=existing;
            else{completion=new(TaskCreationOptions.RunContinuationsAsynchronously);run.Ending=ending=completion.Task;}
        }
        if(completion is not null)_ = EndRunAsync(run,completion);
        return await ending.WaitAsync(token).ConfigureAwait(false);
    }
    private async Task EndRunAsync(Run run,TaskCompletionSource<InputReply> completion)
    {
        InputReply? acknowledged=null;
        try
        {
            if(Volatile.Read(ref run.Stopped)==0&&run.Session.Length>0)
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var reply=await run.Client.EndAsync(deadline.Token).ConfigureAwait(false);
                if(reply.Ok)acknowledged=reply;
            }
        }
        catch{/* Interrupted requests are closed below; no remote ACK is claimed. */}
        finally
        {
            StopRun(run,"local-end");
            try{await CleanupAsync(run).ConfigureAwait(false);completion.TrySetResult(acknowledged??LocalRelease(run));}
            catch(Exception error){completion.TrySetException(error);}
        }
    }
    private static InputReply LocalRelease(Run? run)=>new(true,"local-release",run?.Session??"",Error:"Local transport stopped; peer release was not acknowledged.");
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;trust.Changed-=TrustChanged;
        Abort("disposed");await starting.WaitAsync().ConfigureAwait(false);
        try{if(Volatile.Read(ref current) is{} run)await CleanupAsync(run).ConfigureAwait(false);}
        finally{starting.Release();if(ownsArbiter)arbiter?.Dispose();}
    }
}
