using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DesktopToolbox.DeskLink;

namespace NianLink.CandidateDiscovery;

/// <summary>
/// One host-side pairing confirmation attempt for a user-selected discovery result.
/// Confirmation authenticates only this connection; it creates no grant or invitation.
/// </summary>
public sealed class SecurePairingHost : IAsyncDisposable
{
    private readonly PairingAuthenticatedChannel channel;
    private readonly DiscoveryReviewLease lease;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken lifetimeToken;
    private readonly Task monitor;
    private readonly X509Certificate2 certificate;
    private readonly MultiPeerRuntime runtime;
    private int completing;
    private readonly object disposeGate = new();
    private Task? disposeTask;

    private SecurePairingHost(PairingAuthenticatedChannel channel, DiscoveryReviewLease lease,
        CancellationTokenSource lifetime, Task monitor, X509Certificate2 certificate, MultiPeerRuntime runtime)
    { this.channel = channel; this.lease = lease; this.lifetime = lifetime; lifetimeToken = lifetime.Token; this.monitor = monitor; this.certificate = certificate; this.runtime = runtime; }

    public string LocalFingerprint => channel.LocalFingerprint;
    public string RemoteFingerprint => channel.RemoteFingerprint;
    public CancellationToken LifetimeToken => lifetimeToken;
    public bool BothConfirmed => !lifetime.IsCancellationRequested && lease.IsFresh && channel.Phase == PairingConsentPhase.ReadyToPrepare;

    /// <summary>
    /// Starts only for the selected candidate and its original approved network. On successful claim,
    /// ownership of the selection's lease transfers here, even if startup fails.
    /// </summary>
    public static async Task<SecurePairingHost> StartAsync(MultiPeerRuntime host, CandidateReviewSelection selection,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(selection);
        ct.ThrowIfCancellationRequested();
        if (!selection.TryClaimForSecurePairing()) throw new InvalidOperationException("This discovered device was already selected.");
        var lease = selection.Lease;
        var binding = lease.Binding;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Socket? listener = null, sender = null, accepted = null;
        X509Certificate2? certificate = null;
        PairingAuthenticatedChannel? channel = null;
        var monitor = MonitorNetworkAsync(lease, lifetime);
        try
        {
            if (!OperatingSystem.IsWindows() || !binding.HasLiveNetworkOrigin || !lease.IsFresh || !binding.IsHardwareAvailable())
                throw new InvalidOperationException("The approved discovery network is unavailable.");
            var identity = await host.OpenPairingIdentityAsync(binding, lifetime.Token).ConfigureAwait(false);
            certificate = identity.Certificate;
            listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            DiscoveryInterfacePin.Configure(listener, binding.InterfaceIndex);
            listener.Bind(new IPEndPoint(binding.Address, 0)); listener.Listen(4);
            if (listener.LocalEndPoint is not IPEndPoint local || !local.Address.Equals(binding.Address) || local.Port < 1024)
                throw new InvalidOperationException("The pairing listener is not bound to the approved network.");
            var now = DateTimeOffset.UtcNow;
            var notice = HostPairingNoticeFactory.Create(selection, binding, identity.Name,
                DeskLinkFingerprint.OfCertificate(certificate), local.Port, now);
            lifetime.CancelAfter(notice.SessionExpiresAtUtc - now);
            if (!lease.IsFresh || !binding.IsHardwareAvailable()) throw new InvalidOperationException("The discovery network changed.");
            sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            DiscoveryInterfacePin.Configure(sender, binding.InterfaceIndex);
            sender.Bind(new IPEndPoint(binding.Address, 0));
            var frame = notice.Encode(now);
            if (await sender.SendToAsync(frame, SocketFlags.None, new IPEndPoint(selection.SourceAddress, DiscoveryProtocol.Port), lifetime.Token).ConfigureAwait(false) != frame.Length)
                throw new IOException("The pairing request was not sent completely.");
            sender.Dispose(); sender = null;

            // Network source is only a route constraint. TLS and user comparison authenticate the peer.
            while (true)
            {
                accepted = await listener.AcceptAsync(lifetime.Token).ConfigureAwait(false);
                if (accepted.RemoteEndPoint is IPEndPoint remote && remote.Address.Equals(selection.SourceAddress) &&
                    accepted.LocalEndPoint is IPEndPoint acceptedLocal && acceptedLocal.Address.Equals(binding.Address)) break;
                accepted.Dispose(); accepted = null;
            }
            listener.Dispose(); listener = null;
            if (!lease.IsFresh || !binding.IsHardwareAvailable()) throw new InvalidOperationException("The discovery network changed.");
            var pairingIdentity = new PairingSessionIdentity(notice.SessionId, notice.OfferNonce, notice.TransactionId,
                notice.HostName, notice.PeerName, notice.HostFingerprint, selection.Offer.KeyboardFingerprint, notice.SessionExpiresAtUtc);
            channel = await PairingAuthenticatedChannel.StartHostAsync(accepted, certificate, pairingIdentity, selection.Offer, lifetime.Token).ConfigureAwait(false);
            accepted = null; // The authenticated channel owns the connected socket.
            if (!lease.IsFresh || lifetime.IsCancellationRequested) throw new InvalidOperationException("The discovery network changed.");
            var result = new SecurePairingHost(channel, lease, lifetime, monitor, certificate, host);
            channel = null; certificate = null;
            return result;
        }
        catch (Exception startup)
        {
            var errors = new List<Exception>();
            try { lifetime.Cancel(); } catch (Exception error) { errors.Add(error); }
            try { sender?.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { accepted?.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { listener?.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { await monitor.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { certificate?.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { await lease.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { lifetime.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new PairingStartupCleanupException(startup, errors);
            throw;
        }
    }

    public async Task ConfirmAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            if (!lease.IsFresh) throw new InvalidOperationException("The approved network changed.");
            await channel.ConfirmLocalAsync(linked.Token).ConfigureAwait(false);
        }
        catch { CancelLifetime(); throw; }
    }
    public async Task WaitForRemoteConfirmationAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            await channel.ReceiveRemoteConfirmationAsync(linked.Token).ConfigureAwait(false);
            if (!lease.IsFresh) throw new InvalidOperationException("The approved network changed.");
        }
        catch { CancelLifetime(); throw; }
    }

    /// <summary>Complete one explicitly confirmed pairing on this same connection.</summary>
    public async Task<TrustedDeviceIdentity> CompletePairingAsync(MultiPeerPairingCoordinator pairing, CancellationToken ct = default)
    {
        FilelessPairingAvailability.EnsureCompletionAvailable();
        ArgumentNullException.ThrowIfNull(pairing);
        if (!BothConfirmed || Interlocked.CompareExchange(ref completing, 1, 0) != 0)
            throw new InvalidOperationException("Both sides must confirm this pairing before continuing.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            using var delivery = channel.BeginDelivery();
            await delivery.SendPairingOperationAsync(false, linked.Token).ConfigureAwait(false);
            await pairing.PrepareFilelessAsync(delivery, runtime, linked.Token).ConfigureAwait(false);
            var receipt = await delivery.SendPrepareAndAwaitReadyAsync(linked.Token).ConfigureAwait(false);
            await delivery.CommitVerifiedReadyAsync(linked.Token).ConfigureAwait(false);
            await delivery.SendCommittedAsync(linked.Token).ConfigureAwait(false);
            await delivery.ReceiveAppliedAsync(linked.Token).ConfigureAwait(false);
            await delivery.SendFinalAndAwaitAckAsync(linked.Token).ConfigureAwait(false);
            await delivery.CleanupFinishedDeliveryAsync(linked.Token).ConfigureAwait(false);
            var selected = pairing.Vault.GetTrustedDevices().SingleOrDefault(x =>
                x.Name == delivery.Identity.PeerName && x.Fingerprint == delivery.Identity.PeerFingerprint && x.Slot == receipt.Slot)
                ?? throw new InvalidOperationException("The completed device no longer matches the trust store.");
            return selected;
        }
        catch { CancelLifetime(); throw; }
    }

    /// <summary>Finish one matching, previously applied transaction after renewed confirmation.</summary>
    public async Task<TrustedDeviceIdentity?> RecoverHistoricalPairingAsync(MultiPeerPairingCoordinator pairing, CancellationToken ct = default)
    {
        FilelessPairingAvailability.EnsureCompletionAvailable();
        ArgumentNullException.ThrowIfNull(pairing);
        if (!BothConfirmed || Interlocked.CompareExchange(ref completing, 1, 0) != 0)
            throw new InvalidOperationException("Both sides must confirm this recovery before continuing.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            using var delivery = channel.BeginDelivery();
            await delivery.SendPairingOperationAsync(true, linked.Token).ConfigureAwait(false);
            if (!await delivery.ResumeHistoricalCommittedAsync(pairing.Vault, linked.Token).ConfigureAwait(false)) return null;
            var binding = delivery.GetVerifiedHistoricalBinding(linked.Token);
            await delivery.CleanupHistoricalFinishedDeliveryAsync(linked.Token).ConfigureAwait(false);
            return pairing.Vault.GetTrustedDevices().SingleOrDefault(x =>
                x.Name == binding.Identity.PeerName && x.Fingerprint == binding.Identity.PeerFingerprint && x.Slot == binding.Slot)
                ?? throw new InvalidOperationException("The recovered device no longer matches the trust store.");
        }
        catch { CancelLifetime(); throw; }
    }

    private void CancelLifetime() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }

    public ValueTask DisposeAsync() { lock (disposeGate) return new(disposeTask ??= DisposeCoreAsync()); }

    private async Task DisposeCoreAsync()
    {
        var errors = new List<Exception>();
        try { lifetime.Cancel(); } catch (Exception error) { errors.Add(error); }
        try { await channel.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { await monitor.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { certificate.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { await lease.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { lifetime.Dispose(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Pairing cleanup failed.", errors);
    }

    private static async Task MonitorNetworkAsync(DiscoveryReviewLease lease, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (!lease.IsFresh) { lifetime.Cancel(); break; }
                await Task.Delay(TimeSpan.FromMilliseconds(250), lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
}
