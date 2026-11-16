using DesktopToolbox.DeskLink;
using Nian.Input.Runtime;

namespace DesktopToolbox.Windows.DeskSwitch;

public sealed partial class NativeIntegratedDeskSwitchService
{
    public async Task<IntegratedDeskSwitchStatus> StopAsync(CancellationToken token)
    {
        // Local input restoration is deliberately before waiting for any host gate or network operation.
        Volatile.Write(ref stopping,1);Interlocked.Exchange(ref pendingEmergencyReturn,null);StopPresence();arbiter?.StopAll("sharing-stopped");
        if(physical is{} capture)_ = capture.StopAsync("sharing-stopped",CancellationToken.None);
        running?.Cancel();
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try{await StopInsideAsync().ConfigureAwait(false);return Snapshot;}
        finally{lifecycle.Release();}
    }
    private async Task StopInsideAsync()
    {
        Volatile.Write(ref stopping,1);Interlocked.Exchange(ref pendingEmergencyReturn,null);StopPresence();running?.Cancel();
        if(IsRunning)Volatile.Write(ref processState,(int)DeskLinkProcessState.Stopping);
        await switchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if(settings is{} names&&CurrentEndpoint() is not null&&Volatile.Read(ref displayIntent).Computer==names.RemoteName)
            {
                using var recovery=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var returned=await SwitchInsideAsync("local",recovery.Token).ConfigureAwait(false);
                    // Keep authenticated, read-only state alive for the Mac's display-intent observer after a Windows Stop.
                    if(returned.InputTransferred&&input is not null)await Task.Delay(TimeSpan.FromSeconds(3),CancellationToken.None).ConfigureAwait(false);
                }
                catch(Exception error){lastError="本机键鼠已恢复，停止前显示回切未确认："+error.Message;}
            }
        }
        finally{switchGate.Release();}
        await ReleaseAsync().ConfigureAwait(false);Publish();
    }
    private async Task ReleaseAsync()
    {
        Volatile.Write(ref stopping,1);Interlocked.Exchange(ref pendingEmergencyReturn,null);var owner=running;owner?.Cancel();
        await StopPresenceAsync().ConfigureAwait(false);
        arbiter?.StopAll("host-disposed");
        NativeInputConnection[] transports;lock(channelsGate)transports=channels.ToArray();
        foreach(var connection in transports)connection.Abort("host-disposed");
        try{if(physical is{} capture)await capture.DisposeAsync().ConfigureAwait(false);}catch(Exception error){lastError=error.Message;}
        physical=null;physicalTransport=null;
        foreach(var connection in transports)try{await RemoveTransportAsync(connection).ConfigureAwait(false);}catch(Exception error){lastError=error.Message;}
        // Cancellation precedes server disposal: pending local confirmation and switch callbacks cannot wait on the host lifecycle gate.
        try{if(input is{} receiver)await receiver.DisposeAsync().ConfigureAwait(false);}catch(Exception error){lastError=error.Message;}finally{input=null;}
        try{if(bootstrap is{} data)await data.DisposeAsync().ConfigureAwait(false);}catch(Exception error){lastError=error.Message;}finally{bootstrap=null;}
        frames?.Invalidate();frames=null;receiverTarget=null;nativeTarget?.Dispose();nativeTarget=null;
        trust?.Dispose();trust=null;identity?.Dispose();identity=null;arbiter?.Dispose();arbiter=null;
        if(ReferenceEquals(running,owner))running=null;owner?.Dispose();address=null;approvedForCommit=null;
        Volatile.Write(ref processState,(int)DeskLinkProcessState.Stopped);
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        Volatile.Write(ref stopping,1);StopPresence();arbiter?.StopAll("disposed");running?.Cancel();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try{await StopInsideAsync().ConfigureAwait(false);}finally{lifecycle.Release();}
    }
}
