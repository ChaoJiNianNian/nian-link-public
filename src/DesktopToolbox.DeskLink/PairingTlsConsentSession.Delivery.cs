using System.Net;
using System.Net.Sockets;

namespace DesktopToolbox.DeskLink;

internal sealed partial class PairingTlsConsentSession
{
    private DeliveryLease? delivery;

    /// <summary>
    /// One in-process right to attempt delivery on this already authenticated and mutually confirmed
    /// connection. It does not itself create, persist, send or authorize a credential.
    /// </summary>
    internal sealed partial class DeliveryLease : IDisposable
    {
        private readonly PairingTlsConsentSession owner;
        private int closed;
        private int stageStarted;
        private bool freezeStarted;
        private MultiDeviceCredentialVault? stagedVault;
        private Guid stagedId;
        private PendingFrozenInvitationMetadata? frozenInvitation;
        private readonly byte[] localAddress, remoteAddress;
        internal DeliveryLease(PairingTlsConsentSession owner, PairingSessionIdentity identity, PeerIdentityOffer offer, bool isHost,
            IPAddress localAddress, IPAddress remoteAddress)
        {
            this.owner = owner; Identity = identity; Offer = offer; IsHost = isHost;
            this.localAddress = localAddress.GetAddressBytes(); this.remoteAddress = remoteAddress.GetAddressBytes();
        }
        internal PairingSessionIdentity Identity { get; }
        internal PeerIdentityOffer Offer { get; }
        internal bool IsHost { get; }
        internal IPAddress LocalAddress => new(localAddress);
        internal IPAddress RemoteAddress => new(remoteAddress);
        internal bool IsClosed => Volatile.Read(ref closed) != 0;
        internal void EnsureCurrent(CancellationToken ct = default) => owner.CheckDelivery(this, ct);
        internal async Task<PendingGrantMetadata> StageHostGrantAsync(MultiDeviceCredentialVault vault, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(vault);
            EnsureCurrent(ct);
            if (!IsHost) throw new InvalidOperationException("Only the confirmed host can stage its own pending grant.");
            if (Interlocked.CompareExchange(ref stageStarted, 1, 0) != 0)
                throw new InvalidOperationException("This pairing session already attempted to stage a grant.");
            owner.BeginDeliveryOperation(this, ct);
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                EnsureCurrent(deadline.Token);
                var pending = await vault.StagePendingGrantAsync(Identity, Offer, owner.clock(), deadline.Token, owner.clock).ConfigureAwait(false);
                // A cancellation after the protected write leaves pending evidence, never active trust.
                lock (owner.gate)
                {
                    owner.CheckDelivery(this, deadline.Token);
                    stagedVault = vault; stagedId = pending.StageId;
                }
                return pending;
            }
            finally { owner.FinishOperation(); }
        }
        internal async Task<PendingFrozenInvitationMetadata> FreezeHostInvitationAsync(VerifiedLanBinding binding,
            int controlPort, int keyboardPort, int dataPortBase, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(binding);
            if (!binding.Address.Equals(LocalAddress)) throw new InvalidOperationException("The invitation network differs from the TLS connection.");
            MultiDeviceCredentialVault vault; Guid stage;
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || freezeStarted || stagedVault is null || stagedId == Guid.Empty)
                    throw new InvalidOperationException("A current host pending grant is required.");
                freezeStarted = true; vault = stagedVault; stage = stagedId; owner.activeOperations++;
            }
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                EnsureCurrent(deadline.Token);
                var frozen = await vault.FreezePendingInvitationAsync(stage, Identity, binding, controlPort,
                    keyboardPort, dataPortBase, owner.clock(), deadline.Token, owner.clock).ConfigureAwait(false);
                lock (owner.gate) { owner.CheckDelivery(this, deadline.Token); frozenInvitation = frozen; }
                return frozen;
            }
            finally { owner.FinishOperation(); }
        }
        // Existing loopback-only vault helper; never accepts a live LAN address.
        internal async Task<PendingFrozenInvitationMetadata> FreezeHostInvitationForLoopbackTestAsync(
            int controlPort, int keyboardPort, int dataPortBase, CancellationToken ct = default)
        {
            if (LocalAddress.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(LocalAddress))
                throw new InvalidOperationException("Only a loopback TLS connection is allowed.");
            MultiDeviceCredentialVault vault; Guid stage;
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || freezeStarted || stagedVault is null || stagedId == Guid.Empty)
                    throw new InvalidOperationException("A current host pending grant is required.");
                freezeStarted = true; vault = stagedVault; stage = stagedId; owner.activeOperations++;
            }
            try
            {
                using var deadline = owner.CreateDeadline(ct);
                var frozen = await vault.FreezePendingInvitationForLoopbackTestAsync(stage, Identity, LocalAddress,
                    controlPort, keyboardPort, dataPortBase, owner.clock(), deadline.Token, owner.clock).ConfigureAwait(false);
                lock (owner.gate) { owner.CheckDelivery(this, deadline.Token); frozenInvitation = frozen; }
                return frozen;
            }
            finally { owner.FinishOperation(); }
        }

        internal (MultiDeviceCredentialVault Vault, Guid StageId, PendingFrozenInvitationMetadata Frozen) GetFrozenHost(CancellationToken ct = default)
        {
            lock (owner.gate)
            {
                owner.CheckDelivery(this, ct);
                if (!IsHost || stagedVault is null || stagedId == Guid.Empty || frozenInvitation is null)
                    throw new InvalidOperationException("The current pairing has no frozen host invitation.");
                return (stagedVault, stagedId, frozenInvitation);
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0) owner.FinishDelivery(this);
        }
        public override string ToString() => "[pairing delivery session]";
    }

    internal DeliveryLease BeginDelivery(IPAddress localAddress, IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(localAddress); ArgumentNullException.ThrowIfNull(remoteAddress);
        if (localAddress.AddressFamily != AddressFamily.InterNetwork || remoteAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Pairing requires IPv4 socket addresses.");
        lock (gate)
        {
            RequireActive();
            if (!helloComplete || !localConfirmation || !remoteConfirmation || remoteChallenge is null ||
                consent.Phase != PairingConsentPhase.ReadyToPrepare || delivery is not null)
                throw new InvalidOperationException("Both sides must confirm the current pairing connection.");
            delivery = new DeliveryLease(this, identity, offer, role == "host", localAddress, remoteAddress);
            activeOperations++;
            return delivery;
        }
    }

    private void CheckDelivery(DeliveryLease candidate, CancellationToken ct)
    {
        lock (gate)
        {
            ct.ThrowIfCancellationRequested(); RequireActive();
            if (!ReferenceEquals(delivery, candidate) || candidate.IsClosed || consent.Phase != PairingConsentPhase.ReadyToPrepare)
                throw new InvalidOperationException("The pairing delivery is no longer available.");
        }
    }

    private void BeginDeliveryOperation(DeliveryLease candidate, CancellationToken ct)
    {
        lock (gate)
        {
            CheckDelivery(candidate, ct);
            activeOperations++;
        }
    }

    private void FinishDelivery(DeliveryLease candidate)
    {
        lock (gate)
        {
            if (!ReferenceEquals(delivery, candidate)) throw new InvalidOperationException("Pairing delivery ownership changed.");
            activeOperations--;
            if (activeOperations == 0 && Volatile.Read(ref disposed) != 0) drained.TrySetResult();
        }
    }
}
