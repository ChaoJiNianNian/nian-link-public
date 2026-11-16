using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MacSyntheticMtlsProbe;

namespace MacSyntheticMtlsProbe.Tests;

public sealed class SyntheticMtlsTests
{
    [Fact]
    public async Task CancellationDoesNotWaitForAReaderWhoseSynchronousReadIsBlocked()
    {
        using var reader = new BlockingReader();
        using var cancellation = new CancellationTokenSource();
        var pending = ProbeInput.ReadPinAsync(reader, cancellation.Token);
        Assert.True(reader.Entered.Wait(TimeSpan.FromSeconds(3)));
        cancellation.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2))); }
        finally { reader.Release.Set(); Assert.True(reader.Exited.Wait(TimeSpan.FromSeconds(3))); }
    }

    private sealed class BlockingReader : TextReader
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public ManualResetEventSlim Exited { get; } = new();
        public override string? ReadLine() { Entered.Set(); try { Release.Wait(); return null; } finally { Exited.Set(); } }
        protected override void Dispose(bool disposing) { if (disposing) { Release.Set(); Entered.Dispose(); Release.Dispose(); Exited.Dispose(); } base.Dispose(disposing); }
    }

    [Theory]
    [InlineData("server")]
    [InlineData("client")]
    public void ExplicitRunRequiresTwoDifferentPrivateAddressesAndHighPort(string role)
    {
        Assert.NotNull(ProbeOptions.Parse(["--run", role, "192.168.42.2", "192.168.42.3", "54321"]));
        Assert.ThrowsAny<ArgumentException>(() => ProbeOptions.Parse([role, "192.168.42.2", "192.168.42.3", "54321"]));
        Assert.ThrowsAny<ArgumentException>(() => ProbeOptions.Parse(["--run", role, "192.168.42.2", "192.168.42.3"]));
        Assert.ThrowsAny<ArgumentException>(() => ProbeOptions.Parse(["--run", role, "192.168.42.2", "192.168.42.2", "54321"]));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.3")]
    [InlineData("8.8.8.8")]
    [InlineData("::1")]
    [InlineData("not-an-ip")]
    public void PublicLoopbackUnspecifiedAndMalformedAddressesAreRejectedByCli(string address)
    {
        Assert.ThrowsAny<ArgumentException>(() => ProbeOptions.Parse(["--run", "server", address, "192.168.42.3", "54321"]));
        Assert.ThrowsAny<ArgumentException>(() => ProbeOptions.Parse(["--run", "client", "192.168.42.2", address, "54321"]));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1023")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("xyz")]
    [InlineData("49473")]
    [InlineData("24800")]
    [InlineData("24801")]
    [InlineData("24802")]
    public void InvalidAndProductDiscoveryPortsAreRejected(string port) =>
        Assert.ThrowsAny<ArgumentException>(() => ProbeOptions.Parse(["--run", "server", "192.168.42.2", "192.168.42.3", port]));

    [Fact]
    public void SyntheticCertificatesHaveDifferentPinsAndOwnPrivateKeys()
    {
        using var first = SyntheticCertificate.Create();
        using var second = SyntheticCertificate.Create();
        Assert.True(first.HasPrivateKey);
        Assert.True(second.HasPrivateKey);
        Assert.NotEqual(CertificatePin.Get(first), CertificatePin.Get(second));
        Assert.True(CertificatePin.Matches(CertificatePin.Get(first), first));
        Assert.False(CertificatePin.Matches(CertificatePin.Get(first), second));
        Assert.False(CertificatePin.Matches(new string('0', 64), first));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void InvalidCertificatePinsCannotMatch(string pin)
    {
        using var certificate = SyntheticCertificate.Create();
        Assert.False(CertificatePin.Matches(pin, certificate));
    }

    [Fact]
    public async Task RealLoopbackMutualTlsExchangesChallengeWithPinnedPeers()
    {
        using var host = SyntheticCertificate.Create();
        using var peer = SyntheticCertificate.Create();
        var (server, client) = await RunPair(host, peer, CertificatePin.Get(host), CertificatePin.Get(peer));
        Assert.Contains(server, new[] { SslProtocols.Tls12, SslProtocols.Tls13 });
        Assert.Equal(server, client);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EitherWrongPeerPinRejectsTheFullMutualSession(bool wrongHostPin)
    {
        using var host = SyntheticCertificate.Create();
        using var peer = SyntheticCertificate.Create();
        using var other = SyntheticCertificate.Create();
        var hostPin = CertificatePin.Get(wrongHostPin ? other : host);
        var peerPin = CertificatePin.Get(wrongHostPin ? peer : other);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var port = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ProbeSession.ServerAsync(IPAddress.Loopback, IPAddress.Loopback, 0, host, peerPin,
            p => port.TrySetResult(p), deadline.Token);
        var portNumber = await port.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var client = ProbeSession.ClientAsync(IPAddress.Loopback, IPAddress.Loopback, portNumber, peer, hostPin, deadline.Token);
        try
        {
            var rejected = wrongHostPin ? client : server;
            await Assert.ThrowsAsync<AuthenticationException>(async () => await rejected.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            deadline.Cancel();
            try { await Task.WhenAll(server, client).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    [Fact]
    public async Task ClientWithoutPrivateKeyCannotAuthenticate()
    {
        using var host = SyntheticCertificate.Create();
        using var peer = SyntheticCertificate.Create();
        using var publicOnly = X509CertificateLoader.LoadCertificate(peer.RawData);
        Assert.False(publicOnly.HasPrivateKey);
        await Assert.ThrowsAnyAsync<Exception>(() => RunPair(host, publicOnly, CertificatePin.Get(host), CertificatePin.Get(peer)));
    }

    [Fact]
    public async Task CancelledListenerDoesNotStartOrReturnSuccess()
    {
        using var host = SyntheticCertificate.Create();
        using var peer = SyntheticCertificate.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var listening = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProbeSession.ServerAsync(
            IPAddress.Loopback, IPAddress.Loopback, 0, host, CertificatePin.Get(peer), _ => listening = true, cancellation.Token));
        Assert.False(listening);
    }

    [Fact]
    public async Task CancellingAnActiveHandshakeStopsAndReleasesListener()
    {
        using var host = SyntheticCertificate.Create();
        using var peer = SyntheticCertificate.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var port = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ProbeSession.ServerAsync(IPAddress.Loopback, IPAddress.Loopback, 0, host, CertificatePin.Get(peer),
            p => port.TrySetResult(p), deadline.Token);
        var portNumber = await port.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var plainClient = new TcpClient(AddressFamily.InterNetwork);
        await plainClient.ConnectAsync(IPAddress.Loopback, portNumber, deadline.Token);
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await server.WaitAsync(TimeSpan.FromSeconds(3)));
        using var after = new TcpListener(IPAddress.Loopback, portNumber);
        after.Start();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientRejectsMissingOrIncorrectFinalConfirmation(bool sendIncorrect)
    {
        using var host = SyntheticCertificate.Create();
        using var peer = SyntheticCertificate.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var badServer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var tls = new SslStream(socket.GetStream(), false,
                (_, certificate, _, _) => CertificatePin.Matches(CertificatePin.Get(peer), certificate));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = host, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption, AllowTlsResume = false
            }, deadline.Token);
            var challenge = new byte[32];
            await tls.ReadExactlyAsync(challenge, deadline.Token);
            await tls.WriteAsync(challenge, deadline.Token);
            var ack = new byte[32];
            await tls.ReadExactlyAsync(ack, deadline.Token);
            Assert.Equal(SHA256.HashData(challenge), ack);
            if (sendIncorrect) await tls.WriteAsync("NOT-OK!!"u8.ToArray(), deadline.Token);
        });
        var client = ProbeSession.ClientAsync(IPAddress.Loopback, IPAddress.Loopback, port, peer, CertificatePin.Get(host), deadline.Token);
        try
        {
            var error = await Record.ExceptionAsync(async () => await client.WaitAsync(TimeSpan.FromSeconds(6)));
            Assert.True(error is EndOfStreamException or AuthenticationException, $"Expected final-confirmation rejection, got {error?.GetType().Name ?? "success"}.");
            await badServer.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            deadline.Cancel();
            try { await badServer.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private static async Task<(SslProtocols Server, SslProtocols Client)> RunPair(
        X509Certificate2 host, X509Certificate2 peer, string expectedHost, string expectedPeer)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var port = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ProbeSession.ServerAsync(IPAddress.Loopback, IPAddress.Loopback, 0, host, expectedPeer,
            p => port.TrySetResult(p), deadline.Token);
        var portNumber = await port.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var client = ProbeSession.ClientAsync(IPAddress.Loopback, IPAddress.Loopback, portNumber, peer, expectedHost, deadline.Token);
        try { await Task.WhenAll(server, client).WaitAsync(TimeSpan.FromSeconds(9)); }
        finally
        {
            deadline.Cancel();
            try { await Task.WhenAll(server, client).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        return (await server, await client);
    }
}
