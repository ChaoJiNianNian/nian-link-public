using DesktopToolbox.DeskLink;
using Nian.Input.Control;
using Nian.Input.Security;
using Nian.Input.Windows;
using System.Diagnostics;

namespace DesktopToolbox.Windows.DeskSwitch;

public sealed partial class NativeIntegratedDeskSwitchService
{
    private Task<DesktopControlState> ReadStateForPeerAsync(CancellationTokenSource owner,string peer,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!ReferenceEquals(Volatile.Read(ref running),owner)||!Allows(peer,DevicePermission.Control))throw new UnauthorizedAccessException("Only the current locally approved peer can read native control state.");
        return Task.FromResult(BuildControlState());
    }
    private async Task<DesktopSwitchResult> SwitchForPeerAsync(CancellationTokenSource owner,string peer,DesktopFullSwitchRequest request,CancellationToken token)
    {
        if(!SameRun(owner)||!Allows(peer,DevicePermission.Control))throw new UnauthorizedAccessException("Only the current locally approved peer can request a full switch.");
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,owner.Token);
        await switchGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if(!SameRun(owner)||!Allows(peer,DevicePermission.Control))throw new UnauthorizedAccessException("The current paired device changed.");
            if(request.ExpectedDisplayIntentRevision!=Volatile.Read(ref displayIntent).Revision)
                return new(false,BuildControlState(),false,"notAttempted","显示意图已变化，请刷新后重新操作。");
            var target=request.Target==DesktopSwitchTarget.Local?"local":"remote";
            var result=await SwitchInsideAsync(target,linked.Token).ConfigureAwait(false);
            return new(result.Ok,BuildControlState(),result.InputTransferred,result.VideoStatus,result.Error);
        }
        catch(OperationCanceledException){await StopLocalInputsAsync("full-switch-canceled").ConfigureAwait(false);throw;}
        finally{switchGate.Release();}
    }
    public async Task<DeskLinkSwitchResult> SwitchAsync(string target,CancellationToken token)
    {
        if(target is not("local" or "remote"))throw new ArgumentException("只能切到 Windows 或 Mac。",nameof(target));
        ObjectDisposedException.ThrowIf(disposed!=0,this);
        var owner=running??throw new InvalidOperationException("请先开始原生共享。");
        if(!SameRun(owner)||Volatile.Read(ref stopping)!=0)throw new InvalidOperationException("共享正在停止。");
        Interlocked.Exchange(ref pendingEmergencyReturn,null);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,owner.Token);
        await switchGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try{if(!SameRun(owner)||Volatile.Read(ref stopping)!=0)throw new InvalidOperationException("共享正在停止。");return await SwitchInsideAsync(target,linked.Token).ConfigureAwait(false);}
        catch(OperationCanceledException){await StopLocalInputsAsync("full-switch-canceled").ConfigureAwait(false);throw;}
        finally{switchGate.Release();}
    }
    private async Task<DeskLinkSwitchResult> SwitchInsideAsync(string target,CancellationToken token,PendingEmergencyReturn? emergency=null)
    {
        var names=settings??throw new InvalidOperationException("请先开始原生共享。");var endpoint=CurrentEndpoint();
        var computer=target=="local"?names.LocalName:names.RemoteName;
        var nextIntent=new DisplayIntentState(checked(Volatile.Read(ref displayIntent).Revision+1),computer);
        DeskLinkSwitchResult Failed(string message)=>PublishSwitch(new(){Target=computer,ActiveComputer=BuildDeskState().ActiveComputer,Error=message});
        DeskLinkSwitchResult Superseded()=>new(){Target=computer,ActiveComputer=BuildDeskState().ActiveComputer};
        if(target=="local")
        {
            if(emergency is null)Interlocked.Exchange(ref pendingEmergencyReturn,null);
            else if(!IsCurrentEmergencyReturn(emergency))return Superseded();
            // StopAsync restores physical input synchronously before its first await. Preview never supplies this display intent.
            var stopped=(emergency?.Source??physical)?.StopAsync("explicit-local-return",token)??Task.CompletedTask;
            // A physical emergency has already released its exact keyboard lease. It must never stop a newer preview owner.
            if(emergency is null)arbiter?.StopAll("explicit-local-return");
            await stopped.ConfigureAwait(false);
            if(emergency is not null&&!IsCurrentEmergencyReturn(emergency))return Superseded();
            if(endpoint is null||!Allows(endpoint.Fingerprint,DevicePermission.Control))return Failed("本机键鼠已恢复；尚无当前 Mac 的设备控制确认，未切换显示器。");
            if(!await ConfirmRemoteInputReleasedAsync(endpoint,token).ConfigureAwait(false))return Failed("本机键鼠已恢复；Mac 尚未确认输入已释放，未提交显示回切。");
        }
        else
        {
            if(!Healthy()||endpoint is null||!Allows(endpoint.Fingerprint,DevicePermission.Control))return Failed("请先在两端完成设备控制授权并确认本机安全保护正常。");
            Interlocked.Exchange(ref pendingEmergencyReturn,null);
            if(options.CaptureFactory is null)EnsureNoLegacyCapture();
            if(physical?.IsRunning!=true)
            {
                arbiter?.StopAll("explicit-keyboard-switch");
                if(physical is{} previous)await previous.DisposeAsync().ConfigureAwait(false);
                if(physicalTransport is{} previousTransport)await RemoveTransportAsync(previousTransport).ConfigureAwait(false);
                physicalTransport=CreateTransport(DesktopControlMode.Keyboard);
                var created=new WindowsKeyboardControlSession(physicalTransport,arbiter!,endpoint.Fingerprint,options.CaptureFactory);
                physical=created;
                created.LocalReturnRequested+=reason=>
                {
                    var pending=Volatile.Read(ref pendingEmergencyReturn);
                    if(pending is not null&&ReferenceEquals(pending.Source,created))_ = ReturnAfterEmergencyAsync(pending);
                };
            }
            // Publish the action's expected committed intent before capture can emit its emergency callback.
            // Repeated remote actions reuse a physical source but receive a fresh context.
            var pendingReturn=new PendingEmergencyReturn(physical!,Volatile.Read(ref running)??throw new InvalidOperationException("共享已经停止。"),nextIntent);
            Volatile.Write(ref pendingEmergencyReturn,pendingReturn);
            try{await physical!.StartAsync(token).ConfigureAwait(false);}
            catch(Exception error)when(error is InvalidOperationException or IOException or System.Security.Authentication.AuthenticationException or TimeoutException)
            {Interlocked.CompareExchange(ref pendingEmergencyReturn,null,pendingReturn);return Failed("Mac 未确认键鼠交接，未切换显示器："+error.Message);}
            if(!physical.IsRunning||CurrentEndpoint()!=endpoint||!Allows(endpoint.Fingerprint,DevicePermission.Control))
            {await StopLocalInputsAsync("paired-device-changed").ConfigureAwait(false);return Failed("键鼠目标或设备权限已经变化，未切换显示器。");}
        }
        token.ThrowIfCancellationRequested();
        // Incoming preview preparation can supersede this callback while its authenticated release RPC awaits.
        // The CAS is this emergency's commit boundary; later preview actions never roll back an already committed display intent.
        if(emergency is not null&&(!IsCurrentEmergencyReturn(emergency)||
            !ReferenceEquals(Interlocked.CompareExchange(ref pendingEmergencyReturn,null,emergency),emergency)))return Superseded();
        // Explicit full switch alone advances display intent. The input mode arbiter and preview lifecycle never write these fields.
        Interlocked.Increment(ref inputRevision);
        Volatile.Write(ref displayIntent,nextIntent);
        if(!names.LinkDisplay)return PublishSwitch(new(){Ok=true,Target=computer,ActiveComputer=computer,InputTransferred=true,VideoStatus="disabled"});
        try
        {
            var result=await new UnifiedDisplayAdapter(bridge,context.Paths.Resolve("desk-switch.json"),names).ApplyAsync(computer,token).ConfigureAwait(false);
            return PublishSwitch(new(){Ok=result.Status=="verified",Target=computer,ActiveComputer=computer,InputTransferred=true,VideoStatus=result.Status,Error=result.Error});
        }
        catch(OperationCanceledException){throw;}
        catch(Exception error)when(error is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {return PublishSwitch(new(){Target=computer,ActiveComputer=computer,InputTransferred=true,VideoStatus="failed",Error=error.Message});}
    }
    private async Task<bool> ConfirmRemoteInputReleasedAsync(TrustedEndpoint endpoint,CancellationToken token)
    {
        for(var attempt=0;attempt<3;attempt++)
        {
            token.ThrowIfCancellationRequested();if(CurrentEndpoint()!=endpoint||!Allows(endpoint.Fingerprint,DevicePermission.Control))return false;
            try
            {
                await using var client=new DesktopInputClient(identity!,endpoint);await client.ConnectAsync(token).ConfigureAwait(false);
                var state=await client.GetStateAsync(token).ConfigureAwait(false);
                if(state.LocalName!=settings!.LocalName||state.RemoteName!=settings.RemoteName)return false;
                if(!state.InputActive)return true;
            }
            catch(Exception error)when(error is IOException or InvalidOperationException or System.Security.Authentication.AuthenticationException or TimeoutException or System.Net.Sockets.SocketException){return false;}
            if(attempt<2)await Task.Delay(100,token).ConfigureAwait(false);
        }
        return false;
    }
    private DeskLinkSwitchResult PublishSwitch(DeskLinkSwitchResult result){Volatile.Write(ref lastSwitch,result);lastError=result.Error;Publish();return result;}
    private async Task StopLocalInputsAsync(string reason)
    {
        Interlocked.Exchange(ref pendingEmergencyReturn,null);
        var stopped=physical?.StopAsync(reason,CancellationToken.None)??Task.CompletedTask;
        arbiter?.StopAll(reason);await stopped.ConfigureAwait(false);
    }
    private static void EnsureNoLegacyCapture()
    {
        foreach(var name in new[]{"deskflow","deskflow-core","deskflow-integrated","DeskflowIntegrated"})
        foreach(var process in Process.GetProcessesByName(name))
        {
            using(process)if(!process.HasExited)throw new InvalidOperationException("请先在现用共享软件中正常停止共享并退出旧核心，再进行自研键鼠实机验收；工具箱不会自动停止它。");
        }
    }
    private bool IsCurrentEmergencyReturn(PendingEmergencyReturn pending)=>SameRun(pending.Owner)&&Volatile.Read(ref stopping)==0&&
        ReferenceEquals(Volatile.Read(ref physical),pending.Source)&&ReferenceEquals(Volatile.Read(ref pendingEmergencyReturn),pending)&&
        ReferenceEquals(Volatile.Read(ref displayIntent),pending.ExpectedIntent)&&pending.ExpectedIntent.Computer==settings?.RemoteName;
    private async Task ReturnAfterEmergencyAsync(PendingEmergencyReturn pending)
    {
        var owner=pending.Owner;
        try
        {
            if(!SameRun(owner)||Volatile.Read(ref stopping)!=0||!ReferenceEquals(Volatile.Read(ref physical),pending.Source)||
                !ReferenceEquals(Volatile.Read(ref pendingEmergencyReturn),pending))return;
            await switchGate.WaitAsync(owner.Token).ConfigureAwait(false);
            try
            {
                // A queued native notification may outlive its capture, host run, or a newer explicit switch.
                // Only the still-current remote intent can be consumed; native restoration already happened.
                if(!IsCurrentEmergencyReturn(pending))return;
                await SwitchInsideAsync("local",owner.Token,pending).ConfigureAwait(false);
            }
            finally{switchGate.Release();}
        }
        catch(OperationCanceledException){}catch(ObjectDisposedException){}
        catch(Exception error){if(IsCurrentEmergencyReturn(pending)){lastError=error.Message;Publish();}}
    }
}
