using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

/// <summary>Test-only synthetic-certificate mTLS on loopback; never opens a LAN endpoint.</summary>
internal static class PairingTlsLoopbackProbe
{
    internal sealed record Evidence(string HostFingerprint, string PeerFingerprint);
    internal sealed class Failure(bool hostRejected, bool peerRejected, IReadOnlyList<Exception> causes) : Exception("Loopback TLS failed.")
    {
        internal bool HostRejected { get; } = hostRejected;
        internal bool PeerRejected { get; } = peerRejected;
        internal IReadOnlyList<Exception> Causes { get; } = causes;
    }

    internal static async Task<Evidence> ConnectAsync(X509Certificate2 host, X509Certificate2 peer,
        string expectedHost, string expectedPeer, CancellationToken ct = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var hostRejected = 0; var peerRejected = 0;
        var hostReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Evidence> HostAsync()
        {
            try
            {
                using var accepted = await listener.AcceptTcpClientAsync(lifetime.Token);
                await using var tls = new SslStream(accepted.GetStream(), false, (_, certificate, _, _) =>
                {
                    var valid = Match(expectedPeer, certificate);
                    if (!valid) Interlocked.Exchange(ref hostRejected, 1);
                    return valid;
                });
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = host,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    EncryptionPolicy = EncryptionPolicy.RequireEncryption
                }, lifetime.Token);
                if (!tls.IsEncrypted || !tls.IsMutuallyAuthenticated || tls.RemoteCertificate is null)
                    throw new AuthenticationException("Mutual TLS was not established.");
                hostReady.TrySetResult();
                await peerReady.Task.WaitAsync(lifetime.Token);
                return new(DeskLinkFingerprint.OfCertificate(host), Hash(tls.RemoteCertificate));
            }
            catch { await lifetime.CancelAsync(); throw; }
        }
        async Task<string> PeerAsync()
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, lifetime.Token);
                await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                {
                    var valid = Match(expectedHost, certificate);
                    if (!valid) Interlocked.Exchange(ref peerRejected, 1);
                    return valid;
                },
                    (_, _, _, _, _) => peer);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "nian-discovery-test",
                    ClientCertificates = new X509CertificateCollection { peer },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    EncryptionPolicy = EncryptionPolicy.RequireEncryption
                }, lifetime.Token);
                if (!tls.IsEncrypted || !tls.IsMutuallyAuthenticated || tls.RemoteCertificate is null)
                    throw new AuthenticationException("Mutual TLS was not established.");
                peerReady.TrySetResult();
                await hostReady.Task.WaitAsync(lifetime.Token);
                return Hash(tls.RemoteCertificate);
            }
            catch { await lifetime.CancelAsync(); throw; }
        }
        var hostTask = HostAsync(); var peerTask = PeerAsync();
        try { await Task.WhenAll(hostTask, peerTask); }
        catch
        {
            var errors = new[] { hostTask.Exception, peerTask.Exception }.Where(e => e is not null)
                .SelectMany(e => e!.Flatten().InnerExceptions).ToArray();
            throw new Failure(hostRejected != 0, peerRejected != 0, errors);
        }
        var evidence = await hostTask;
        if (evidence.HostFingerprint != await peerTask) throw new AuthenticationException("Host identity did not match.");
        return evidence;
    }
    private static string Hash(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    private static bool Match(string expected, X509Certificate? certificate) => certificate is not null && DeskLinkFingerprint.Matches(expected, Hash(certificate));
}
