using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MacSyntheticMtlsProbe;

public static class ProbeSession
{
    private static readonly SslProtocols Versions = SslProtocols.Tls12 | SslProtocols.Tls13;
    private static readonly byte[] FinalConfirmation = "NIAN-OK!"u8.ToArray();

    public static async Task<SslProtocols> ServerAsync(IPAddress local, IPAddress peer, int port,
        X509Certificate2 own, string expectedPeer, Action<int> listening, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireInputs(local, peer, own, expectedPeer);
        using var listener = new TcpListener(local, port);
        listener.Start(1);
        listening(((IPEndPoint)listener.LocalEndpoint).Port);
        using var socket = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        if (socket.Client.RemoteEndPoint is not IPEndPoint remote || !remote.Address.Equals(peer))
            throw new AuthenticationException("Unexpected peer address.");
        await using var tls = new SslStream(socket.GetStream(), false, (_, certificate, _, _) => CertificatePin.Matches(expectedPeer, certificate));
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = own, ClientCertificateRequired = true, EnabledSslProtocols = Versions,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption, AllowTlsResume = false
        }, ct).ConfigureAwait(false);
        RequireAuthenticated(tls);
        var challenge = new byte[32];
        await tls.ReadExactlyAsync(challenge, ct).ConfigureAwait(false);
        await tls.WriteAsync(challenge, ct).ConfigureAwait(false);
        var ack = new byte[32];
        await tls.ReadExactlyAsync(ack, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(challenge), ack))
            throw new AuthenticationException("Challenge acknowledgement did not match.");
        await tls.WriteAsync(FinalConfirmation, ct).ConfigureAwait(false);
        return tls.SslProtocol;
    }

    public static async Task<SslProtocols> ClientAsync(IPAddress local, IPAddress peer, int port,
        X509Certificate2 own, string expectedHost, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireInputs(local, peer, own, expectedHost);
        using var socket = new TcpClient(AddressFamily.InterNetwork);
        socket.Client.Bind(new IPEndPoint(local, 0));
        await socket.ConnectAsync(peer, port, ct).ConfigureAwait(false);
        await using var tls = new SslStream(socket.GetStream(), false,
            (_, certificate, _, _) => CertificatePin.Matches(expectedHost, certificate), (_, _, _, _, _) => own);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "nian-synthetic-tls-probe", ClientCertificates = new X509CertificateCollection { own },
            EnabledSslProtocols = Versions, EncryptionPolicy = EncryptionPolicy.RequireEncryption, AllowTlsResume = false
        }, ct).ConfigureAwait(false);
        RequireAuthenticated(tls);
        var challenge = RandomNumberGenerator.GetBytes(32);
        await tls.WriteAsync(challenge, ct).ConfigureAwait(false);
        var echo = new byte[32];
        await tls.ReadExactlyAsync(echo, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(challenge, echo))
            throw new AuthenticationException("Challenge did not match.");
        await tls.WriteAsync(SHA256.HashData(challenge), ct).ConfigureAwait(false);
        var confirmation = new byte[FinalConfirmation.Length];
        await tls.ReadExactlyAsync(confirmation, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(FinalConfirmation, confirmation))
            throw new AuthenticationException("Final confirmation did not match.");
        return tls.SslProtocol;
    }

    private static void RequireInputs(IPAddress local, IPAddress peer, X509Certificate2 own, string pin)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || peer.AddressFamily != AddressFamily.InterNetwork ||
            !own.HasPrivateKey || !CertificatePin.IsValid(pin)) throw new ArgumentException("Invalid synthetic TLS probe input.");
    }

    private static void RequireAuthenticated(SslStream tls)
    {
        if (!tls.IsAuthenticated || !tls.IsEncrypted || !tls.IsMutuallyAuthenticated || tls.RemoteCertificate is null ||
            tls.SslProtocol is not (SslProtocols.Tls12 or SslProtocols.Tls13))
            throw new AuthenticationException("Mutual TLS was not established.");
    }
}
