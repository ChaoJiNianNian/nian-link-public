using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Threading;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Views;
using Nian.Input;
using Nian.Input.Control;
using Nian.Input.Mac;
using Nian.Input.Runtime;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Services;

/// <summary>Borrowed Deskflow observations plus a separate, preview-only identity and receiver.</summary>
public sealed class MacPreviewControlRuntime : INativePreviewControl, IMacPreviewControlLifecycle
{
    private readonly string directory;
    private readonly int receiverPort;
    private readonly Func<CancellationToken,Task<MacLinkSnapshot>> readLegacy;
    private readonly MacDesktopLocalEventGuard guard;
    private readonly MacDesktopInputTarget target;
    private readonly MacPreviewAdmissionObserver admission=new();
    private readonly CancellationTokenSource lifetime=new();
    private readonly SemaphoreSlim initialization=new(1,1);
    private readonly ITimer safety;
    private PreviewOnlyDeviceService? device;
    private MacPreviewPairingWindow? pairing;
    private Window? owner;
    private string? preparationError;
    private int disposed,polling;

    public MacPreviewControlRuntime(string directory,Func<CancellationToken,Task<MacLinkSnapshot>> readLegacy,int receiverPort=24942)
    {
        Dispatcher.UIThread.VerifyAccess();
        if(!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("预览操作接收端只能在 Mac 运行。");
        if(!Path.IsPathFullyQualified(directory)||receiverPort is <1 or >65535)throw new ArgumentException("预览操作配置无效。");
        ArgumentNullException.ThrowIfNull(readLegacy);
        this.directory=directory;this.readLegacy=readLegacy;this.receiverPort=receiverPort;
        var ownPolicy=new MacDesktopOwnUiPolicy(()=>true,_=>true,
            (bundle,pid)=>NativeSurfaceSnapshot.ExcludesPreviewOwner(bundle,pid,Environment.ProcessId));
        guard=MacDesktopLocalEventGuard.Install(ownPolicy);
        try{target=new MacDesktopInputTarget(ownPolicy);}
        catch{guard.Dispose();throw;}
        safety=TimeProvider.System.CreateTimer(_=>CheckSafety(),null,TimeSpan.FromMilliseconds(250),TimeSpan.FromMilliseconds(250));
    }
    public event Action? Changed;
    public PreviewOnlyDeviceStatus Status=>Volatile.Read(ref device)?.Status??new(){Message="连接设备后可在预览中操作",Error=Volatile.Read(ref preparationError)};
    public bool PreviewControlReady=>ReadSendAdmission().Allowed;
    public string PreviewControlHint=>ReadSendAdmission().Reason??"预览操作已连接，默认仅观看";
    public void BindOwner(Window value){Dispatcher.UIThread.VerifyAccess();owner=value;}
    public Task InitializeSavedDeviceAsync(CancellationToken token=default)=>
        File.Exists(Path.Combine(directory,"preview-control","identity.json"))?InitializeAsync(token):Task.CompletedTask;

    public async Task InitializeAsync(CancellationToken token=default)
    {
        using var request=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        await initialization.WaitAsync(request.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
            if(Volatile.Read(ref device)?.Status.Started==true)return;
            var snapshot=await readLegacy(request.Token).ConfigureAwait(false);ObserveSnapshot(snapshot);
            if(device is null)
            {
                var service=new PreviewOnlyDeviceService(directory,snapshot.Settings.MacName,new MacKeychainIdentityVault(),target,new()
                {
                    BindAddress=FindLanAddress(snapshot.Settings.WindowsHost),ReceiverPort=receiverPort,
                    ReadCurrentGeometry=()=>MacDesktopSource.ReadPreviewGeometry(1,1),
                    Prepare=_=>PrepareTarget(),Renew=(_,consent)=>guard.IsHealthy&&target.RenewLease(consent,TimeSpan.FromSeconds(2)),
                    RequireLocalApproval=RequireLocalApproval,ConfirmIncomingPairingAsync=ConfirmIncomingPairingAsync,
                    CheckAccess=CheckAccess
                });
                service.Changed+=Notify;Volatile.Write(ref device,service);
            }
            await device.StartAsync(request.Token).ConfigureAwait(false);Volatile.Write(ref preparationError,null);
        }
        catch(Exception error)when(error is not OperationCanceledException)
        {Volatile.Write(ref preparationError,"预览操作尚未准备好，请检查局域网和 Mac 系统权限后重试");throw;}
        finally{initialization.Release();Notify();}
    }
    private bool PrepareTarget()
    {
        if(!guard.IsHealthy)return false;
        target.RefreshSafety();target.SetArmed(true,TimeSpan.FromSeconds(2));
        if(target.IsReady)return true;
        target.SetArmed(false);return false;
    }
    public void RequireLocalApproval()
    {
        Dispatcher.UIThread.VerifyAccess();
        if(Volatile.Read(ref disposed)!=0||!guard.IsHealthy||!MacDesktopLocalEventGuard.IsCurrentEventEligibleForLocalApproval())
            throw new InvalidOperationException("请在这台 Mac 上亲自点击确认，预览操作不能代为授权。");
    }
    public DeviceInvitation CreateInvitation()=>RequireDevice().CreateInvitation();
    public Task PairAsync(string code,CancellationToken token=default)=>RequireDevice().PairAsync(code,token);
    public Task RevokePeerAsync(CancellationToken token=default)=>RequireDevice().RevokePeerAsync(token);
    public void CancelInvitation()=>Volatile.Read(ref device)?.CancelInvitation();
    private PreviewOnlyDeviceService RequireDevice()=>Volatile.Read(ref device) is{Status.Started:true} ready?ready:throw new InvalidOperationException("请先准备预览操作连接。");

    public async Task ShowPairingAsync(Window value)
    {
        Dispatcher.UIThread.VerifyAccess();BindOwner(value);
        if(pairing is{IsVisible:true} existing){existing.Activate();return;}
        var dialog=new MacPreviewPairingWindow(this){RequestedThemeVariant=value.ActualThemeVariant};pairing=dialog;
        try{await dialog.ShowDialog(value);}
        finally{if(ReferenceEquals(pairing,dialog))pairing=null;}
    }
    private async Task<bool> ConfirmIncomingPairingAsync(PairingApproval approval,CancellationToken token)
    {
        return await Dispatcher.UIThread.InvokeAsync(async()=>
        {
            if(Volatile.Read(ref disposed)!=0||owner is null||token.IsCancellationRequested)return false;
            if(!owner.IsVisible)owner.Show();
            var dialogOwner=pairing is{IsVisible:true}?pairing:owner;
            return await MacPreviewIncomingApproval.ShowAsync(dialogOwner,approval,RequireLocalApproval,token);
        }).ConfigureAwait(false);
    }
    public void ObserveSnapshot(MacLinkSnapshot snapshot)
    {
        if(Volatile.Read(ref disposed)!=0)return;
        if(admission.Observe(snapshot,MacPreviewCoreProbe.HasOtherCore(snapshot.PreviewLocalCoreState.ProcessId)))
            StopActiveControl("日常键鼠状态已变化，预览操作已停止");
    }
    private PreviewAdmission ReadSendAdmission()
    {
        var current=Status;
        if(!current.Started)return new(false,current.Error??"请先连接预览操作设备");
        if(!current.Paired||current.PeerName is null||current.PeerFingerprint is not{} fingerprint)return new(false,"请先连接预览操作设备");
        return CheckAccess(PreviewControlDirection.Send,fingerprint);
    }
    private PreviewAdmission CheckAccess(PreviewControlDirection direction,string fingerprint)
    {
        var current=Status;
        if(!guard.IsHealthy||!current.Paired||current.PeerName is null||!string.Equals(current.PeerFingerprint,fingerprint,StringComparison.OrdinalIgnoreCase))
            return new(false,"此设备尚未允许预览操作");
        return admission.Check(direction==PreviewControlDirection.Send,current.PeerName);
    }
    private void CheckSafety()
    {
        if(Volatile.Read(ref disposed)!=0||Interlocked.Exchange(ref polling,1)!=0)return;
        try
        {
            var snapshot=admission.Snapshot;
            if(admission.RefreshLocalProbe(snapshot,MacPreviewCoreProbe.HasOtherCore(snapshot.PreviewLocalCoreState.ProcessId)))
                StopActiveControl("日常键鼠状态已变化，预览操作已停止");
            else if(Status.ControlActive&&(!guard.IsHealthy||(!admission.Check(true,Status.PeerName??"").Allowed&&!admission.Check(false,Status.PeerName??"").Allowed)))
                StopActiveControl("日常键鼠状态暂未确认，预览操作已停止");
        }
        catch{admission.Invalidate();StopActiveControl("无法确认日常键鼠状态，预览操作已停止");}
        finally{Volatile.Write(ref polling,0);}
    }
    public void StopControl(string reason){admission.Invalidate();StopActiveControl(reason);}
    private void StopActiveControl(string reason){Volatile.Read(ref device)?.StopControl(reason);Notify();}
    public PreviewFrameProof? BindCapturedFrame(byte[] jpeg,PreviewSourceGeometry? geometry)=>Volatile.Read(ref device)?.BindCapturedFrame(jpeg,geometry);
    public void InvalidateCapturedFrames()=>Volatile.Read(ref device)?.InvalidateCapturedFrames();
    public IPreviewControlChannel CreatePreviewChannel()=>new LazyChannel(this);
    private sealed class LazyChannel(MacPreviewControlRuntime owner):IPreviewControlChannel,IAsyncDisposable
    {
        private IPreviewControlChannel? channel;
        public async Task BeginAsync(CancellationToken token)
        {channel??=owner.RequireDevice().CreatePreviewChannel();await channel.BeginAsync(token).ConfigureAwait(false);}
        public Task SendAsync(PreviewInputCommand value,CancellationToken token)=>(channel??throw new InvalidOperationException("预览操作尚未开启")).SendAsync(value,token);
        public Task HeartbeatAsync(CancellationToken token)=>(channel??throw new InvalidOperationException("预览操作尚未开启")).HeartbeatAsync(token);
        public Task EndAsync(CancellationToken token)=>channel?.EndAsync(token)??Task.CompletedTask;
        public async ValueTask DisposeAsync(){if(channel is IAsyncDisposable disposable)await disposable.DisposeAsync();}
    }
    private void Notify()
    {
        if(Volatile.Read(ref disposed)!=0)return;
        if(Changed is{} handlers)foreach(Action handler in handlers.GetInvocationList())try{handler();}catch{}
    }
    private static IPAddress FindLanAddress(string peer)
    {
        if(IPAddress.TryParse(peer,out var remote))
        {
            try{using var route=new Socket(remote.AddressFamily,SocketType.Dgram,ProtocolType.Udp);route.Connect(new IPEndPoint(remote,9));if(route.LocalEndPoint is IPEndPoint endpoint&&!IPAddress.IsLoopback(endpoint.Address))return endpoint.Address;}
            catch(SocketException){}
        }
        var candidate=NetworkInterface.GetAllNetworkInterfaces().Where(item=>item.OperationalStatus==OperationalStatus.Up&&item.NetworkInterfaceType is not(NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(item=>item.GetIPProperties().UnicastAddresses).Select(item=>item.Address).FirstOrDefault(address=>address.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(address)&&!address.ToString().StartsWith("169.254.",StringComparison.Ordinal));
        return candidate??throw new IOException("未找到可用的局域网地址。");
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        lifetime.Cancel();await safety.DisposeAsync().ConfigureAwait(false);
        await initialization.WaitAsync().ConfigureAwait(false);
        try
        {
            try{if(device is{} service){service.Changed-=Notify;await service.DisposeAsync().ConfigureAwait(false);}}
            finally{await Dispatcher.UIThread.InvokeAsync(()=>{try{target.Dispose();}finally{guard.Dispose();}});}
        }
        finally{initialization.Release();lifetime.Dispose();}
    }
}
