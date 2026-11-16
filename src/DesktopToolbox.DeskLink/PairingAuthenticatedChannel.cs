using System.Net.Security;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DesktopToolbox.DeskLink;

/// <summary>
/// One authenticated TLS connection and its explicit, per-side consent exchange.
/// Socket origin, current network approval and user interaction are the caller's responsibility.
/// This channel neither creates nor stores pairing credentials or authorizes control.
/// </summary>
internal sealed class PairingAuthenticatedChannel : IAsyncDisposable
{
    private readonly PairingTlsConsentSession session;
    private readonly CancellationTokenRegistration lifetime;
    private readonly byte[] localAddress, remoteAddress;

    private PairingAuthenticatedChannel(PairingTlsConsentSession session, string localFingerprint,
        string remoteFingerprint, IPAddress localAddress, IPAddress remoteAddress, CancellationToken cancellation)
    {
        this.session = session;
        LocalFingerprint = localFingerprint;
        RemoteFingerprint = remoteFingerprint;
        this.localAddress = localAddress.GetAddressBytes(); this.remoteAddress = remoteAddress.GetAddressBytes();
        lifetime = cancellation.UnsafeRegister(static state =>
        {
            var channel = (PairingAuthenticatedChannel)state!;
            // Abort runs synchronously before DisposeAsync returns; observe an eventual drain fault.
            try
            {
                var drain = channel.session.DisposeAsync().AsTask();
                _ = drain.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch { /* A cancellation callback must not escape its caller. */ }
        }, this);
    }

    internal string LocalFingerprint { get; }
    internal string RemoteFingerprint { get; }
    internal PairingConsentPhase Phase => session.Phase;

    internal static Task<PairingAuthenticatedChannel> StartHostAsync(Socket socket, X509Certificate2 certificate,
        PairingSessionIdentity identity, PeerIdentityOffer offer, CancellationToken ct) => StartAsync(socket, certificate, identity, offer, true, ct);

    internal static Task<PairingAuthenticatedChannel> StartPeerAsync(Socket socket, X509Certificate2 certificate,
        PairingSessionIdentity identity, PeerIdentityOffer offer, CancellationToken ct) => StartAsync(socket, certificate, identity, offer, false, ct);

    private static async Task<PairingAuthenticatedChannel> StartAsync(Socket socket, X509Certificate2 certificate,
        PairingSessionIdentity identity, PeerIdentityOffer offer, bool host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket); ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(offer);
        ct.ThrowIfCancellationRequested();
        if (socket.LocalEndPoint is not IPEndPoint local || socket.RemoteEndPoint is not IPEndPoint remote ||
            local.Address.AddressFamily != AddressFamily.InterNetwork || remote.Address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("Pairing requires a connected IPv4 socket.");
        var localAddress = new IPAddress(local.Address.GetAddressBytes());
        var remoteAddress = new IPAddress(remote.Address.GetAddressBytes());
        _ = new PairingConsentState(identity);
        offer.Validate(DateTimeOffset.UtcNow);
        var expectedLocal = host ? identity.HostFingerprint : identity.PeerFingerprint;
        var expectedRemote = host ? identity.PeerFingerprint : identity.HostFingerprint;
        if (!certificate.HasPrivateKey || !DeskLinkFingerprint.Matches(expectedLocal, DeskLinkFingerprint.OfCertificate(certificate)))
            throw new AuthenticationException("Local pairing identity does not match its certificate.");

        // This method owns the connected socket after the network stream is created, including on failures.
        var stream = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false,
            (_, remote, _, _) => Matches(expectedRemote, remote));
        PairingTlsConsentSession? session = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var remaining = identity.ExpiresAtUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new AuthenticationException("Pairing session expired.");
            timeout.CancelAfter(remaining);
            if (host)
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate, ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck, AllowTlsResume = false
                }, timeout.Token).ConfigureAwait(false);
            else
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "nian-link-pairing", ClientCertificates = new X509CertificateCollection { certificate },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck, AllowTlsResume = false
                }, timeout.Token).ConfigureAwait(false);
            session = new PairingTlsConsentSession(stream, identity, offer, host);
            await session.BeginAsync(timeout.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var result = new PairingAuthenticatedChannel(session, expectedLocal, expectedRemote, localAddress, remoteAddress, ct);
            session = null;
            return result;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            else await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal Task ConfirmLocalAsync(CancellationToken ct = default) => session.ConfirmLocalAsync(ct);
    internal Task ReceiveRemoteConfirmationAsync(CancellationToken ct = default) => session.ReceiveRemoteConfirmationAsync(ct);
    internal PairingTlsConsentSession.DeliveryLease BeginDelivery() => session.BeginDelivery(new IPAddress(localAddress), new IPAddress(remoteAddress));

    public async ValueTask DisposeAsync()
    {
        lifetime.Dispose();
        await session.DisposeAsync().ConfigureAwait(false);
    }

    private static bool Matches(string expected, X509Certificate? actual)
    {
        if (actual is null) return false;
        try
        {
            var expectedBytes = Convert.FromHexString(DeskLinkFingerprint.Normalize(expected));
            var actualBytes = SHA256.HashData(actual.GetRawCertData());
            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException) { return false; }
    }
}
