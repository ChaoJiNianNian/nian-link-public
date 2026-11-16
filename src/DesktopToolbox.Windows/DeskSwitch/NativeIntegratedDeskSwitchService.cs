using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DesktopToolbox.Core;
using DesktopToolbox.DeskLink;
using Nian.Input.Control;
using Nian.Input.Runtime;
using Nian.Input.Security;
using Nian.Input.Windows;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>Native input host. Legacy TLS credentials serve data only; device input uses a separate private identity and trust store.</summary>
public sealed partial class NativeIntegratedDeskSwitchService : IIntegratedDeskSwitchService,INativePreviewControl
{
    private sealed record DisplayIntentState(long Revision,string? Computer);
    private sealed record PendingEmergencyReturn(WindowsKeyboardControlSession Source,CancellationTokenSource Owner,DisplayIntentState ExpectedIntent);
    private const DevicePermission Both=DevicePermission.Control|DevicePermission.PreviewControl;
    private readonly ModuleContext context;
    private readonly IDisplayBridge bridge;
    private readonly Func<PairingApproval,CancellationToken,Task<bool>> confirm;
    private readonly Func<bool> guardHealthy;
    private readonly NativeIntegratedDeskSwitchOptions options;
    private readonly string root,nativeRoot;
    private readonly JsonFileStore peerFiles;
    private readonly SemaphoreSlim lifecycle=new(1,1),switchGate=new(1,1);
    private readonly object channelsGate=new();
    private readonly HashSet<NativeInputConnection> channels=[];
    private CancellationTokenSource? running;
    private DesktopControlArbiter? arbiter;
    private DeviceIdentity? identity;
    private DeviceTrustStore? trust;
    private WindowsDesktopInputTarget? nativeTarget;
    private ReceiverTarget? receiverTarget;
    private PreviewFrameAuthority? frames;
    private DesktopInputServer? input;
    private PairedControlServer? bootstrap;
    private WindowsKeyboardControlSession? physical;
    private NativeInputConnection? physicalTransport;
    private SavedNativePeer? approvedPeer;
    private PairingApproval? approvedForCommit;
    private DeskSwitchSettings? settings;
    private IPAddress? address;
    private DeskLinkSwitchResult? lastSwitch;
    private string? lastError,pendingFingerprint;
    private DisplayIntentState displayIntent=new(0,null);
    private PendingEmergencyReturn? pendingEmergencyReturn;
    private long inputRevision;
    private int processState,disposed,stopping;

    public NativeIntegratedDeskSwitchService(ModuleContext context,IDisplayBridge bridge,
        Func<PairingApproval,CancellationToken,Task<bool>>? confirmPairing=null,Func<bool>? guardHealthy=null,NativeIntegratedDeskSwitchOptions? options=null)
    {
        ArgumentNullException.ThrowIfNull(context);ArgumentNullException.ThrowIfNull(bridge);
        this.context=context;this.bridge=bridge;confirm=confirmPairing??((_,_)=>Task.FromResult(false));this.guardHealthy=guardHealthy??(()=>false);
        this.options=options??new();
        if(this.options.InputPort is <0 or >65535||this.options.BootstrapPort is <0 or >65535||this.options.DataPort is <1 or >65535)throw new ArgumentException("Invalid native candidate ports.");
        root=context.Paths.Resolve("desk-link");nativeRoot=Path.Combine(root,"native-input");peerFiles=new(new AppPaths(nativeRoot));
    }
    public bool IsRunning=>(DeskLinkProcessState)Volatile.Read(ref processState) is DeskLinkProcessState.Starting or DeskLinkProcessState.Running or DeskLinkProcessState.Stopping;
    public IntegratedDeskSwitchStatus Snapshot=>new()
    {
        Backend=DeskSwitchBackend.Native,Available=true,Session=BuildDeskState(),LastSwitch=Volatile.Read(ref lastSwitch),LastError=Volatile.Read(ref lastError),
        ControlAddress=address?.ToString(),ControlPort=bootstrap?.Port,ControlFingerprint=bootstrap?.CertificateSha256
    };
    private bool Healthy(){try{return guardHealthy();}catch{return false;}}
    private TrustedEndpoint? CurrentEndpoint()=>Volatile.Read(ref approvedPeer) is{LocalBidirectionalApproved:true} saved?saved.Endpoint:null;
    private bool Allows(string peer,DevicePermission permission)=>CurrentEndpoint() is{} endpoint&&string.Equals(endpoint.Fingerprint,peer,StringComparison.OrdinalIgnoreCase)&&
        Volatile.Read(ref approvedPeer) is{} saved&&(saved.Permissions&permission)==permission&&trust?.Allows(peer,permission)==true;
    private bool SameRun(CancellationTokenSource owner)=>ReferenceEquals(Volatile.Read(ref running),owner)&&!owner.IsCancellationRequested;
    private DeskLinkState BuildDeskState(DisplayIntentState? intent=null)
    {
        intent??=Volatile.Read(ref displayIntent);
        var names=settings??new();var endpoint=CurrentEndpoint();var observed=ReadPeerObservation(endpoint);
        var connected=observed is not null&&endpoint is not null&&Allows(endpoint.Fingerprint,DevicePermission.Control);
        return new(){ProcessState=(DeskLinkProcessState)Volatile.Read(ref processState),ConnectionState=connected?DeskLinkConnectionState.Connected:IsRunning?DeskLinkConnectionState.Listening:DeskLinkConnectionState.Disconnected,
            ActiveComputer=physical?.IsRunning==true?names.RemoteName:names.LocalName,ConnectedComputers=connected?[names.RemoteName]:[],LocalFingerprint=identity?.Fingerprint,
            PendingPeerFingerprint=Volatile.Read(ref pendingFingerprint),LastError=Volatile.Read(ref lastError),NativeControlAvailable=IsRunning&&Healthy()&&connected&&observed!.Available,
            Revision=intent.Revision,ProcessId=null};
    }
    private DesktopControlState BuildControlState()
    {
        // The target and its revision are one published value, never two independent reads.
        var intent=Volatile.Read(ref displayIntent);var value=BuildDeskState(intent);var names=settings??new();
        // A keyboard lease remains active until StopLocal finishes native restoration and releases its reservation.
        var keyboardOwned=arbiter?.Current is{Mode:DesktopControlMode.Keyboard};
        // This authenticated RPC describes local capability, including the read-only stopping window used by the Mac display observer.
        // UI peer presence is separate: stopping immediately clears its online observation without discarding a confirmed return intent.
        var available=IsRunning&&Healthy()&&CurrentEndpoint() is{} endpoint&&Allows(endpoint.Fingerprint,DevicePermission.Control);
        return new(available,names.LocalName,names.RemoteName,value.ActiveComputer,receiverTarget?.Active==true||physical?.IsRunning==true||keyboardOwned,
            Interlocked.Read(ref inputRevision),intent.Revision,intent.Computer,value.LastError);
    }
    public async Task<IntegratedDeskSwitchStatus> StartAsync(DeskSwitchSettings next,CancellationToken token)
    {
        next.Validate();await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);if(IsRunning)return Snapshot;
            settings=next;Interlocked.Exchange(ref pendingEmergencyReturn,null);
            Volatile.Write(ref displayIntent,new(Volatile.Read(ref displayIntent).Revision,next.LocalName));
            lastError=null;lastSwitch=null;Volatile.Write(ref stopping,0);Volatile.Write(ref processState,(int)DeskLinkProcessState.Starting);
            var owner=new CancellationTokenSource();running=owner;address=options.BindAddress??FindLanAddress();arbiter=new();
            var vault=options.IdentityVault??new WindowsIdentityVault(nativeRoot);
            identity=await DeviceIdentity.OpenAsync(nativeRoot,next.LocalName,vault,token).ConfigureAwait(false);
            trust=await DeviceTrustStore.OpenAsync(nativeRoot,token).ConfigureAwait(false);
            approvedPeer=await peerFiles.LoadAsync<SavedNativePeer?>("approved-peer.json",()=>null,token).ConfigureAwait(false);
            if(approvedPeer is{} saved&&!ValidSavedPeer(saved))throw new InvalidDataException("Native input approval is invalid; the original file was retained.");
            nativeTarget=new();receiverTarget=new(nativeTarget,()=>Interlocked.Increment(ref inputRevision));
            frames=new(()=>options.ReadSource is{} read?read():nativeTarget?.ReadPreviewGeometry(1,1));
            var serverOptions=new DesktopInputServerOptions{Arbiter=arbiter,PreviewFrames=frames,PrepareInput=PrepareReceiver,RenewInput=RenewReceiver,
                Paired=paired=>CommitPairing(owner,paired),ReadStateAsync=(peer,ct)=>ReadStateForPeerAsync(owner,peer,ct),FullSwitchAsync=(peer,request,ct)=>SwitchForPeerAsync(owner,peer,request,ct)};
            input=new(identity,trust,address,options.InputPort,receiverTarget,(approval,ct)=>ConfirmPairingAsync(owner,approval,ct),options:serverOptions);
            await input.StartAsync(token).ConfigureAwait(false);
            bootstrap=new(new(){ProfileDirectory=Path.Combine(root,"control"),BindAddress=address,Port=options.BootstrapPort,AllowedTargets=[next.LocalName,next.RemoteName]},
                _=>Task.FromResult(BuildDeskState()),(target,_)=>Task.FromResult(new DeskLinkSwitchResult{Target=target,ActiveComputer=BuildDeskState().ActiveComputer,Error="旧数据配对只提供协同数据；请使用新设备控制授权。"}));
            await bootstrap.StartAsync(token).ConfigureAwait(false);
            Volatile.Write(ref processState,(int)DeskLinkProcessState.Running);StartPresence(owner);Publish();return Snapshot;
        }
        catch{await ReleaseAsync().ConfigureAwait(false);throw;}
        finally{lifecycle.Release();}
    }
    private bool PrepareReceiver(string peer,DesktopControlMode mode)
    {
        if(mode!=DesktopControlMode.Preview||Volatile.Read(ref stopping)!=0||!Healthy()||!Allows(peer,DevicePermission.PreviewControl)||receiverTarget is null)return false;
        Interlocked.Exchange(ref pendingEmergencyReturn,null);
        return receiverTarget.Prepare();
    }
    private bool RenewReceiver(string peer,DesktopControlMode mode,long consent)=>mode==DesktopControlMode.Preview&&Volatile.Read(ref stopping)==0&&Healthy()&&
        Allows(peer,DevicePermission.PreviewControl)&&receiverTarget?.Renew(consent)==true;
    public async Task<IntegratedDeskSwitchStatus> RefreshAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(disposed!=0,this);
        await RefreshPresenceAsync(token).ConfigureAwait(false);return Snapshot;
    }
    private void Publish()=>ThreadPool.QueueUserWorkItem(_=>{try{context.Emit("deskSwitch.sessionChanged",Snapshot);}catch{}});
    private static IPAddress FindLanAddress()=>NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(n=>n.Address).FirstOrDefault(ip=>ip.AddressFamily==AddressFamily.InterNetwork&&IsPrivate(ip))??throw new InvalidOperationException("请连接可供 Mac 使用的同一局域网。");
    private static bool IsPrivate(IPAddress ip){var b=ip.GetAddressBytes();return b[0]==10||b[0]==192&&b[1]==168||b[0]==172&&b[1] is >=16 and <=31;}
}
