using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Nian.Input.Control;

namespace Nian.Input.Security;

/// <summary>Separate v2 receiver. Every connection requires a per-device TLS client certificate.</summary>
public sealed partial class DesktopInputServer : IAsyncDisposable
{
    private sealed class Connection(string fingerprint,TcpClient tcp)
    {internal readonly string Fingerprint=fingerprint;internal readonly TcpClient Tcp=tcp;internal bool Begun,WasBegun;internal long Consent,Sequence,SwitchSequence;internal DesktopControlMode Mode;internal DesktopControlLease? Lease;internal PreviewSourceGeometry? PreviewGeometry;}
    private sealed class CommitLease(object gate) : IDisposable {public void Dispose()=>Monitor.Exit(gate);}
    private readonly DeviceIdentity identity;private readonly DeviceTrustStore trust;private readonly IPAddress address;private readonly int requestedPort;
    private readonly IInputTarget target;private readonly Func<PairingApproval,CancellationToken,Task<bool>> confirm;private readonly TimeProvider clock;
    private readonly DesktopInputServerOptions? options;private readonly DesktopControlArbiter? arbiter;private readonly bool ownsArbiter;
    private readonly SemaphoreSlim lifecycle=new(1,1),owner=new(1,1),limit=new(4,4);private readonly CancellationTokenSource lifetime=new();
    private readonly ConcurrentDictionary<int,Task> connections=new();private readonly object invitationGate=new(),inputGate=new();
    private TcpListener? listener;private Task accepting=Task.CompletedTask;private byte[]? invitationHash;private DateTimeOffset invitationExpiry;private DevicePermission invitationPermissions;private long invitationGeneration;private int failedInvitations,id,disposed;private Connection? active;
    private readonly HashSet<string> pendingApprovals=new(StringComparer.Ordinal);
    public DesktopInputServer(DeviceIdentity identity,DeviceTrustStore trust,IPAddress address,int port,IInputTarget target,Func<PairingApproval,CancellationToken,Task<bool>> confirm,TimeProvider? timeProvider=null,DesktopInputServerOptions? options=null)
    {
        ArgumentNullException.ThrowIfNull(identity);ArgumentNullException.ThrowIfNull(trust);ArgumentNullException.ThrowIfNull(target);ArgumentNullException.ThrowIfNull(confirm);ArgumentNullException.ThrowIfNull(address);
        if(port is <0 or >65535||address.Equals(IPAddress.Any)||address.Equals(IPAddress.IPv6Any))throw new ArgumentException("Choose a concrete isolated receiver address.");
        this.identity=identity;this.trust=trust;this.address=address;requestedPort=port;this.target=target;this.confirm=confirm;clock=timeProvider??TimeProvider.System;
        this.options=options;
        if(options is not null)
        {
            if(options.InputLease<=TimeSpan.Zero||options.InputLease>TimeSpan.FromSeconds(2))throw new ArgumentOutOfRangeException(nameof(options));
            arbiter=options.Arbiter??new DesktopControlArbiter(clock);ownsArbiter=options.Arbiter is null;
        }
        trust.Changed+=TrustChanged;
    }
    public int Port=>listener is null?throw new InvalidOperationException("Start the independent receiver first."):((IPEndPoint)listener.LocalEndpoint).Port;
    public event Action<string>? StatusChanged;
    public async Task StartAsync(CancellationToken cancellationToken=default)
    {
        await lifecycle.WaitAsync(cancellationToken);try{ObjectDisposedException.ThrowIf(disposed!=0,this);if(listener is not null)return;listener=new TcpListener(address,requestedPort);listener.Start(4);accepting=AcceptAsync();Notify("listening");}finally{lifecycle.Release();}
    }
    public DeviceInvitation CreateInvitation(DevicePermission permissions=DevicePermission.Control)
    {
        if(!TrustValidation.Permissions(permissions,false))throw new ArgumentOutOfRangeException(nameof(permissions));
        ObjectDisposedException.ThrowIf(disposed!=0,this);var port=Port;var secret=RandomNumberGenerator.GetBytes(32);
        try {lock(invitationGate){ClearInvitation();invitationHash=SHA256.HashData(secret);invitationPermissions=permissions;invitationExpiry=clock.GetUtcNow().AddMinutes(2);failedInvitations=0;invitationGeneration++;return new(address.ToString(),port,identity.Fingerprint,Convert.ToBase64String(secret),invitationExpiry,Permissions:permissions);}}
        finally{CryptographicOperations.ZeroMemory(secret);}
    }
    public void CloseInvitations(){lock(invitationGate){ClearInvitation();invitationGeneration++;}}
    private void ClearInvitation(){if(invitationHash is not null)CryptographicOperations.ZeroMemory(invitationHash);invitationHash=null;}
    private bool InvitationOpen(){lock(invitationGate)return invitationHash is not null&&failedInvitations<5&&clock.GetUtcNow()<invitationExpiry;}
    private bool IsTrusted(string fingerprint,DevicePermission? permission=null)
    {lock(invitationGate)return !pendingApprovals.Contains(fingerprint)&&(permission is{} requested?trust.Allows(fingerprint,requested):trust.Allows(fingerprint)||options is not null&&trust.Allows(fingerprint,DevicePermission.PreviewControl));}
    private async Task AcceptAsync()
    {
        try{while(!lifetime.IsCancellationRequested){var tcp=await listener!.AcceptTcpClientAsync(lifetime.Token);tcp.NoDelay=true;if(!await limit.WaitAsync(0,lifetime.Token)){tcp.Dispose();continue;}var connectionId=Interlocked.Increment(ref id);var task=ServeAsync(tcp);connections[connectionId]=task;_ = task.ContinueWith(_=>connections.TryRemove(connectionId,out var ignored),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);}}
        catch(Exception e)when(lifetime.IsCancellationRequested&&e is OperationCanceledException or SocketException or ObjectDisposedException){}
    }
    private async Task ServeAsync(TcpClient tcp)
    {
        Connection? state=null;var owns=false;
        try
        {
            using(tcp)await using(var secure=new SslStream(tcp.GetStream(),false,(_,certificate,_,_)=>
            {if(!DesktopProtocol.CertificateValid(certificate))return false;var fingerprint=Convert.ToHexString(SHA256.HashData(certificate!.GetRawCertData()));return IsTrusted(fingerprint)||InvitationOpen();}))
            {
                using var authentication=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);authentication.CancelAfter(TimeSpan.FromSeconds(5));
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=identity.Certificate,ClientCertificateRequired=true,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,EncryptionPolicy=EncryptionPolicy.RequireEncryption,AllowTlsResume=false,AllowRenegotiation=false},authentication.Token);
                if(!DesktopProtocol.CertificateValid(secure.RemoteCertificate))throw new AuthenticationException("A device certificate is required.");
                var fingerprint=Convert.ToHexString(SHA256.HashData(secure.RemoteCertificate!.GetRawCertData()));
                var hello=await InputWire.ReadAsync<DesktopRequest>(secure,authentication.Token);
                if(hello.Version!=2||hello.Session!=""||hello.Packet is not null||hello.Preview is not null||hello.FullSwitch is not null)throw new AuthenticationException("The independent protocol greeting is invalid.");
                if(hello.Action=="pair"){await PairAsync(secure,fingerprint,hello,((IPEndPoint)tcp.Client.RemoteEndPoint!).Address.ToString());return;}
                if(hello.Action!="hello"||hello.DeviceName is not null||hello.Invitation is not null||hello.Permissions is not null||hello.ReceiverPort is not null||!IsTrusted(fingerprint))throw new AuthenticationException("This device is not approved for control.");
                if(options is null)
                {
                    if(!await owner.WaitAsync(0,authentication.Token)){await InputWire.WriteAsync(secure,new InputReply(false,"hello","",Error:"The receiver has another controller."),authentication.Token);return;}
                    owns=true;
                }
                state=new(fingerprint,tcp);var session=Guid.NewGuid().ToString("N");
                await InputWire.WriteAsync(secure,new InputReply(true,"hello",session),authentication.Token);Notify("connected");
                while(!lifetime.IsCancellationRequested)
                {
                    bool begun;lock(inputGate)begun=state.Begun;
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);deadline.CancelAfter(begun?1500:30000);
                    var request=await InputWire.ReadAsync<DesktopRequest>(secure,deadline.Token);
                    if(request.Version!=2||request.Session!=session||request.Invitation is not null||request.DeviceName is not null||request.Permissions is not null||request.ReceiverPort is not null||request.Action is not("begin" or "begin-preview" or "input" or "preview" or "heartbeat" or "end" or "state" or "full-switch")||request.Action!="input"&&request.Packet is not null||request.Action!="preview"&&request.Preview is not null||request.Action!="full-switch"&&request.FullSwitch is not null)
                    {Release(state);await InputWire.WriteAsync(secure,new InputReply(false,"rejected",session,state.Sequence,"The request or session is invalid."),deadline.Token);break;}
                    if(request.Action is "state" or "full-switch")
                    {
                        using var controlDeadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);controlDeadline.CancelAfter(TimeSpan.FromSeconds(12));
                        var controlReply=await ApplyControlRpcAsync(state,request,session,controlDeadline.Token);
                        await InputWire.WriteAsync(secure,controlReply,controlDeadline.Token);continue;
                    }
                    var reply=options is null?ApplyLegacy(state,request,session):ApplyNative(state,request,session);
                    await InputWire.WriteAsync(secure,reply,deadline.Token);
                }
            }
        }
        catch(Exception e)when(e is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or CryptographicException)
        {if(!lifetime.IsCancellationRequested)Notify("connection-ended:"+e.GetType().Name);}
        finally{if(state is not null)Release(state);if(owns)owner.Release();tcp.Dispose();limit.Release();Notify("disconnected");}
    }
    private InputReply ApplyLegacy(Connection state,DesktopRequest request,string session)
    {
        lock(inputGate)
        {
        InputReply Reply(bool ok,string? error=null)=>new(ok,request.Action,session,state.Sequence,error);
        if(!IsTrusted(state.Fingerprint)){Release(state);return Reply(false,"Device control permission was revoked.");}
        if(request.Action=="end"){Release(state);return Reply(true);}
        if(request.Action is "begin-preview" or "preview"){Release(state);return Reply(false,"Preview control is unavailable.");}
        if(state.Begun&&target.ConsentVersion!=state.Consent){Release(state);return Reply(false,"Local input consent changed.");}
        if((state.Begun||request.Action=="begin")&&!target.IsReady){Release(state);return Reply(false,"Local desktop control is not allowed or unavailable.");}
        if(request.Action=="begin")
        {if(state.Begun)return Reply(false,"Input is already active.");state.Consent=target.ConsentVersion;state.Begun=true;active=state;Notify("active");return Reply(true);}
        if(request.Action=="heartbeat")return Reply(true);
        if(!state.Begun)return Reply(false,"Begin desktop input first.");
        if(request.Packet is not{} packet||!InputValidation.Packet(packet,state.Sequence)){Release(state);return Reply(false,"The input event or sequence is invalid.");}
        try{target.Apply(packet,state.Consent);state.Sequence=packet.Sequence;return Reply(true);}catch{Release(state);return Reply(false,"The local desktop could not apply input.");}
        }
    }
    private async Task PairAsync(SslStream secure,string fingerprint,DesktopRequest request,string sourceAddress)
    {
        if(request.ReceiverPort is{} receiverPort&&receiverPort is <1 or >65535)throw new AuthenticationException("The advertised receiver port is invalid.");
        long generation;DateTimeOffset expiry;DevicePermission permissions;
        lock(invitationGate)
        {
            if(invitationHash is null||clock.GetUtcNow()>=invitationExpiry||failedInvitations>=5)throw new AuthenticationException("The invitation is unavailable or invalid.");
            if((request.Permissions??DevicePermission.Control)!=invitationPermissions)throw new AuthenticationException("The invitation permissions changed.");
            if(!TrustValidation.DeviceName(request.DeviceName)||!InputValidation.TryToken(request.Invitation,out var supplied)){failedInvitations++;if(failedInvitations>=5)ClearInvitation();throw new AuthenticationException("The invitation is unavailable or invalid.");}
            try{if(!CryptographicOperations.FixedTimeEquals(invitationHash,SHA256.HashData(supplied))){failedInvitations++;if(failedInvitations>=5)ClearInvitation();throw new AuthenticationException("The invitation is unavailable or invalid.");}}
            finally{CryptographicOperations.ZeroMemory(supplied);}
            generation=invitationGeneration;expiry=invitationExpiry;permissions=invitationPermissions;ClearInvitation();pendingApprovals.Add(fingerprint); // consume before waiting for a local decision
        }
        try
        {
            using var approval=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var remaining=expiry-clock.GetUtcNow();if(remaining<=TimeSpan.Zero)throw new AuthenticationException("The invitation expired.");approval.CancelAfter(remaining);
            var accepted=await confirm(new(request.DeviceName!,fingerprint,permissions,expiry,sourceAddress,request.ReceiverPort),approval.Token).WaitAsync(approval.Token);
            lock(invitationGate)if(!accepted||generation!=invitationGeneration||clock.GetUtcNow()>=expiry)throw new AuthenticationException("Local pairing approval was declined or expired.");
            IDisposable GuardCommit()
            {
                Monitor.Enter(invitationGate);
                try
                {
                    if(generation!=invitationGeneration||clock.GetUtcNow()>=expiry||approval.IsCancellationRequested)throw new AuthenticationException("The invitation was closed or expired.");
                    return new CommitLease(invitationGate);
                }
                catch{Monitor.Exit(invitationGate);throw;}
            }
            // The invitation validity check, atomic JSON rename, in-memory permission and approval completion
            // share one short synchronous commit boundary. Closing invitations cannot race a half-grant.
            await trust.GrantAfterApprovalAsync(new(fingerprint,request.DeviceName!,permissions,clock.GetUtcNow()),GuardCommit,()=>pendingApprovals.Remove(fingerprint),approval.Token);
            options?.Paired?.Invoke(new(request.DeviceName!,fingerprint,permissions,sourceAddress,request.ReceiverPort));
            await InputWire.WriteAsync(secure,new PairingReply(2,true,"pair",fingerprint,identity.DeviceName,permissions),approval.Token);
            Notify("paired");
        }
        finally
        {
            lock(invitationGate)pendingApprovals.Remove(fingerprint);
        }
    }
    private void TrustChanged(string fingerprint)
    {
        if(arbiter is null)
        {
            lock(inputGate)if(active is{} legacy&&legacy.Fingerprint==fingerprint&&!IsTrusted(fingerprint,DevicePermission.Control))
            {ReleaseCore(legacy);legacy.Tcp.Dispose();Notify("revoked");}
            return;
        }
        Connection? state;DesktopControlLease? lease;DesktopControlMode mode;
        lock(inputGate){state=active is{} current&&current.Fingerprint==fingerprint?current:null;lease=state?.Lease;mode=state?.Mode??DesktopControlMode.Keyboard;}
        // Stop the exact observed generation. A permission update cannot tear down a replacement session.
        if(lease is not null&&!IsTrusted(fingerprint,PermissionFor(mode))&&arbiter.Stop(lease,"permission-revoked"))Notify("revoked");
    }
    private void Release(Connection state)
    {
        if(arbiter is not null){DesktopControlLease? lease;lock(inputGate)lease=state.Lease;if(lease is not null)arbiter.Stop(lease,"session-ended");return;}
        lock(inputGate)ReleaseCore(state);
    }
    private void ReleaseCore(Connection state)
    {if(!state.Begun)return;state.Begun=false;state.Lease=null;if(ReferenceEquals(active,state))active=null;try{target.ReleaseAll(state.Consent);Notify("released");}catch{Notify("release-failed");}}
    private void Notify(string status){try{StatusChanged?.Invoke(status);}catch{}}
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;trust.Changed-=TrustChanged;CloseInvitations();lifetime.Cancel();
        Connection? state;lock(inputGate)state=active;if(state is not null){Release(state);state.Tcp.Dispose();}
        await lifecycle.WaitAsync();try{listener?.Stop();await accepting;await Task.WhenAll(connections.Values.ToArray());listener=null;}finally{lifecycle.Release();lifetime.Dispose();if(ownsArbiter)arbiter?.Dispose();}
    }
}
