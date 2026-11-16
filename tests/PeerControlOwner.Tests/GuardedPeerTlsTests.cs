using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.MultiDevice;

namespace PeerControlOwnerTests;

public sealed class GuardedPeerTlsTests
{
    [Fact]
    public async Task LateBeginOverRealTlsCannotUnlockReplacementConnection()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=nian-desklink", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var pfx = generated.Export(X509ContentType.Pkcs12);
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        CryptographicOperations.ZeroMemory(pfx);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var tls = new SslStream(socket.GetStream(), false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
            using var frame = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(tls, CancellationToken.None))!);
            Assert.Equal(token, frame.RootElement.GetProperty("token").GetString());
            Assert.Equal("preview-begin", frame.RootElement.GetProperty("action").GetString());
            entered.TrySetResult();
            await release.Task;
            applied.TrySetResult(); // Synthetic delayed action, no real keyboard.
        });
        var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            HostName = "host", PeerName = "peer", Token = token,
            CertificateSha256 = Convert.ToHexString(SHA256.HashData(certificate.RawData)) };
        await using var client = new MultiPeerControlClient(invitation, true);
        var backend = new TlsBackend(client);
        var owner = new PeerControlOwner(); owner.Bind(backend);
        var control = Wrap(owner, backend);
        using var cancel = new CancellationTokenSource();
        try
        {
            var pending = control.BeginPreviewAsync(cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            owner.Retire(backend);
            var replacement = new TlsBackend(client); owner.Bind(replacement);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Wrap(owner, replacement).BeginPreviewAsync());
            release.TrySetResult();
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Wrap(owner, replacement).BeginPreviewAsync());
        }
        finally { release.TrySetResult(); listener.Stop(); await server; }
    }

    private static GuardedPeerControl Wrap(PeerControlOwner owner, TlsBackend backend) =>
        new(owner, backend, (_, _) => throw new InvalidOperationException("unused"), "host", "peer");

    private sealed class TlsBackend(MultiPeerControlClient client) : IDeskflowPreviewControl
    {
        public bool PreviewHandoffSupported => true;
        public string PreviewReturnShortcut => "fixture";
        public string PreviewHandoffHint => "fixture";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) => client.BeginPreviewAsync(ct);
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => client.RenewPreviewAsync(id, ct);
        public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default) => client.EndPreviewAsync(id, ct);
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) => client.ReadPreviewAsync(ct);
    }
}
