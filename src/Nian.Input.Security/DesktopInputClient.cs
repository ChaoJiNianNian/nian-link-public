using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Nian.Input.Control;

namespace Nian.Input.Security;

public sealed class DesktopInputClient : IAsyncDisposable
{
    private readonly DeviceIdentity identity;private readonly TrustedEndpoint endpoint;private readonly SemaphoreSlim requests=new(1,1);private readonly CancellationTokenSource lifetime=new();
    private TcpClient? tcp;private SslStream? secure;private string session="";private int disposed;
    public DesktopInputClient(DeviceIdentity identity,TrustedEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(identity);ArgumentNullException.ThrowIfNull(endpoint);
        if(!TrustValidation.Endpoint(endpoint.Host,endpoint.Port,endpoint.Fingerprint)||!TrustValidation.DeviceName(endpoint.DeviceName))throw new ArgumentException("The trusted endpoint is invalid.");
        this.identity=identity;this.endpoint=endpoint;
    }
    public event Action<string>? StatusChanged;
    public async Task ConnectAsync(CancellationToken cancellationToken=default)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);deadline.CancelAfter(TimeSpan.FromSeconds(5));await requests.WaitAsync(deadline.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed!=0,this);if(secure is not null)throw new InvalidOperationException("This device is already connected.");
            tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync(endpoint.Host,endpoint.Port,deadline.Token);secure=await AuthenticateAsync(tcp,identity,endpoint.Fingerprint,deadline.Token);
            await InputWire.WriteAsync(secure,new DesktopRequest(2,"hello",""),deadline.Token);var reply=await InputWire.ReadAsync<InputReply>(secure,deadline.Token);
            if(!reply.Ok||reply.Action!="hello"||!Guid.TryParseExact(reply.Session,"N",out _))throw new AuthenticationException("The peer rejected this device or has another controller.");
            session=reply.Session;Notify("connected");
        }
        catch{Close();throw;}finally{requests.Release();}
    }
    public static Task<TrustedEndpoint> PairAsync(DeviceIdentity identity,DeviceInvitation invitation,CancellationToken cancellationToken=default)=>PairCoreAsync(identity,invitation,null,cancellationToken);
    public static Task<TrustedEndpoint> PairAsync(DeviceIdentity identity,DeviceInvitation invitation,int advertisedReceiverPort,CancellationToken cancellationToken=default)
    {
        if(advertisedReceiverPort is <1 or >65535)throw new ArgumentOutOfRangeException(nameof(advertisedReceiverPort));
        return PairCoreAsync(identity,invitation,advertisedReceiverPort,cancellationToken);
    }
    private static async Task<TrustedEndpoint> PairCoreAsync(DeviceIdentity identity,DeviceInvitation invitation,int? receiverPort,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);ArgumentNullException.ThrowIfNull(invitation);invitation.Validate();
        var remaining=invitation.ExpiresAt-DateTimeOffset.UtcNow;if(remaining<=TimeSpan.Zero||remaining>TimeSpan.FromMinutes(2.1))throw new AuthenticationException("The invitation has expired or has an invalid lifetime.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(remaining);
        using var tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync(invitation.Host,invitation.Port,deadline.Token);
        await using var stream=await AuthenticateAsync(tcp,identity,invitation.Fingerprint,deadline.Token);
        await InputWire.WriteAsync(stream,new DesktopRequest(2,"pair","",identity.DeviceName,invitation.Secret,Permissions:invitation.Permissions,ReceiverPort:receiverPort),deadline.Token);
        var reply=await InputWire.ReadAsync<PairingReply>(stream,deadline.Token);
        if(reply.Version!=2||!reply.Ok||reply.Action!="pair"||reply.ClientFingerprint!=identity.Fingerprint||!TrustValidation.DeviceName(reply.DeviceName)||reply.Permissions!=invitation.Permissions)throw new AuthenticationException("The receiver did not approve this device.");
        return new(invitation.Host,invitation.Port,invitation.Fingerprint,reply.DeviceName);
    }
    private static async Task<SslStream> AuthenticateAsync(TcpClient tcp,DeviceIdentity identity,string fingerprint,CancellationToken ct)
    {
        var stream=new SslStream(tcp.GetStream(),false,(_,certificate,_,_)=>DesktopProtocol.CertificateValid(certificate)&&TrustValidation.Matches(fingerprint,certificate!.GetRawCertData()));
        try
        {
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="nian-independent-desktop-v2",EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,EncryptionPolicy=EncryptionPolicy.RequireEncryption,AllowTlsResume=false,AllowRenegotiation=false,ClientCertificates=new X509CertificateCollection{identity.Certificate}},ct);
            if(!DesktopProtocol.CertificateValid(stream.RemoteCertificate)||!TrustValidation.Matches(fingerprint,stream.RemoteCertificate!.GetRawCertData()))throw new AuthenticationException("The paired device certificate did not match.");return stream;
        }
        catch{await stream.DisposeAsync();throw;}
    }
    public Task<InputReply> BeginAsync(CancellationToken cancellationToken=default)=>RequestAsync("begin",null,cancellationToken);
    public Task<InputReply> SendAsync(InputPacket packet,CancellationToken cancellationToken=default){ArgumentNullException.ThrowIfNull(packet);return RequestAsync("input",packet,cancellationToken);}
    public Task<InputReply> BeginPreviewAsync(CancellationToken cancellationToken=default)=>RequestAsync("begin-preview",null,cancellationToken);
    public Task<InputReply> SendPreviewAsync(PreviewInputCommand command,CancellationToken cancellationToken=default){ArgumentNullException.ThrowIfNull(command);return RequestAsync("preview",null,cancellationToken,command);}
    public Task<InputReply> HeartbeatAsync(CancellationToken cancellationToken=default)=>RequestAsync("heartbeat",null,cancellationToken);
    public Task<InputReply> EndAsync(CancellationToken cancellationToken=default)=>RequestAsync("end",null,cancellationToken);
    public async Task<DesktopControlState> GetStateAsync(CancellationToken cancellationToken=default)
    {
        var reply=await RequestControlAsync("state",null,cancellationToken);
        if(!DesktopControlValidation.State(reply.State)||reply.SwitchResult is not null)throw new InvalidDataException("The native control state is invalid.");
        return reply.State!;
    }
    public async Task<DesktopSwitchResult> FullSwitchAsync(DesktopFullSwitchRequest command,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(command);if(!DesktopControlValidation.Request(command))throw new ArgumentException("The native full-switch request is invalid.",nameof(command));
        var reply=await RequestControlAsync("full-switch",command,cancellationToken);
        if(!DesktopControlValidation.Result(reply.SwitchResult)||reply.State is not null)throw new InvalidDataException("The native full-switch result is invalid.");
        var result=reply.SwitchResult!;var target=command.Target==DesktopSwitchTarget.Local?result.State.LocalName:result.State.RemoteName;
        if(result.InputTransferred&&result.State.ActiveComputer!=target)throw new InvalidDataException("The native input target was not confirmed.");
        return result;
    }
    private async Task<DesktopControlRpcReply> RequestControlAsync(string action,DesktopFullSwitchRequest? command,CancellationToken cancellationToken)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);deadline.CancelAfter(action=="state"?TimeSpan.FromSeconds(3):TimeSpan.FromSeconds(12));
        await requests.WaitAsync(deadline.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed!=0,this);if(secure is null||session.Length==0)throw new InvalidOperationException("Connect the approved desktop device first.");
            await InputWire.WriteAsync(secure,new DesktopRequest(2,action,session,FullSwitch:command),deadline.Token);
            var reply=await InputWire.ReadAsync<DesktopControlRpcReply>(secure,deadline.Token);
            if(reply.Version!=2||reply.Action!=action||reply.Session!=session)throw new InvalidDataException("The native control response did not match the request.");
            if(!reply.Ok)throw new InvalidOperationException(reply.Error??"The native control request was rejected.");
            return reply;
        }
        catch{Close();throw;}finally{requests.Release();}
    }
    private async Task<InputReply> RequestAsync(string action,InputPacket? packet,CancellationToken ct,PreviewInputCommand? preview=null)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);deadline.CancelAfter(1500);await requests.WaitAsync(deadline.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed!=0,this);if(secure is null||session.Length==0)throw new InvalidOperationException("Connect the approved desktop device first.");
            await InputWire.WriteAsync(secure,new DesktopRequest(2,action,session,Packet:packet,Preview:preview),deadline.Token);var reply=await InputWire.ReadAsync<InputReply>(secure,deadline.Token);
            var sequence=packet?.Sequence??preview?.Sequence;
            if(reply.Action!=action||reply.Session!=session||reply.Sequence<0||reply.Ok&&sequence is{} expected&&reply.Sequence!=expected)throw new InvalidDataException("The desktop reply did not match the request.");
            if(reply.Ok&&action is "begin" or "begin-preview" or "end")Notify(action=="end"?"released":"active");return reply;
        }
        catch{Close();throw;}finally{requests.Release();}
    }
    private void Close(){secure?.Dispose();secure=null;tcp?.Dispose();tcp=null;session="";Notify("disconnected");}
    /// <summary>Synchronously cancels pending operations and closes this exact TCP session. Does not wait for network cleanup or claim a peer release ACK.</summary>
    public void Abort()
    {
        try{lifetime.Cancel();}catch(ObjectDisposedException){}
        try{tcp?.Dispose();}catch(ObjectDisposedException){}
    }
    private void Notify(string message){try{StatusChanged?.Invoke(message);}catch{}}
    public async ValueTask DisposeAsync()
    {if(Interlocked.Exchange(ref disposed,1)!=0)return;lifetime.Cancel();tcp?.Dispose();await requests.WaitAsync();try{Close();}finally{requests.Release();lifetime.Dispose();}}
}
