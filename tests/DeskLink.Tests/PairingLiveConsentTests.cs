using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingLiveConsentTests
{
    private sealed class LivePair : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly TcpClient client, accepted;
        internal SslStream Host { get; }
        internal SslStream Peer { get; }
        private LivePair(TcpListener listener, TcpClient client, TcpClient accepted, SslStream host, SslStream peer)
        { this.listener = listener; this.client = client; this.accepted = accepted; Host = host; Peer = peer; }
        internal static async Task<LivePair> OpenAsync(X509Certificate2 hostCert, X509Certificate2 peerCert)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
            var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token).AsTask();
                var accepted = await listener.AcceptTcpClientAsync(timeout.Token); await connecting;
                var host = new SslStream(accepted.GetStream(), false, (_, cert, _, _) => Matches(peerCert, cert));
                var peer = new SslStream(client.GetStream(), false, (_, cert, _, _) => Matches(hostCert, cert), (_, _, _, _, _) => peerCert);
                try
                {
                    await Task.WhenAll(
                        host.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = hostCert, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token),
                        peer.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "pairing-test", ClientCertificates = new X509CertificateCollection { peerCert }, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token));
                    return new(listener, client, accepted, host, peer);
                }
                catch { await host.DisposeAsync(); await peer.DisposeAsync(); accepted.Dispose(); throw; }
            }
            catch { client.Dispose(); listener.Stop(); throw; }
        }
        private static bool Matches(X509Certificate2 expected, X509Certificate? actual) => actual is not null &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected.RawData), SHA256.HashData(actual.GetRawCertData()));
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); await Peer.DisposeAsync(); accepted.Dispose(); client.Dispose(); listener.Stop(); }
    }

    private static async Task<(X509Certificate2 Host, X509Certificate2 Peer)> CertificatesAsync(TestFolder folder)
    {
        var host = Path.Combine(folder.Path, "host.pem"); var peer = Path.Combine(folder.Path, "peer.pem");
        await PrivateFiles.EnsureCertificateAsync(host, CancellationToken.None); await PrivateFiles.EnsureCertificateAsync(peer, CancellationToken.None);
        return (PrivateFiles.LoadTlsCertificate(host), PrivateFiles.LoadTlsCertificate(peer));
    }
    private static (PairingSessionIdentity Identity, PeerIdentityOffer Offer) Proposal(X509Certificate2 host, X509Certificate2 peer, DateTimeOffset now)
    {
        var offerNonce = Guid.NewGuid();
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peer), Nonce = offerNonce.ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), offerNonce, Guid.NewGuid(), "host", "peer", DeskLinkFingerprint.OfCertificate(host), DeskLinkFingerprint.OfCertificate(peer), now.AddMinutes(1));
        return (identity, offer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DifferentOfferPlatformOrOfferDeadlineCannotReachConfirmation(bool changePlatform)
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            var changed = changePlatform ? offer with { Platform = "mac" } : offer with { ExpiresAtUtc = offer.ExpiresAtUtc.AddSeconds(1) };
            {
                await using var pair = await LivePair.OpenAsync(hostCert, peerCert);
                await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
                await using var peer = new PairingTlsConsentSession(pair.Peer, identity, changed, false, () => now);
                var hostBegin = host.BeginAsync(); var peerBegin = peer.BeginAsync();
                await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(hostBegin, peerBegin));
                var failures = new[] { hostBegin.Exception, peerBegin.Exception }.Where(x => x is not null)
                    .SelectMany(x => x!.Flatten().InnerExceptions).ToArray();
                Assert.Contains(failures, error => error is InvalidDataException);
                Assert.NotEqual(PairingConsentPhase.ReadyToPrepare, host.Phase);
                Assert.NotEqual(PairingConsentPhase.ReadyToPrepare, peer.Phase);
            }
        }
    }

    [Fact]
    public async Task EquivalentNormalizedOfferValuesCanCompleteBothConfirmations()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        await using (var pair = await LivePair.OpenAsync(hostCert, peerCert))
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            var equivalent = offer with
            {
                KeyboardFingerprint = string.Join(":", Enumerable.Range(0, 32).Select(i => offer.KeyboardFingerprint.Substring(i * 2, 2).ToLowerInvariant())),
                Nonce = offer.Nonce.ToUpperInvariant(),
                ExpiresAtUtc = offer.ExpiresAtUtc.ToOffset(TimeSpan.FromHours(8))
            };
            await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
            await using var peer = new PairingTlsConsentSession(pair.Peer, identity, equivalent, false, () => now);
            await Task.WhenAll(host.BeginAsync(), peer.BeginAsync());
            await host.ConfirmLocalAsync(); await peer.ReceiveRemoteConfirmationAsync();
            await peer.ConfirmLocalAsync(); await host.ReceiveRemoteConfirmationAsync();
            Assert.Equal(PairingConsentPhase.ReadyToPrepare, host.Phase);
            Assert.Equal(PairingConsentPhase.ReadyToPrepare, peer.Phase);
        }
    }

    [Fact]
    public async Task OldVersionHelloIsRejectedEvenWhenItContainsTheFullOffer()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        await using (var pair = await LivePair.OpenAsync(hostCert, peerCert))
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
            var begin = host.BeginAsync();
            Assert.NotNull(await JsonLineProtocol.ReadAsync(pair.Peer));
            await JsonLineProtocol.WriteAsync(pair.Peer, new { version = 1, type = "hello", role = "peer", identity, offer, challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
            await Assert.ThrowsAsync<InvalidDataException>(() => begin);
            Assert.Equal(PairingConsentPhase.Aborted, host.Phase);
        }
    }

    [Fact]
    public async Task ReorderedOfferFieldsAreAcceptedWithoutChangingItsMeaning()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        await using (var pair = await LivePair.OpenAsync(hostCert, peerCert))
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
            var begin = host.BeginAsync();
            Assert.NotNull(await JsonLineProtocol.ReadAsync(pair.Peer));
            var reordered = new { expiresAtUtc = offer.ExpiresAtUtc, nonce = offer.Nonce, keyboardFingerprint = offer.KeyboardFingerprint,
                platform = offer.Platform, name = offer.Name, version = offer.Version };
            await JsonLineProtocol.WriteAsync(pair.Peer, new { version = 2, type = "hello", role = "peer", identity, offer = reordered, challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
            await begin;
            Assert.Equal(PairingConsentPhase.AwaitingConfirmation, host.Phase);
        }
    }

    [Fact]
    public async Task MissingDuplicateExtraInvalidVersionAndExpiredOfferFailClosed()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            string Hello(PeerIdentityOffer value) => JsonSerializer.Serialize(new { version = 2, type = "hello", role = "peer", identity, offer = value, challenge }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var valid = Hello(offer);
            var bad = new[]
            {
                JsonSerializer.Serialize(new { version = 2, type = "hello", role = "peer", identity, challenge }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                valid.Replace("\"platform\":\"windows\",", "", StringComparison.Ordinal),
                valid.Replace("\"platform\":\"windows\"", "\"platform\":\"windows\",\"platform\":\"windows\"", StringComparison.Ordinal),
                valid.Replace("\"platform\":\"windows\"", "\"platform\":\"windows\",\"Platform\":\"windows\"", StringComparison.Ordinal),
                valid.Replace("\"platform\":\"windows\"", "\"platform\":\"windows\",\"extra\":true", StringComparison.Ordinal),
                valid.Replace("\"hostName\":\"host\"", "\"hostName\":\"host\",\"hostName\":\"host\"", StringComparison.Ordinal),
                valid.Replace("\"offer\":{\"version\":1", "\"offer\":{\"version\":2", StringComparison.Ordinal),
                Hello(offer with { ExpiresAtUtc = now.AddSeconds(-1) }),
                Hello(offer with { Name = "other" }),
                Hello(offer with { Nonce = Guid.NewGuid().ToString("N") }),
                Hello(offer with { KeyboardFingerprint = new string('A', 64) })
            };
            foreach (var frame in bad)
            {
                Assert.NotEqual(valid, frame);
                await using var pair = await LivePair.OpenAsync(hostCert, peerCert);
                await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
                var begin = host.BeginAsync();
                Assert.NotNull(await JsonLineProtocol.ReadAsync(pair.Peer));
                await pair.Peer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(frame + "\n")); await pair.Peer.FlushAsync();
                await Assert.ThrowsAsync<InvalidDataException>(() => begin);
                Assert.Equal(PairingConsentPhase.Aborted, host.Phase);
            }
        }
    }

    [Fact]
    public async Task LiveMutualTlsRequiresExplicitConfirmationFromEachSide()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        await using (var pair = await LivePair.OpenAsync(hostCert, peerCert))
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
            await using var peer = new PairingTlsConsentSession(pair.Peer, identity, offer, false, () => now);
            await Task.WhenAll(host.BeginAsync(), peer.BeginAsync());
            Assert.Equal(PairingConsentPhase.AwaitingConfirmation, host.Phase);
            await host.ConfirmLocalAsync(); await peer.ReceiveRemoteConfirmationAsync();
            Assert.Equal(PairingConsentPhase.AwaitingConfirmation, host.Phase);
            Assert.Equal(PairingConsentPhase.AwaitingConfirmation, peer.Phase);
            await peer.ConfirmLocalAsync(); await host.ReceiveRemoteConfirmationAsync();
            Assert.Equal(PairingConsentPhase.ReadyToPrepare, host.Phase);
            Assert.Equal(PairingConsentPhase.ReadyToPrepare, peer.Phase);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.ConfirmLocalAsync());
        }
    }

    [Fact]
    public async Task RejectsMismatchedActualCertificatesAndOfferExpiryOrIdentity()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        await using (var pair = await LivePair.OpenAsync(hostCert, peerCert))
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            Assert.Throws<ArgumentException>(() => new PairingTlsConsentSession(pair.Host, identity with { PeerFingerprint = new string('A', 64) }, offer, true, () => now));
            var wrongPeer = new string('B', 64);
            Assert.Throws<ArgumentException>(() => new PairingTlsConsentSession(pair.Host, identity with { PeerFingerprint = wrongPeer }, offer with { KeyboardFingerprint = wrongPeer }, true, () => now));
            Assert.Throws<ArgumentException>(() => new PairingTlsConsentSession(pair.Host, identity, offer with { Nonce = Guid.NewGuid().ToString("N") }, true, () => now));
            Assert.Throws<ArgumentException>(() => new PairingTlsConsentSession(pair.Host, identity, offer with { Name = "other" }, true, () => now));
            Assert.Throws<ArgumentException>(() => new PairingTlsConsentSession(pair.Host, identity with { ExpiresAtUtc = offer.ExpiresAtUtc.AddSeconds(1) }, offer, true, () => now));
            Assert.True(pair.Host.IsAuthenticated); // Ownership remains with the caller after rejected construction.
        }
    }

    [Fact]
    public async Task PeerDisconnectAndExpiredConfirmationCannotLeaveReadyConsent()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            await using (var pair = await LivePair.OpenAsync(hostCert, peerCert))
            {
                await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
                await using var peer = new PairingTlsConsentSession(pair.Peer, identity, offer, false, () => now);
                await Task.WhenAll(host.BeginAsync(), peer.BeginAsync());
                await host.ConfirmLocalAsync();
                await peer.DisposeAsync();
                await Assert.ThrowsAnyAsync<IOException>(() => host.ReceiveRemoteConfirmationAsync());
                Assert.Equal(PairingConsentPhase.Aborted, host.Phase);
            }
            await using (var pair = await LivePair.OpenAsync(hostCert, peerCert))
            {
                await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
                await using var peer = new PairingTlsConsentSession(pair.Peer, identity, offer, false, () => now);
                await Task.WhenAll(host.BeginAsync(), peer.BeginAsync());
                await host.ConfirmLocalAsync();
                now = identity.ExpiresAtUtc;
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReceiveRemoteConfirmationAsync());
                Assert.Equal(PairingConsentPhase.Aborted, host.Phase);
                Assert.False(pair.Host.IsAuthenticated);
            }
        }
    }

    [Fact]
    public async Task DuplicateOrUnexpectedFieldsFailClosedBeforeAnyRemoteConsent()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        {
          await using (var pair = await LivePair.OpenAsync(hostCert, peerCert))
          {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
            var begin = host.BeginAsync();
            Assert.NotNull(await JsonLineProtocol.ReadAsync(pair.Peer));
            await JsonLineProtocol.WriteAsync(pair.Peer, new { version = 2, type = "hello", role = "peer", identity, offer, challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), unexpected = true });
            await Assert.ThrowsAsync<InvalidDataException>(() => begin);
            Assert.Equal(PairingConsentPhase.Aborted, host.Phase);
          }
          await using (var pair = await LivePair.OpenAsync(hostCert, peerCert))
          {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
            await using var peer = new PairingTlsConsentSession(pair.Peer, identity, offer, false, () => now);
            await Task.WhenAll(host.BeginAsync(), peer.BeginAsync());
            await host.ConfirmLocalAsync();
            var duplicate = System.Text.Encoding.UTF8.GetBytes("{\"version\":2,\"version\":2,\"type\":\"confirm\",\"role\":\"peer\"}\n");
            await pair.Peer.WriteAsync(duplicate); await pair.Peer.FlushAsync();
            await Assert.ThrowsAsync<InvalidDataException>(() => host.ReceiveRemoteConfirmationAsync());
            Assert.Equal(PairingConsentPhase.Aborted, host.Phase);
          }
        }
    }

    [Fact]
    public void OnlyModernTlsVersionsCanBeAccepted()
    {
        Assert.True(PairingTlsConsentSession.IsSupportedProtocol(SslProtocols.Tls12));
        Assert.True(PairingTlsConsentSession.IsSupportedProtocol(SslProtocols.Tls13));
        Assert.False(PairingTlsConsentSession.IsSupportedProtocol(SslProtocols.None));
        Assert.False(PairingTlsConsentSession.IsSupportedProtocol((SslProtocols)192));
        Assert.False(PairingTlsConsentSession.IsSupportedProtocol(SslProtocols.Tls12 | SslProtocols.Tls13));
    }

    [Fact]
    public async Task IdleDeadlineClosesTransportWithoutAnotherMethodCall()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        await using (var pair = await LivePair.OpenAsync(hostCert, peerCert))
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            identity = identity with { ExpiresAtUtc = now.AddMilliseconds(300) };
            await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true);
            await using var peer = new PairingTlsConsentSession(pair.Peer, identity, offer, false);
            await Task.WhenAll(host.BeginAsync(), peer.BeginAsync());
            await Task.Delay(800);
            Assert.False(pair.Host.IsAuthenticated);
            Assert.False(pair.Peer.IsAuthenticated);
            Assert.Equal(PairingConsentPhase.Aborted, host.Phase);
        }
    }

    [Fact]
    public async Task IdleClockJumpForwardOrBackwardInvalidatesAndClosesTransport()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        {
            foreach (var forward in new[] { true, false })
            {
                await using var pair = await LivePair.OpenAsync(hostCert, peerCert);
                var start = DateTimeOffset.UtcNow; long utcTicks = start.UtcTicks;
                DateTimeOffset Clock() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
                var (identity, offer) = Proposal(hostCert, peerCert, start);
                await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, Clock);
                await using var peer = new PairingTlsConsentSession(pair.Peer, identity, offer, false, Clock);
                await Task.WhenAll(host.BeginAsync(), peer.BeginAsync());
                Interlocked.Exchange(ref utcTicks, (forward ? identity.ExpiresAtUtc.AddSeconds(1) : start.AddSeconds(-1)).UtcTicks);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (pair.Host.IsAuthenticated || pair.Peer.IsAuthenticated) await Task.Delay(25, timeout.Token);
                Assert.Equal(PairingConsentPhase.Aborted, host.Phase);
            }
        }
    }

    [Fact]
    public async Task DisposeWaitsForThePendingReadToEnd()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        await using (var pair = await LivePair.OpenAsync(hostCert, peerCert))
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
            await using var peer = new PairingTlsConsentSession(pair.Peer, identity, offer, false, () => now);
            await Task.WhenAll(host.BeginAsync(), peer.BeginAsync());
            var waiting = host.ReceiveRemoteConfirmationAsync();
            Assert.False(waiting.IsCompleted);
            await host.DisposeAsync();
            Assert.True(waiting.IsCompleted);
            Assert.True(waiting.IsFaulted || waiting.IsCanceled);
            Assert.Equal(PairingConsentPhase.Aborted, host.Phase);
        }
    }

    [Fact]
    public async Task BadUtf8OversizedFrameWrongRoleAndMalformedChallengeAreRejected()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            var badFrames = new[]
            {
                new byte[] { 0xff, (byte)'\n' },
                Enumerable.Repeat((byte)'x', 4097).Append((byte)'\n').ToArray(),
                System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 2, type = "hello", role = "host", identity, offer, challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n"),
                System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 2, type = "hello", role = "peer", identity, offer, challenge = "invalid" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n")
            };
            foreach (var frame in badFrames)
            {
                await using var pair = await LivePair.OpenAsync(hostCert, peerCert);
                await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
                var begin = host.BeginAsync();
                Assert.NotNull(await JsonLineProtocol.ReadAsync(pair.Peer));
                await pair.Peer.WriteAsync(frame); await pair.Peer.FlushAsync();
                await Assert.ThrowsAsync<InvalidDataException>(() => begin);
                Assert.Equal(PairingConsentPhase.Aborted, host.Phase);
                Assert.False(pair.Host.IsAuthenticated);
            }
        }
    }

    [Fact]
    public async Task ClosingOrCancellingStopsWaitingAndInvalidatesConsent()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        await using (var pair = await LivePair.OpenAsync(hostCert, peerCert))
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            await using var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
            await using var peer = new PairingTlsConsentSession(pair.Peer, identity, offer, false, () => now);
            await Task.WhenAll(host.BeginAsync(), peer.BeginAsync());
            await host.ConfirmLocalAsync();
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ReceiveRemoteConfirmationAsync(cancel.Token));
            Assert.Equal(PairingConsentPhase.Aborted, host.Phase);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.ConfirmLocalAsync());
        }
    }

    [Fact]
    public async Task OldConfirmationCannotBeInjectedIntoAnotherLiveTlsConnection()
    {
        using var folder = new TestFolder(); var (hostCert, peerCert) = await CertificatesAsync(folder);
        using (hostCert) using (peerCert)
        await using (var first = await LivePair.OpenAsync(hostCert, peerCert))
        await using (var second = await LivePair.OpenAsync(hostCert, peerCert))
        {
            var now = DateTimeOffset.UtcNow; var (identity, offer) = Proposal(hostCert, peerCert, now);
            async Task<(PairingTlsConsentSession Host, string CapturedConfirm)> PrepareManualPeerAsync(LivePair pair)
            {
                var host = new PairingTlsConsentSession(pair.Host, identity, offer, true, () => now);
                var begin = host.BeginAsync();
                var hello = await JsonLineProtocol.ReadAsync(pair.Peer) ?? throw new InvalidDataException();
                using var parsed = JsonDocument.Parse(hello);
                var hostChallenge = parsed.RootElement.GetProperty("challenge").GetString();
                var peerChallenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                await JsonLineProtocol.WriteAsync(pair.Peer, new { version = 2, type = "hello", role = "peer", identity, offer, challenge = peerChallenge });
                await begin;
                var forgedPeerConfirm = JsonSerializer.Serialize(new { version = 2, type = "confirm", role = "peer", sessionId = identity.SessionId, offerNonce = identity.OfferNonce, transactionId = identity.TransactionId, senderChallenge = peerChallenge, receiverChallenge = hostChallenge }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return (host, forgedPeerConfirm);
            }
            var old = await PrepareManualPeerAsync(first); var current = await PrepareManualPeerAsync(second);
            await using var oldHost = old.Host; await using var currentHost = current.Host;
            await currentHost.ConfirmLocalAsync();
            var bytes = System.Text.Encoding.UTF8.GetBytes(old.CapturedConfirm + "\n");
            await second.Peer.WriteAsync(bytes); await second.Peer.FlushAsync();
            await Assert.ThrowsAsync<InvalidDataException>(() => currentHost.ReceiveRemoteConfirmationAsync());
            Assert.Equal(PairingConsentPhase.Aborted, currentHost.Phase);
        }
    }
}
