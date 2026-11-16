using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Services;

/// <summary>Preview control observes, but never replaces, the existing Deskflow lifecycle.</summary>
public interface IMacPreviewControlLifecycle : IAsyncDisposable
{
    void ObserveSnapshot(MacLinkSnapshot snapshot);
    void StopControl(string reason);
}

public sealed class MacPreviewAwareLinkService : IMacLinkService
{
    private readonly IMacLinkService legacy;
    private readonly IMacPreviewControlLifecycle preview;
    private readonly Func<CancellationToken,Task<DeskLinkPairingBundle?>> getPairing;
    private int disposed;
    private long latestSnapshotVersion;
    public MacPreviewAwareLinkService(IMacLinkService legacy, IMacPreviewControlLifecycle preview,
        Func<CancellationToken,Task<DeskLinkPairingBundle?>> getPairing)
    {
        ArgumentNullException.ThrowIfNull(legacy);ArgumentNullException.ThrowIfNull(preview);ArgumentNullException.ThrowIfNull(getPairing);
        this.legacy=legacy;this.preview=preview;this.getPairing=getPairing;
        legacy.Changed+=OnChanged;
    }
    public event Action<MacLinkSnapshot>? Changed;
    private void OnChanged(MacLinkSnapshot value)
    {
        var version=value.LocalSnapshotVersion;
        while(true)
        {
            var latest=Volatile.Read(ref latestSnapshotVersion);
            if(latest>0 && (version==0 || version<latest))return;
            if(version==0 || version==latest || Interlocked.CompareExchange(ref latestSnapshotVersion,version,latest)==latest)break;
        }
        preview.ObserveSnapshot(value);
        var committed=Volatile.Read(ref latestSnapshotVersion);
        if(committed>0 && (version==0 || version<committed))return;
        Changed?.Invoke(value);
    }
    public Task<DeskLinkPairingBundle?> GetApprovedPairingAsync(CancellationToken token=default)=>getPairing(token);
    public Task<MacLinkSnapshot> RefreshAsync(CancellationToken token=default)=>legacy.RefreshAsync(token);
    public Task StartAsync(MacConnectionSettings settings,CancellationToken token=default)
    {preview.StopControl("日常键鼠正在重新连接，预览操作已停止");return legacy.StartAsync(settings,token);}
    public Task StopAsync(CancellationToken token=default)
    {preview.StopControl("日常键鼠正在停止，预览操作已停止");return legacy.StopAsync(token);}
    public Task ConfirmPeerAsync(string fingerprint,bool accept,CancellationToken token=default)=>legacy.ConfirmPeerAsync(fingerprint,accept,token);
    public Task<PairingPreview> PreviewPairingAsync(string json,CancellationToken token=default)=>legacy.PreviewPairingAsync(json,token);
    public Task AcceptPairingAsync(string id,CancellationToken token=default)
    {preview.StopControl("数据连接已更新，请重新开启预览操作");return legacy.AcceptPairingAsync(id,token);}
    public Task<MacSwitchOutcome> SwitchAsync(string target,CancellationToken token=default)
    {preview.StopControl("日常键鼠正在切换，预览操作已停止");return legacy.SwitchAsync(target,token);}
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        legacy.Changed-=OnChanged;
        preview.StopControl("预览操作已停止");
        try{await legacy.DisposeAsync();}finally{await preview.DisposeAsync();}
    }
}
