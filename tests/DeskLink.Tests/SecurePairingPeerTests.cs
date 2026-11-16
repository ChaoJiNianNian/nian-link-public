using System.Net;
using System.Net.NetworkInformation;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class SecurePairingPeerTests
{
    [Fact]
    public async Task SyntheticNoticeCannotStartARealPairingConnection()
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
        using var folder = new TestFolder();
        await Assert.ThrowsAsync<InvalidOperationException>(() => SecurePairingPeer.AcceptAsync(request, folder.Path));
        Assert.Null(request.TryClaim());
    }
}
