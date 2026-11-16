using System.Net;
using System.Security.Authentication;
using Nian.Input.Security;

namespace Nian.Input.Runtime;

public sealed partial class PreviewOnlyDeviceService
{
    private static bool ValidApproval(PairingApproval approval) => approval.RequestedPermission == DevicePermission.PreviewControl &&
        approval.ReceiverPort is > 0 and <= 65535 && IPAddress.TryParse(approval.SourceAddress, out var address) &&
        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) &&
        PreviewOnlyDeviceStore.ValidEndpoint(new(address.ToString(), approval.ReceiverPort.Value, approval.Fingerprint, approval.DeviceName));
    private async Task<bool> ConfirmIncomingAsync(PairingApproval approval, CancellationToken token)
    {
        if (!ValidApproval(approval) || approval.ExpiresAt <= UtcNow) return false;
        IncomingApproval pending;
        lock (pairingGate)
        {
            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref started) == 0 || incomingApproval is not null) return false;
            pending = new(pairingGeneration, approval, token); incomingApproval = pending;
        }
        try
        {
            SetStatus("等待本机确认对方的预览操作指纹", clearError: true);
            // The platform performs its guard in the real local Accept handler, never on this network continuation.
            var accepted = await options.ConfirmIncomingPairingAsync(approval, token).WaitAsync(token).ConfigureAwait(false);
            lock (pairingGate)
            {
                if (!ReferenceEquals(incomingApproval, pending)) return false;
                if (!accepted) { SetPairStatus(pending.Generation, "本机未批准预览操作", "预览操作授权未确认。"); return false; }
                RequireGeneration(pending.Generation, token);
                if (UtcNow >= approval.ExpiresAt) return false;
                incomingApproval = pending with { Accepted = true };
            }
            return true;
        }
        catch (OperationCanceledException) { SetPairStatus(pending.Generation, "预览操作配对已取消", "配对确认已取消或过期。"); return false; }
        catch { SetPairStatus(pending.Generation, "预览操作配对未完成", "无法完成本机预览授权确认。"); throw; }
        finally
        {
            lock (pairingGate)
                if (ReferenceEquals(incomingApproval, pending)) incomingApproval = null;
        }
    }
    private void CommitIncoming(PairedDevice device)
    {
        IncomingApproval? approved;
        lock (pairingGate) approved = incomingApproval;
        if (approved is null || !MatchesApproval(approved, device)) throw new AuthenticationException("The reverse preview endpoint was not approved locally.");
        peerMutation.Wait(lifetime.Token);
        try
        {
            lock (pairingGate) ValidateIncomingCommit(approved, device);
            StopControl("preview-peer-replaced");
            var endpoint = device.ReceiverEndpoint!;
            var saved = new SavedPreviewPeer(1, true, DevicePermission.PreviewControl, endpoint);
            long committedGeneration = 0;
            store.WriteAsync(saved, approved.Cancellation, () => AcquireIncomingCommit(approved, device),
                () => { CommitPeer(saved); committedGeneration = pairingGeneration; }).GetAwaiter().GetResult();
            SetPairStatus(committedGeneration, "预览操作已配对，当前仅观看", clearError: true);
        }
        catch { SetPairStatus(approved.Generation, "预览操作配对未完成", "本机未能保存完整预览授权，尚未允许操作。"); throw; }
        finally
        {
            lock (pairingGate) if (ReferenceEquals(incomingApproval, approved)) incomingApproval = null;
            peerMutation.Release();
        }
    }
    private static bool MatchesApproval(IncomingApproval approved, PairedDevice device) => approved.Accepted &&
        device.GrantedPermissions == DevicePermission.PreviewControl && approved.Tuple.RequestedPermission == DevicePermission.PreviewControl &&
        approved.Tuple.Fingerprint == device.Fingerprint && approved.Tuple.DeviceName == device.DeviceName &&
        approved.Tuple.SourceAddress == device.SourceAddress && approved.Tuple.ReceiverPort == device.ReceiverPort &&
        device.ReceiverEndpoint is { } endpoint && PreviewOnlyDeviceStore.ValidEndpoint(endpoint) && IPAddress.TryParse(endpoint.Host, out _);
    private void ValidateIncomingCommit(IncomingApproval approved, PairedDevice device)
    {
        RequireGeneration(approved.Generation, approved.Cancellation);
        if (!ReferenceEquals(incomingApproval, approved) || !MatchesApproval(approved, device) || UtcNow >= approved.Tuple.ExpiresAt)
            throw new AuthenticationException("The local reverse endpoint decision expired or changed.");
    }
    private IDisposable AcquireIncomingCommit(IncomingApproval approved, PairedDevice device)
    {
        Monitor.Enter(pairingGate);
        try { ValidateIncomingCommit(approved, device); return new PairCommitLease(pairingGate); }
        catch { Monitor.Exit(pairingGate); throw; }
    }
}
