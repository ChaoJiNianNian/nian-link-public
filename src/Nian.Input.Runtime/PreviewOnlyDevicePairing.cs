using System.Security.Authentication;
using Nian.Input.Security;

namespace Nian.Input.Runtime;

public sealed partial class PreviewOnlyDeviceService
{
    private sealed class PairCommitLease(object gate) : IDisposable { public void Dispose() => Monitor.Exit(gate); }
    private sealed record IncomingApproval(long Generation, PairingApproval Tuple, CancellationToken Cancellation, bool Accepted = false);
    private IncomingApproval? incomingApproval;
    private DateTimeOffset UtcNow => (options.Clock ?? TimeProvider.System).GetUtcNow();

    public DeviceInvitation CreateInvitation()
    {
        DeviceInvitation invitation;
        lock (pairingGate)
        {
            RequireStarted(); pairingGeneration = checked(pairingGeneration + 1); incomingApproval = null;
            invitation = server!.CreateInvitation(DevicePermission.PreviewControl);
        }
        SetStatus("请在另一台电脑粘贴预览配对码并核对指纹", clearError: true);
        return invitation;
    }
    /// <summary>Closes this dialog's invitations/unfinished approval without changing the established device grant.</summary>
    public void CancelInvitation()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        InvalidatePairing();
        SetStatus("本次预览配对已取消，已有授权保持不变");
    }
    public Task PairAsync(string code, CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfDisposed();
            // Deliberately outside any async method: AppKit currentEvent must still be the actual Accept click.
            options.RequireLocalApproval(); cancellationToken.ThrowIfCancellationRequested();
            var invitation = DeviceInvitation.Parse(code);
            if (invitation.Permissions != DevicePermission.PreviewControl) throw new InvalidDataException("Only a preview-only invitation is accepted.");
            long generation;
            lock (pairingGate)
            {
                RequireStarted();
                if (string.Equals(invitation.Fingerprint, identity!.Fingerprint, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A device cannot pair with itself.");
                if (invitation.ExpiresAt <= UtcNow || invitation.ExpiresAt - UtcNow > TimeSpan.FromMinutes(2.1)) throw new AuthenticationException("The preview invitation expired.");
                generation = checked(++pairingGeneration); incomingApproval = null; server!.CloseInvitations();
            }
            StopControl("preview-pairing-started");
            SetStatus("等待另一台电脑本机确认预览操作", clearError: true);
            return PairInsideAsync(invitation, generation, cancellationToken);
        }
        catch (Exception failure)
        {
            SetStatus("预览操作配对未完成", "请使用本机确认并检查预览配对码。");
            return Task.FromException(failure);
        }
    }
    private async Task PairInsideAsync(DeviceInvitation invitation, long generation, CancellationToken token)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try
        {
            var endpoint = await DesktopInputClient.PairAsync(identity!, invitation, Volatile.Read(ref receiverPort), request.Token).ConfigureAwait(false);
            await peerMutation.WaitAsync(request.Token).ConfigureAwait(false);
            try
            {
                lock (pairingGate) RequireGeneration(generation, request.Token);
                StopControl("preview-peer-replaced");
                // An interrupted write can leave a trust entry, but it cannot authorize input without peer.json.
                await trust!.GrantAsync(new(endpoint.Fingerprint, endpoint.DeviceName, DevicePermission.PreviewControl, UtcNow), request.Token).ConfigureAwait(false);
                var saved = new SavedPreviewPeer(1, true, DevicePermission.PreviewControl, endpoint);
                long committedGeneration = 0;
                await store.WriteAsync(saved, request.Token, () => AcquirePairCommit(generation, request.Token),
                    () => { CommitPeer(saved); committedGeneration = pairingGeneration; }).ConfigureAwait(false);
                SetPairStatus(committedGeneration, "预览操作已配对，当前仅观看", clearError: true);
            }
            finally { peerMutation.Release(); }
        }
        catch
        {
            SetPairStatus(generation, "预览操作配对未完成", "配对未完成或已取消，请重新生成配对码并在双方确认。"); throw;
        }
    }
    public async Task RevokePeerAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
        SavedPreviewPeer? previous;
        lock (pairingGate) { InvalidatePairingCore(); previous = Volatile.Read(ref peer); Volatile.Write(ref peer, null); }
        StopControl("preview-permission-revoked");
        await peerMutation.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireStarted();
            // Once revoke starts, persist it even if the presentation request is cancelled.
            if (previous is not null) await trust!.RevokeAsync(previous.Endpoint.Fingerprint, CancellationToken.None).ConfigureAwait(false);
            await store.WriteAsync(null, CancellationToken.None).ConfigureAwait(false);
            SetStatus("预览操作授权已移除，当前仅观看", clearError: true);
        }
        catch { SetStatus("预览操作已停止", "授权记录未能保存，请检查后再次撤销授权。"); throw; }
        finally { peerMutation.Release(); }
    }
    private void RequireStarted()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref started) == 0 || server is null || identity is null || trust is null) throw new InvalidOperationException("Start preview control before pairing.");
    }
    private void RequireGeneration(long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RequireStarted();
        if (generation != pairingGeneration) throw new OperationCanceledException("The local pairing decision was superseded.");
    }
    private IDisposable AcquirePairCommit(long generation, CancellationToken token)
    {
        Monitor.Enter(pairingGate);
        try { RequireGeneration(generation, token); return new PairCommitLease(pairingGate); }
        catch { Monitor.Exit(pairingGate); throw; }
    }
    private void CommitPeer(SavedPreviewPeer value)
    {
        Volatile.Write(ref peer, value); pairingGeneration = checked(pairingGeneration + 1); incomingApproval = null; server?.CloseInvitations();
    }
    private void SetPairStatus(long generation, string value, string? failure = null, bool clearError = false)
    {
        lock (pairingGate)
            if (Volatile.Read(ref disposed) == 0 && generation == pairingGeneration) SetStatus(value, failure, clearError);
    }
    private void InvalidatePairing() { lock (pairingGate) InvalidatePairingCore(); }
    private void InvalidatePairingCore() { pairingGeneration = checked(pairingGeneration + 1); incomingApproval = null; server?.CloseInvitations(); }
}
