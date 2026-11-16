using System.Net;
using System.Security.Authentication;
using System.Text;
using DesktopToolbox.DeskLink;
using Nian.Input.Security;

namespace DesktopToolbox.Windows.DeskSwitch;

public sealed partial class NativeIntegratedDeskSwitchService
{
    private bool ValidSavedPeer(SavedNativePeer value)=>value.Version==1&&value.LocalBidirectionalApproved&&value.Permissions==Both&&
        value.Endpoint is{} endpoint&&IPAddress.TryParse(endpoint.Host,out var ip)&&!ip.Equals(IPAddress.Any)&&!ip.Equals(IPAddress.IPv6Any)&&
        endpoint.Port is >0 and <=65535&&trust?.Allows(endpoint.Fingerprint,Both)==true;
    private async Task<bool> ConfirmPairingAsync(CancellationTokenSource owner,PairingApproval approval,CancellationToken token)
    {
        if(!SameRun(owner)||approval.RequestedPermission!=Both||approval.ReceiverPort is not (>0 and <=65535)||!IPAddress.TryParse(approval.SourceAddress,out _))return false;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(owner.Token,token);
        Volatile.Write(ref pendingFingerprint,approval.Fingerprint);Publish();
        try
        {
            var accepted=await confirm(approval,linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();if(!accepted||!SameRun(owner))return false;
            Volatile.Write(ref approvedForCommit,approval);return true;
        }
        finally{Volatile.Write(ref pendingFingerprint,null);Publish();}
    }
    private void CommitPairing(CancellationTokenSource owner,PairedDevice paired)
    {
        var local=Interlocked.Exchange(ref approvedForCommit,null);
        if(!SameRun(owner)||local is null||local.Fingerprint!=paired.Fingerprint||local.SourceAddress!=paired.SourceAddress||local.ReceiverPort!=paired.ReceiverPort||
            paired.GrantedPermissions!=Both||paired.ReceiverEndpoint is not{} endpoint)throw new AuthenticationException("The exact bidirectional local approval is required.");
        switchGate.Wait(owner.Token);
        try
        {
            owner.Token.ThrowIfCancellationRequested();
            // Pairing replacement cannot leave a capture or a preview owned by the old peer.
            Interlocked.Exchange(ref pendingEmergencyReturn,null);
            arbiter?.StopAll("paired-device-changed");
            if(physical is{} previous)previous.StopAsync("paired-device-changed",owner.Token).GetAwaiter().GetResult();
            var saved=new SavedNativePeer(1,endpoint,Both,true);
            peerFiles.SaveAsync("approved-peer.json",saved,owner.Token).GetAwaiter().GetResult();
            Volatile.Write(ref approvedPeer,saved);ResetPresence(owner);lastError=null;Publish();
        }
        finally{switchGate.Release();}
    }
    public async Task<string> ExportPairingAsync(CancellationToken token)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed!=0,this);
            if(input is null||bootstrap is null||address is null||settings is null||!IsRunning)throw new InvalidOperationException("请先开始原生共享。");
            var data=bootstrap.CreatePairingBundle(address.ToString(),settings.LocalName,settings.RemoteName,DeskLinkDisplayBindingFactory.FromSettings(settings));
            var bundle=data with{Version=2,InputInvitation=input.CreateInvitation(Both).ToCode(),DataPort=options.DataPort};
            var path=Path.Combine(nativeRoot,"Mac配对.json");DeskLinkRuntimePackage.EnsureOrdinaryPath(path);
            var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{await File.WriteAllTextAsync(temporary,bundle.ToJson(),new UTF8Encoding(false),token).ConfigureAwait(false);File.Move(temporary,path,true);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            return path;
        }
        finally{lifecycle.Release();}
    }
    public Task ConfirmPeerAsync(string fingerprint,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(disposed!=0,this);
        if(!Allows(fingerprint,Both))throw new InvalidOperationException("请在 Windows 本机的完整指纹确认窗口批准本次配对。");
        return Task.CompletedTask;
    }
    public Task RejectPeerAsync(CancellationToken token)=>StopAsync(token);
}
