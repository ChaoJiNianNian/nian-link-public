using System.Text.Json;
using System.Buffers.Binary;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PeerRetirementTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("index-written")]
    [InlineData("source-deleted")]
    public async Task ExactRetirementSurvivesInterruptionAndRejectsLatePairing(string interruption)
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "peer"); var protector = new TestProtector();
        var store = await PeerInvitationStore.OpenAsync(path, protector);
        var now = DateTimeOffset.UtcNow;
        var oldIdentity = new PairingSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", "peer",
            new string('B', 64), new string('C', 64), now.AddMinutes(1));
        for (var i = 0; i < 64; i++)
        {
            var id = i == 0 ? oldIdentity : oldIdentity with { SessionId = Guid.NewGuid(), OfferNonce = Guid.NewGuid(), TransactionId = Guid.NewGuid() };
            var binding = new PairingHistoricalBinding(id, Guid.NewGuid(), 1, 24822, "127.0.0.1", 24821, 24800, 24822, new string('D', 64));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, binding }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllBytesAsync(Path.Combine(path, "peer-aborted-" + id.TransactionId.ToString("N") + ".bin"), bytes);
        }
        if (interruption != "none")
        {
            store.RetirementCheckpointForTesting = step => { if (step == interruption) throw new IOException("simulated crash"); };
            await Assert.ThrowsAnyAsync<IOException>(() => store.CompactAbortedHistoryAsync());
            await store.DisposeAsync();
            store = await PeerInvitationStore.OpenAsync(path, protector);
        }
        await using (store)
        {
            await store.CompactAbortedHistoryAsync();
            Assert.Empty(Directory.EnumerateFiles(path, "peer-aborted-*.bin"));
            Assert.True(File.Exists(Path.Combine(path, "peer-retired.bin")));
            var candidate = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, HostName = "host", PeerName = "peer",
                CertificateSha256 = oldIdentity.HostFingerprint, Token = Convert.ToBase64String(new byte[32]) };
            await Assert.ThrowsAnyAsync<Exception>(() => store.StagePendingAsync(oldIdentity, candidate, "peer", oldIdentity.HostFingerprint,
                PeerInvitationStore.ComputePendingDigest(candidate), now));
            foreach (var reused in new[] {
                oldIdentity with { SessionId = Guid.NewGuid(), OfferNonce = Guid.NewGuid() },
                oldIdentity with { TransactionId = Guid.NewGuid(), OfferNonce = Guid.NewGuid() },
                oldIdentity with { TransactionId = Guid.NewGuid(), SessionId = Guid.NewGuid() } })
                await Assert.ThrowsAnyAsync<Exception>(() => store.StagePendingAsync(reused, candidate, "peer", reused.HostFingerprint,
                    PeerInvitationStore.ComputePendingDigest(candidate), now));
            var next = oldIdentity with { SessionId = Guid.NewGuid(), OfferNonce = Guid.NewGuid(), TransactionId = Guid.NewGuid() };
            await store.StagePendingAsync(next, candidate, "peer", next.HostFingerprint, PeerInvitationStore.ComputePendingDigest(candidate), now);
            Assert.True(File.Exists(Path.Combine(path, "peer-pending-invitation.bin")));
        }
    }

    [Fact]
    public async Task CorruptRetiredIndexFailsClosedAndPreservesExistingInvitation()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "peer");
        await using var store = await PeerInvitationStore.OpenAsync(path, new TestProtector());
        var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, HostName = "host", PeerName = "peer",
            CertificateSha256 = new string('B', 64), Token = Convert.ToBase64String(new byte[32]) };
        await store.SaveConfirmedAsync(invitation, invitation.CertificateSha256);
        await File.WriteAllTextAsync(Path.Combine(path, "peer-retired.bin"), "damaged synthetic index");
        var now = DateTimeOffset.UtcNow;
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", "peer",
            invitation.CertificateSha256, new string('C', 64), now.AddMinutes(1));
        await Assert.ThrowsAnyAsync<Exception>(() => store.StagePendingAsync(identity, invitation, "peer", identity.HostFingerprint,
            PeerInvitationStore.ComputePendingDigest(invitation), now));
        Assert.Equal(PairingRecoveryPhase.EvidenceProblem, (await store.GetPairingRecoveryStatusAsync()).Phase);
        Assert.Equal(invitation, store.Get());
    }

    [Fact]
    public async Task ReplacingPublishedIndexCannotDeleteOriginalAbortEvidence()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "peer");
        await using var store = await PeerInvitationStore.OpenAsync(path, new TestProtector());
        var now = DateTimeOffset.UtcNow;
        var identity = new PairingSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", "peer",
            new string('B', 64), new string('C', 64), now.AddMinutes(1));
        var binding = new PairingHistoricalBinding(identity, Guid.NewGuid(), 1, 24822, "127.0.0.1", 24821, 24800, 24822, new string('D', 64));
        var original = Path.Combine(path, "peer-aborted-" + identity.TransactionId.ToString("N") + ".bin");
        await File.WriteAllBytesAsync(original, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, binding }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        store.RetirementCheckpointForTesting = step =>
        {
            if (step == "index-written") File.WriteAllText(Path.Combine(path, "peer-retired.bin"), "externally replaced index");
        };
        await Assert.ThrowsAnyAsync<Exception>(() => store.CompactAbortedHistoryAsync());
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task LegacyIndexUpgradesBeyond64KiBAndFullIndexPreservesNewEvidence()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "peer");
        await using var store = await PeerInvitationStore.OpenAsync(path, new TestProtector());
        var now = DateTimeOffset.UtcNow;
        var legacy = new byte[12 + 1099 * 48];
        "NLRIDX01"u8.CopyTo(legacy); BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(8, 4), 1099);
        Guid firstTransaction = default, firstSession = default, firstNonce = default;
        for (var i = 0; i < 1099; i++)
        {
            var transaction = Guid.NewGuid(); var session = Guid.NewGuid(); var nonce = Guid.NewGuid();
            if (i == 0) { firstTransaction = transaction; firstSession = session; firstNonce = nonce; }
            var offset = 12 + i * 48;
            transaction.TryWriteBytes(legacy.AsSpan(offset, 16)); session.TryWriteBytes(legacy.AsSpan(offset + 16, 16));
            nonce.TryWriteBytes(legacy.AsSpan(offset + 32, 16));
        }
        await File.WriteAllBytesAsync(Path.Combine(path, "peer-retired.bin"), legacy);
        var id = new PairingSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", "peer",
            new string('B', 64), new string('C', 64), now.AddMinutes(1));
        async Task<string> AddAbortAsync(PairingSessionIdentity value)
        {
            var binding = new PairingHistoricalBinding(value, Guid.NewGuid(), 1, 24822, "127.0.0.1", 24821, 24800, 24822, new string('D', 64));
            var file = Path.Combine(path, "peer-aborted-" + value.TransactionId.ToString("N") + ".bin");
            await File.WriteAllBytesAsync(file, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, binding }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return file;
        }
        await AddAbortAsync(id);
        await store.CompactAbortedHistoryAsync();
        var upgraded = await File.ReadAllBytesAsync(Path.Combine(path, "peer-retired.bin"));
        Assert.True(upgraded.Length > 65536);
        Assert.True(upgraded.AsSpan(0, 8).SequenceEqual("NLRIDX02"u8));
        var candidate = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24821, HostName = "host", PeerName = "peer",
            CertificateSha256 = id.HostFingerprint, Token = Convert.ToBase64String(new byte[32]) };
        var replay = id with { TransactionId = firstTransaction, SessionId = firstSession, OfferNonce = firstNonce };
        await Assert.ThrowsAnyAsync<Exception>(() => store.StagePendingAsync(replay, candidate, "peer", replay.HostFingerprint,
            PeerInvitationStore.ComputePendingDigest(candidate), now));
        var extra = id with { TransactionId = Guid.NewGuid(), SessionId = Guid.NewGuid(), OfferNonce = Guid.NewGuid() };
        var extraFile = await AddAbortAsync(extra);
        await Assert.ThrowsAnyAsync<Exception>(() => store.CompactAbortedHistoryAsync());
        Assert.True(File.Exists(extraFile));
        Assert.Equal(upgraded, await File.ReadAllBytesAsync(Path.Combine(path, "peer-retired.bin")));
    }

    private sealed class TestProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
