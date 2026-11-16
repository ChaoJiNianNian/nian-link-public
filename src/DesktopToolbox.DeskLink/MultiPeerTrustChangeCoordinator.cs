namespace DesktopToolbox.DeskLink;

public interface IMultiPeerRuntime
{
    Task StopAsync(CancellationToken ct);
    Task StartAsync(IReadOnlyList<TrustedDeviceIdentity> peers, CancellationToken ct);
}

/// <summary>Stops existing keyboard sessions before changing their trusted device set.</summary>
public sealed class MultiPeerTrustChangeCoordinator(MultiDeviceCredentialVault vault, IMultiPeerRuntime runtime, TimeProvider? clock = null)
{
    private readonly MultiDeviceCredentialVault vault = vault ?? throw new ArgumentNullException(nameof(vault));
    private readonly IMultiPeerRuntime runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    // All coordinators for the same vault must serialize session reconfiguration.
    private SemaphoreSlim Gate => vault.SessionChangeGate;

    public async Task<MultiPeerInvitation> ExportInvitationAsync(MultiPeerPairingCoordinator pairing, string name,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        if (!ReferenceEquals(pairing.Vault, vault)) throw new ArgumentException("Pairing uses a different trust store.", nameof(pairing));
        await Gate.WaitAsync(ct);
        try { return pairing.ExportTrustedInvitation(name); }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Rebuilds sharing after an independently completed pairing. It cannot grant or revoke trust.
    /// The caller must already have verified the peer's completion receipt.
    /// </summary>
    public async Task RebuildAfterFilelessPairingAsync(TrustedDeviceIdentity completedPeer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(completedPeer);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var peers = vault.GetTrustedDevices();
            if (!peers.Contains(completedPeer))
                throw new InvalidOperationException("The completed pairing no longer matches trusted devices.");
            await runtime.StopAsync(ct).ConfigureAwait(false);
            try
            {
                // Once the old session stops, finish reconfiguration even if the UI request is cancelled.
                await runtime.StartAsync(vault.GetTrustedDevices(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Both sides may already have committed. Never revoke their credential here.
                throw new InvalidOperationException("The device is paired, but sharing could not restart.", error);
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>Commit a reviewed device only after the running device set can be rebuilt.</summary>
    public async Task<MultiPeerInvitation> AddAsync(MultiPeerPairingCoordinator pairing, string previewId,
        string fingerprint, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        if (!ReferenceEquals(pairing.Vault, vault)) throw new ArgumentException("Pairing uses a different trust store.", nameof(pairing));
        await Gate.WaitAsync(ct);
        try
        {
            pairing.ValidateConfirmation(previewId, fingerprint, now);
            var before = vault.GetTrustedDevices();
            await runtime.StopAsync(ct);
            MultiPeerInvitation invitation;
            // Stopping an active session can take time. Recheck expiry at commit,
            // not only when the user originally clicked confirmation.
            try { invitation = await pairing.ConfirmAsync(previewId, fingerprint, clock.GetUtcNow(), CancellationToken.None); }
            catch (Exception error)
            {
                try { if (before.Count != 0) await runtime.StartAsync(before, CancellationToken.None); }
                catch (Exception recovery) { throw new AggregateException("Device trust was not changed and runtime recovery failed.", error, recovery); }
                throw;
            }
            try
            {
                await runtime.StartAsync(vault.GetTrustedDevices(), CancellationToken.None);
                return invitation;
            }
            catch (Exception error)
            {
                try
                {
                    // Do not alter trust while a partially started keyboard session may still exist.
                    await runtime.StopAsync(CancellationToken.None);
                    await vault.RevokeAsync(invitation.PeerName, CancellationToken.None);
                    if (before.Count != 0) await runtime.StartAsync(before, CancellationToken.None);
                }
                catch (Exception recovery) { throw new AggregateException("The new session failed and recovery needs attention.", error, recovery); }
                throw new InvalidOperationException("The new device could not be started; its trust was removed.", error);
            }
        }
        finally { Gate.Release(); }
    }

    public async Task<bool> RevokeAsync(string name, CancellationToken ct = default)
    {
        DeskflowProfile.ValidateName(name);
        await Gate.WaitAsync(ct);
        try
        {
            var before = vault.GetTrustedDevices();
            if (!before.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
            await runtime.StopAsync(ct);
            try
            {
                // Once stopped, finish the trust change and recovery rather than
                // letting a cancelled UI request leave the runtime half updated.
                await vault.RevokeAsync(name, CancellationToken.None);
            }
            catch (Exception error)
            {
                try { if (before.Count != 0) await runtime.StartAsync(before, CancellationToken.None); }
                catch (Exception recovery) { throw new AggregateException("Trust was not changed and runtime recovery failed.", error, recovery); }
                throw;
            }
            var remaining = vault.GetTrustedDevices();
            if (remaining.Count != 0)
            {
                try { await runtime.StartAsync(remaining, CancellationToken.None); }
                catch (Exception error) { throw new InvalidOperationException("The device was revoked but the remaining connections could not restart.", error); }
            }
            return true;
        }
        finally { Gate.Release(); }
    }
}
