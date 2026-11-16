using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DesktopToolbox.DeskLink;
using NianLink.CandidateDiscovery;

namespace FilelessCandidateIntegration.Tests;

public sealed class HighLevelPairingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitIsolatedCandidateResolvesInterruptedPairingThroughBothHighLevelApis(bool peerAbsent)
    {
        var address = VerifiedLanBinding.GetEligibleAddresses().FirstOrDefault()
            ?? throw new InvalidOperationException("An active private LAN interface is needed for this isolated integration test.");
        var lan = VerifiedLanBinding.VerifyCurrentNetwork(address, true);
        var network = DiscoveryNetworkBinding.CreateForCurrentNetwork(lan);
        var workRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "high-level-fixtures", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(workRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var hostPem = Path.Combine(workRoot, "host.pem"); var peerPem = Path.Combine(workRoot, "peer.pem");
        await PrivateFiles.EnsureCertificateAsync(hostPem, timeout.Token);
        await PrivateFiles.EnsureCertificateAsync(peerPem, timeout.Token);
        using var hostCertificate = PrivateFiles.LoadTlsCertificate(hostPem);
        using var peerCertificate = PrivateFiles.LoadTlsCertificate(peerPem);
        var protector = new TestProtector();
        var vaultPath = Path.Combine(workRoot, "vault"); var storePath = Path.Combine(workRoot, "peer");
        string oldToken;
        {
            var (offer, identity) = CreateIdentity(hostCertificate, peerCertificate);
            using var listener = new TcpListener(address, 0); listener.Start(1);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(address, 0));
            var connecting = socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var accepted = await listener.AcceptSocketAsync(timeout.Token); await connecting;
            var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, hostCertificate, identity, offer, timeout.Token);
            var peerTask = PairingAuthenticatedChannel.StartPeerAsync(socket, peerCertificate, identity, offer, timeout.Token);
            await Task.WhenAll(hostTask, peerTask);
            await using var host = await hostTask; await using var peer = await peerTask;
            await Task.WhenAll(host.ConfirmLocalAsync(timeout.Token), peer.ReceiveRemoteConfirmationAsync(timeout.Token));
            await Task.WhenAll(peer.ConfirmLocalAsync(timeout.Token), host.ReceiveRemoteConfirmationAsync(timeout.Token));
            using var hostDelivery = host.BeginDelivery(); using var peerDelivery = peer.BeginDelivery();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector, timeout.Token);
            await using var store = await PeerInvitationStore.OpenAsync(storePath, protector, timeout.Token);
            oldToken = await vault.AddConfirmedAsync("oldpeer", new string('A', 64), timeout.Token);
            await hostDelivery.StageHostGrantAsync(vault, timeout.Token);
            await hostDelivery.FreezeHostInvitationAsync(lan, 24821, 24800, 24822, timeout.Token);
            if (peerAbsent)
            {
                var sent = hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token);
                var marker = Path.Combine(vaultPath, "pending-prepare.bin");
                while (!File.Exists(marker)) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(10, timeout.Token); }
                socket.Dispose();
                await Assert.ThrowsAnyAsync<Exception>(() => sent.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                await Task.WhenAll(hostDelivery.SendPrepareAndAwaitReadyAsync(timeout.Token), peerDelivery.ReceivePrepareAndStageAsync(store, timeout.Token));
                await hostDelivery.CommitVerifiedReadyAsync(timeout.Token);
                socket.Dispose();
            }
        }
        await using var reopenedVault = await MultiDeviceCredentialVault.OpenAsync(vaultPath, protector, timeout.Token);
        await using var reopenedStore = await PeerInvitationStore.OpenAsync(storePath, protector, timeout.Token);
        Assert.Equal(peerAbsent ? PairingRecoveryPhase.AwaitingPeer : PairingRecoveryPhase.CommittedAwaitingPeer,
            (await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        var (freshOffer, freshIdentity) = CreateIdentity(hostCertificate, peerCertificate);
        using var freshListener = new TcpListener(address, 0); freshListener.Start(1);
        using var freshSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        freshSocket.Bind(new IPEndPoint(address, 0));
        var freshConnecting = freshSocket.ConnectAsync((IPEndPoint)freshListener.LocalEndpoint, timeout.Token);
        using var freshAccepted = await freshListener.AcceptSocketAsync(timeout.Token); await freshConnecting;
        var freshHostTask = PairingAuthenticatedChannel.StartHostAsync(freshAccepted, hostCertificate, freshIdentity, freshOffer, timeout.Token);
        var freshPeerTask = PairingAuthenticatedChannel.StartPeerAsync(freshSocket, peerCertificate, freshIdentity, freshOffer, timeout.Token);
        await Task.WhenAll(freshHostTask, freshPeerTask);
        await using var runtime = MultiPeerRuntime.CreateForLan(reopenedVault, Path.Combine(workRoot, "runtime"), "host", lan,
            24821, 24822, Path.Combine(workRoot, "not-started-engine.exe"), MultiPeerKeyboardSession.ExperimentalEngineSha256);
        var pairing = new MultiPeerPairingCoordinator(reopenedVault, "host", address.ToString(), 24821, 24822, 24800, freshIdentity.HostFingerprint);
        var lease = DiscoveryReviewLease.Start(network);
        await using var secureHost = Construct<SecurePairingHost>(await freshHostTask, lease,
            CancellationTokenSource.CreateLinkedTokenSource(timeout.Token), Task.CompletedTask, PrivateFiles.LoadTlsCertificate(hostPem), runtime);
        await using var securePeer = Construct<SecurePairingPeer>(await freshPeerTask, new TestNetwork(network),
            CancellationTokenSource.CreateLinkedTokenSource(timeout.Token), PrivateFiles.LoadTlsCertificate(peerPem));
        await Task.WhenAll(secureHost.ConfirmAsync(timeout.Token), securePeer.WaitForRemoteConfirmationAsync(timeout.Token));
        await Task.WhenAll(securePeer.ConfirmAsync(timeout.Token), secureHost.WaitForRemoteConfirmationAsync(timeout.Token));
        var resumedHost = secureHost.RecoverHistoricalPairingAsync(pairing, timeout.Token);
        var resumedPeer = securePeer.AcceptPairingOperationAsync(reopenedStore, timeout.Token);
        await Task.WhenAll(resumedHost, resumedPeer).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(!peerAbsent, await resumedPeer);
        var result = await resumedHost;
        if (peerAbsent) { Assert.Null(result); Assert.Null(reopenedStore.Get()); }
        else { Assert.Equal("peer", result!.Name); Assert.Equal("peer", reopenedStore.Get()!.PeerName); }
        Assert.Equal(PairingRecoveryPhase.None, (await reopenedVault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        Assert.True(reopenedVault.IsAuthorized("oldpeer", oldToken));
    }

    [Fact]
    public async Task ExplicitIsolatedCandidateCompletesRealTlsThroughBothHighLevelApis()
    {
        // The TCP connection targets this machine's own approved LAN address only.
        // No discovery packets, existing application data or remote devices are used.
        var address = VerifiedLanBinding.GetEligibleAddresses().FirstOrDefault()
            ?? throw new InvalidOperationException("An active private LAN interface is needed for this isolated integration test.");
        var lan = VerifiedLanBinding.VerifyCurrentNetwork(address, true);
        var network = DiscoveryNetworkBinding.CreateForCurrentNetwork(lan);
        var workRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "high-level-fixtures", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(workRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var hostPem = Path.Combine(workRoot, "host.pem"); var peerPem = Path.Combine(workRoot, "peer.pem");
        await PrivateFiles.EnsureCertificateAsync(hostPem, timeout.Token);
        await PrivateFiles.EnsureCertificateAsync(peerPem, timeout.Token);
        using var hostCertificate = PrivateFiles.LoadTlsCertificate(hostPem);
        using var peerCertificate = PrivateFiles.LoadTlsCertificate(peerPem);
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peerCertificate),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, DeskLinkFingerprint.OfCertificate(hostCertificate), offer.KeyboardFingerprint, now.AddMinutes(1));
        using var listener = new TcpListener(address, 0); listener.Start(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Bind(new IPEndPoint(address, 0));
        var connecting = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var accepted = await listener.AcceptSocketAsync(timeout.Token); await connecting;
        var hostTask = PairingAuthenticatedChannel.StartHostAsync(accepted, hostCertificate, identity, offer, timeout.Token);
        var peerTask = PairingAuthenticatedChannel.StartPeerAsync(client, peerCertificate, identity, offer, timeout.Token);
        await Task.WhenAll(hostTask, peerTask);
        var hostChannel = await hostTask; var peerChannel = await peerTask;
        var protector = new TestProtector();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(workRoot, "vault"), protector, timeout.Token);
        await using var store = await PeerInvitationStore.OpenAsync(Path.Combine(workRoot, "peer"), protector, timeout.Token);
        var oldToken = await vault.AddConfirmedAsync("oldpeer", new string('A', 64), timeout.Token);
        await using var runtime = MultiPeerRuntime.CreateForLan(vault, Path.Combine(workRoot, "runtime"), "host", lan,
            24821, 24822, Path.Combine(workRoot, "not-started-engine.exe"), MultiPeerKeyboardSession.ExperimentalEngineSha256);
        var pairing = new MultiPeerPairingCoordinator(vault, "host", address.ToString(), 24821, 24822, 24800, identity.HostFingerprint);
        var lease = DiscoveryReviewLease.Start(network);
        var hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var peerLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        // Construct with actual authenticated channels to exercise the production
        // high-level methods; discovery startup and UI remain separate acceptance tests.
        await using var secureHost = Construct<SecurePairingHost>(hostChannel, lease, hostLifetime, Task.CompletedTask,
            PrivateFiles.LoadTlsCertificate(hostPem), runtime);
        await using var securePeer = Construct<SecurePairingPeer>(peerChannel, new TestNetwork(network), peerLifetime,
            PrivateFiles.LoadTlsCertificate(peerPem));
        await Assert.ThrowsAsync<InvalidOperationException>(() => secureHost.CompletePairingAsync(pairing, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => securePeer.AcceptPairingOperationAsync(store, timeout.Token));
        Assert.DoesNotContain(vault.GetTrustedDevices(), x => x.Name == "peer");
        Assert.Null(store.Get());
        await Task.WhenAll(secureHost.ConfirmAsync(timeout.Token), securePeer.WaitForRemoteConfirmationAsync(timeout.Token));
        await Task.WhenAll(securePeer.ConfirmAsync(timeout.Token), secureHost.WaitForRemoteConfirmationAsync(timeout.Token));
        var hostCompletion = secureHost.CompletePairingAsync(pairing, timeout.Token);
        var peerCompletion = securePeer.AcceptPairingOperationAsync(store, timeout.Token);
        await Task.WhenAll(hostCompletion, peerCompletion).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(await peerCompletion);
        Assert.Equal("peer", (await hostCompletion).Name);
        Assert.Equal("peer", store.Get()!.PeerName);
        Assert.Equal(PairingRecoveryPhase.None, (await vault.GetPairingRecoveryStatusAsync(timeout.Token)).Phase);
        Assert.True(vault.IsAuthorized("oldpeer", oldToken));
    }

    private static T Construct<T>(params object[] arguments) => (T)(Activator.CreateInstance(typeof(T),
        BindingFlags.Instance | BindingFlags.NonPublic, binder: null, args: arguments, culture: null)
        ?? throw new InvalidOperationException("The isolated high-level fixture could not be created."));

    private static (PeerIdentityOffer, PairingSessionIdentity) CreateIdentity(
        System.Security.Cryptography.X509Certificates.X509Certificate2 host,
        System.Security.Cryptography.X509Certificates.X509Certificate2 peer)
    {
        var now = DateTimeOffset.UtcNow;
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peer),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        return (offer, new PairingSessionIdentity(Guid.NewGuid(), Guid.ParseExact(offer.Nonce, "N"), Guid.NewGuid(),
            "host", offer.Name, DeskLinkFingerprint.OfCertificate(host), offer.KeyboardFingerprint, now.AddMinutes(1)));
    }

    private sealed class TestNetwork(DiscoveryNetworkBinding binding) : PairingNetworkHandoff
    {
        internal override DiscoveryNetworkBinding Binding => binding;
        internal override CancellationToken RevocationToken => CancellationToken.None;
        internal override Task<bool> RevalidateAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
