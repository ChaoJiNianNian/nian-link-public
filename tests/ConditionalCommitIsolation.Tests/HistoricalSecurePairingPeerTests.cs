using System.Net;
using System.Net.NetworkInformation;
using DesktopToolbox.DeskLink;

namespace ConditionalCommitIsolation.Tests;

public sealed class HistoricalSecurePairingPeerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableHandoffDisposesOnceBeforeCertificateOrSocket(bool revokedAfterAcquire)
    {
        var now = DateTimeOffset.UtcNow;
        var local = IPAddress.Parse("192.168.88.4");
        var adapter = new DiscoveryNetworkInterface("test-id", "test", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, 7, local, IPAddress.Parse("255.255.255.0"));
        Func<IReadOnlyList<DiscoveryNetworkInterface>> snapshots = () => [adapter];
        Func<bool> available = () => true;
        var ctor = typeof(DiscoveryNetworkBinding).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            binder: null, [typeof(DiscoveryNetworkInterface), snapshots.GetType(), available.GetType(), typeof(bool)], modifiers: null);
        Assert.NotNull(ctor);
        var binding = Assert.IsType<DiscoveryNetworkBinding>(ctor.Invoke([adapter, snapshots, available, true]));
        var handoff = new FakeHandoff(binding, revokedAfterAcquire);
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "mac", KeyboardFingerprint = new string('A', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(1) };
        var notice = new PairingDiscoveryNotice("host", offer.Name, new string('B', 64), Guid.ParseExact(offer.Nonce, "N"),
            Guid.NewGuid(), Guid.NewGuid(), 53123, now.AddSeconds(20), now.AddSeconds(50));
        using var inbox = new PairingNoticeInbox(binding, offer, TimeSpan.FromSeconds(30), default, () => now,
            _ => Task.FromResult<PairingNetworkHandoff>(handoff));
        Assert.True(inbox.Observe(notice.Encode(now), new IPEndPoint(IPAddress.Parse("192.168.88.9"), 53000), local, 7));
        var request = Assert.IsType<PairingIncomingRequest>(inbox.GetPending());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SecurePairingPeer.AcceptAsync(request, "relative-profile"));
        Assert.Equal("The approved network changed.", error.Message);
        Assert.Equal(revokedAfterAcquire ? 1 : 2, handoff.Revalidations);
        Assert.Equal(1, handoff.Disposals);
        Assert.Null(request.TryClaim());
    }

    [Fact]
    public async Task SyntheticRequestIsRejectedBeforeNetworkHandoffAndConsumed()
    {
        var now = DateTimeOffset.UtcNow;
        var local = IPAddress.Parse("192.168.88.4");
        var binding = DiscoveryNetworkBinding.FromSnapshotsForTest(local, () =>
            [new DiscoveryNetworkInterface("test-id", "test", NetworkInterfaceType.Ethernet, OperationalStatus.Up,
                7, local, IPAddress.Parse("255.255.255.0"))]);
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "mac", KeyboardFingerprint = new string('A', 64),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.AddMinutes(1) };
        var notice = new PairingDiscoveryNotice("host", offer.Name, new string('B', 64), Guid.ParseExact(offer.Nonce, "N"),
            Guid.NewGuid(), Guid.NewGuid(), 53123, now.AddSeconds(20), now.AddSeconds(50));
        using var inbox = new PairingNoticeInbox(binding, offer, TimeSpan.FromSeconds(30), default, () => now);
        Assert.True(inbox.Observe(notice.Encode(now), new IPEndPoint(IPAddress.Parse("192.168.88.9"), 53000), local, 7));
        var request = Assert.IsType<PairingIncomingRequest>(inbox.GetPending());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SecurePairingPeer.AcceptAsync(request, Path.GetTempPath()));
        Assert.Equal("The approved network is unavailable.", error.Message);
        Assert.Null(request.TryClaim());
    }

    // This deliberately synthetic binding is marked live via reflection only in this isolated test.
    // The handoff rejects before any certificate access, socket creation or network call.
    private sealed class FakeHandoff(DiscoveryNetworkBinding binding, bool revokedAfterAcquire) : PairingNetworkHandoff
    {
        private readonly CancellationTokenSource revoked = new();
        public int Revalidations { get; private set; }
        public int Disposals { get; private set; }
        internal override DiscoveryNetworkBinding Binding => binding;
        internal override CancellationToken RevocationToken => revoked.Token;
        internal override Task<bool> RevalidateAsync(CancellationToken ct)
        {
            Revalidations++;
            if (Revalidations == 1 && revokedAfterAcquire) revoked.Cancel();
            return Task.FromResult(Revalidations == 1);
        }
        public override ValueTask DisposeAsync() { Disposals++; revoked.Dispose(); return ValueTask.CompletedTask; }
    }
}
