using System.Text.Json;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PeerPairingCapacityTests
{
    [Theory]
    [InlineData("completion", 63, false)]
    [InlineData("completion", 64, false)]
    [InlineData("abort", 63, true)]
    [InlineData("abort", 64, true)]
    public async Task PendingRequiresSpaceForCompletionAndAbortBeforeChangingAnyEvidence(string kind, int count, bool succeeds)
    {
        using var folder = new TestFolder();
        var directory = Path.Combine(folder.Path, "peer");
        await using var store = await PeerInvitationStore.OpenAsync(directory, new TestProtector());
        var old = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, HostName = "oldhost", PeerName = "peer",
            CertificateSha256 = new string('A', 64), Token = Convert.ToBase64String(new byte[32]) };
        await store.SaveConfirmedAsync(old, old.CertificateSha256);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            var historical = new PairingSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", "peer",
                new string('B', 64), new string('C', 64), now.AddMinutes(1));
            var binding = new PairingHistoricalBinding(historical, Guid.NewGuid(), 1, 24822, "127.0.0.1", 24821, 24800, 24822, new string('D', 64));
            var prefix = kind == "completion" ? "peer-completed-" : "peer-aborted-";
            var path = Path.Combine(directory, prefix + historical.TransactionId.ToString("N") + ".bin");
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var bytes = kind == "completion"
                ? JsonSerializer.SerializeToUtf8Bytes(new { version = 1, identity = historical, stageId = binding.StageId, slot = 1,
                    dataPortBase = 24822, host = "127.0.0.1", controlPort = 24821, keyboardPort = 24800, dataPort = 24822,
                    invitationDigest = new string('D', 64), pendingHash = new string('E', 64), readyHash = new string('F', 64),
                    promotionHash = new string('1', 64), baseHash = (string?)null, reservationHash = (string?)null }, options)
                : JsonSerializer.SerializeToUtf8Bytes(new { version = 1, binding }, options);
            await File.WriteAllBytesAsync(path, bytes);
        }
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", "peer",
            new string('B', 64), new string('C', 64), now.AddMinutes(1));
        var candidate = old with { HostName = "host", CertificateSha256 = identity.HostFingerprint, Token = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()) };
        var pending = () => store.StagePendingAsync(identity, candidate, identity.PeerName, identity.HostFingerprint,
            PeerInvitationStore.ComputePendingDigest(candidate), now);
        if (succeeds)
        {
            await pending();
            Assert.True(File.Exists(Path.Combine(directory, "peer-pending-invitation.bin")));
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(pending);
            Assert.False(File.Exists(Path.Combine(directory, "peer-pending-invitation.bin")));
        }
        Assert.Equal(old, store.Get());
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
