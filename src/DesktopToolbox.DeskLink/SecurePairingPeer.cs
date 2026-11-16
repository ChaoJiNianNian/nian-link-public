using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace DesktopToolbox.DeskLink;

/// <summary>
/// A peer-side, short-lived authenticated connection created only after the user accepts a real request.
/// A confirmed session does not create a pairing credential or authorize control.
/// </summary>
public sealed class SecurePairingPeer : IAsyncDisposable
{
    private readonly PairingAuthenticatedChannel channel;
    private readonly PairingNetworkHandoff network;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken lifetimeToken;
    private readonly X509Certificate2 certificate;
    private int completing;
    private readonly object gate = new();
    private Task? disposeTask;

    private SecurePairingPeer(PairingAuthenticatedChannel channel, PairingNetworkHandoff network,
        CancellationTokenSource lifetime, X509Certificate2 certificate)
    { this.channel = channel; this.network = network; this.lifetime = lifetime; lifetimeToken = lifetime.Token; this.certificate = certificate; }

    public string LocalFingerprint => channel.LocalFingerprint;
    public string RemoteFingerprint => channel.RemoteFingerprint;
    public CancellationToken LifetimeToken => lifetimeToken;
    public bool BothConfirmed => !lifetime.IsCancellationRequested && channel.Phase == PairingConsentPhase.ReadyToPrepare;

    /// <summary>
    /// Called only by the application's explicit user-accept action with its own keyboard profile.
    /// The actual address, offer and network are held by the one-use request, not supplied by the UI.
    /// </summary>
    public static async Task<SecurePairingPeer> AcceptAsync(PairingIncomingRequest request, string keyboardProfile,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var claim = request.TryClaim() ?? throw new InvalidOperationException("This pairing request is no longer available.");
        if (!claim.Binding.HasLiveNetworkOrigin) throw new InvalidOperationException("The approved network is unavailable.");
        PairingNetworkHandoff? network = null;
        CancellationTokenSource? lifetime = null;
        X509Certificate2? certificate = null;
        Socket? socket = null;
        PairingAuthenticatedChannel? channel = null;
        try
        {
            // The old discovery run must remain live while its approved network is handed off.
            using (var handoff = CancellationTokenSource.CreateLinkedTokenSource(ct, claim.RevocationToken))
                network = await claim.AcquireNetworkAsync(handoff.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (network is null || !network.Binding.HasLiveNetworkOrigin || network.RevocationToken.IsCancellationRequested ||
                !await network.RevalidateAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("The approved network changed.");
            var notice = claim.Notice;
            var now = DateTimeOffset.UtcNow;
            if (now >= claim.ExpiresAtUtc || now >= notice.SessionExpiresAtUtc ||
                notice.SessionExpiresAtUtc > claim.Offer.ExpiresAtUtc || notice.SessionExpiresAtUtc > now.AddMinutes(2))
                throw new InvalidOperationException("The pairing request expired.");
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, network.RevocationToken);
            lifetime.CancelAfter(notice.SessionExpiresAtUtc - now);
            var path = CertificatePath(keyboardProfile);
            if (!File.Exists(path)) throw new InvalidOperationException("The local keyboard identity is unavailable.");
            certificate = PrivateFiles.LoadTlsCertificate(path);
            if (!certificate.HasPrivateKey || !DeskLinkFingerprint.Matches(claim.Offer.KeyboardFingerprint, DeskLinkFingerprint.OfCertificate(certificate)))
                throw new InvalidOperationException("The selected local identity changed.");
            if (!await network.RevalidateAsync(lifetime.Token).ConfigureAwait(false))
                throw new InvalidOperationException("The approved network changed.");
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            DiscoveryInterfacePin.Configure(socket, network.Binding.InterfaceIndex);
            socket.Bind(new IPEndPoint(network.Binding.Address, 0));
            await socket.ConnectAsync(new IPEndPoint(claim.SourceAddress, notice.Port), lifetime.Token).ConfigureAwait(false);
            if (socket.LocalEndPoint is not IPEndPoint local || !local.Address.Equals(network.Binding.Address) ||
                socket.RemoteEndPoint is not IPEndPoint remote || !remote.Address.Equals(claim.SourceAddress) || remote.Port != notice.Port ||
                !await network.RevalidateAsync(lifetime.Token).ConfigureAwait(false))
                throw new InvalidOperationException("The pairing connection is not on the approved network.");
            var identity = new PairingSessionIdentity(notice.SessionId, notice.OfferNonce, notice.TransactionId,
                notice.HostName, notice.PeerName, notice.HostFingerprint, claim.Offer.KeyboardFingerprint, notice.SessionExpiresAtUtc);
            channel = await PairingAuthenticatedChannel.StartPeerAsync(socket, certificate, identity, claim.Offer, lifetime.Token).ConfigureAwait(false);
            socket = null; // The channel owns the connected socket.
            if (lifetime.IsCancellationRequested || !await network.RevalidateAsync(lifetime.Token).ConfigureAwait(false))
                throw new InvalidOperationException("The approved network changed.");
            var result = new SecurePairingPeer(channel, network, lifetime, certificate);
            channel = null; network = null; lifetime = null; certificate = null;
            return result;
        }
        catch (Exception startup)
        {
            var errors = new List<Exception>();
            try { lifetime?.Cancel(); } catch (Exception error) { errors.Add(error); }
            try { socket?.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { certificate?.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { if (network is not null) await network.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { lifetime?.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new PairingStartupCleanupException(startup, errors);
            throw;
        }
    }

    public async Task ConfirmAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            if (!await network.RevalidateAsync(linked.Token).ConfigureAwait(false)) throw new InvalidOperationException("The approved network changed.");
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
            if (!await network.RevalidateAsync(linked.Token).ConfigureAwait(false)) throw new InvalidOperationException("The approved network changed.");
        }
        catch { CancelLifetime(); throw; }
    }

    /// <summary>Receive the authenticated host's operation, then use this store for the matching transaction.</summary>
    public async Task<bool> AcceptPairingOperationAsync(PeerInvitationStore store, CancellationToken ct = default)
    {
        FilelessPairingAvailability.EnsureCompletionAvailable();
        ArgumentNullException.ThrowIfNull(store);
        if (!BothConfirmed || Interlocked.CompareExchange(ref completing, 1, 0) != 0)
            throw new InvalidOperationException("Both sides must confirm this pairing before continuing.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            using var delivery = channel.BeginDelivery();
            if (await delivery.ReceivePairingOperationAsync(linked.Token).ConfigureAwait(false))
                return await delivery.AnswerHistoricalCommittedAsync(store, linked.Token).ConfigureAwait(false);
            await delivery.ReceivePrepareAndStageAsync(store, linked.Token).ConfigureAwait(false);
            await delivery.ReceiveCommittedAsync(store, linked.Token).ConfigureAwait(false);
            await delivery.PromoteAndSendAppliedAsync(store, linked.Token).ConfigureAwait(false);
            await delivery.ReceiveFinalAndFinalizeAsync(store, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch { CancelLifetime(); throw; }
    }

    private void CancelLifetime() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }

    public ValueTask DisposeAsync()
    { lock (gate) return new(disposeTask ??= DisposeCoreAsync()); }

    private async Task DisposeCoreAsync()
    {
        var errors = new List<Exception>();
        try { lifetime.Cancel(); } catch (Exception error) { errors.Add(error); }
        try { await channel.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { await network.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { certificate.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { lifetime.Dispose(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Pairing cleanup failed.", errors);
    }

    private static string CertificatePath(string keyboardProfile)
    {
        if (string.IsNullOrWhiteSpace(keyboardProfile) || !Path.IsPathFullyQualified(keyboardProfile))
            throw new ArgumentException("An absolute keyboard profile is required.", nameof(keyboardProfile));
        var path = Path.Combine(Path.GetFullPath(keyboardProfile), "tls", "deskflow.pem");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked identity path is not allowed."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }
}
