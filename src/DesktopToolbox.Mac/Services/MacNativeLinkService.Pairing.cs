using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Models;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Services;

public sealed partial class MacNativeLinkService
{
    private const DevicePermission BothPermissions = DevicePermission.Control | DevicePermission.PreviewControl;
    private static void ValidateNativeBundle(DeskLinkPairingBundle bundle)
    {
        if (bundle.Version != 2 || bundle.InputInvitation is null) throw new InvalidOperationException("此候选版需要包含原生键鼠与预览控制邀请的新版配对文件。");
        bundle.Validate();
    }
    private void RequireLocalApproval()
    {
        ThrowIfDisposed();
        // Called synchronously in the visible Pair button's event, before any await
        // loses AppKit currentEvent. The real production facade has no bypass.
        if (!desktop.IsGuardHealthy || !desktop.IsCurrentEventEligibleForLocalApproval())
            throw new InvalidOperationException("请在 Mac 本机使用未注入的输入，明确同意双方键鼠与预览控制。");
    }
    public async Task<PairingPreview> PreviewPairingAsync(string json, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed(); var bundle = DeskLinkPairingBundle.Parse(json); ValidateNativeBundle(bundle);
            var invitation = DeviceInvitation.Parse(bundle.InputInvitation!);
            var description = "确认后将持续允许双方进行键鼠控制与预览点控；仅解锁的普通桌面可接受短时控制。" + DescribeDisplay(bundle.Display);
            var preview = new PairingPreview(Guid.NewGuid().ToString("N"), bundle.Host, invitation.Port, bundle.WindowsName, bundle.MacName, invitation.Fingerprint, description);
            candidate = (preview, bundle); return preview;
        }
        finally { gate.Release(); }
    }
    public Task AcceptPairingAsync(string previewId, CancellationToken cancellationToken = default)
    {
        try { RequireLocalApproval(); }
        catch (Exception failure) { return Task.FromException(failure); }
        return AcceptPairingInsideAsync(previewId, cancellationToken);
    }
    private async Task AcceptPairingInsideAsync(string previewId, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed(); await LoadAsync(token);
            if (Volatile.Read(ref running) != 0 || displaySwitch.IsWriteInFlight || displays?.HasUnfinishedChildren == true)
                throw new InvalidOperationException("请先停止共享并等待显示命令结束，再更换配对。");
            var pending = candidate ?? throw new InvalidOperationException("请重新导入并核对配对资料。");
            if (pending.Preview.Id != previewId) throw new InvalidOperationException("配对资料已变化，请重新核对。");
            if (!desktop.IsGuardHealthy) throw new InvalidOperationException("Mac 本机输入保护已失效。");
            await EnsureRuntimeAsync(pending.Bundle, token);
            Publish(); // Expose this device's actual certificate while Windows is still asking the user to compare it.
            var invitation = DeviceInvitation.Parse(pending.Bundle.InputInvitation!);
            // Windows must visibly accept its local pairing before this machine
            // commits any reverse permission or persistent local input policy.
            var endpoint = await DesktopInputClient.PairAsync(identity!, invitation, server!.Port, token);
            ThrowIfDisposed(); token.ThrowIfCancellationRequested();
            if (!desktop.IsGuardHealthy) throw new InvalidOperationException("Mac 本机输入保护已失效，未保存反向允许。");
            await trust!.GrantAsync(new(endpoint.Fingerprint, endpoint.DeviceName, BothPermissions, DateTimeOffset.UtcNow), token);
            var saved = new ApprovedPairing(true, pending.Bundle.ToJson(), endpoint, BothPermissions);
            await store.WriteAsync("paired-control.json", saved, token);
            approved = saved; pairing = pending.Bundle; candidate = null;
            settings = settings with { WindowsHost = pairing.Host, WindowsName = pairing.WindowsName, MacName = pairing.MacName };
            input.Configure(endpoint.Fingerprint, BothPermissions); ConfigureDisplay(); controlReady = false; error = ""; Publish();
        }
        finally { gate.Release(); }
    }
}
