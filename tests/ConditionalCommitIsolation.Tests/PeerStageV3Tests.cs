using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    private static (PairingSessionIdentity Identity, PeerIdentityOffer Offer, MultiPeerInvitation Invitation) OfflineProposal(DateTimeOffset now)
    {
        var nonce = Guid.NewGuid(); var host = new string('A', 64); var peer = new string('B', 64);
        var identity = new PairingSessionIdentity(Guid.NewGuid(), nonce, Guid.NewGuid(), "host", "peer", host, peer, now.AddMinutes(1));
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = peer, Nonce = nonce.ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        var candidate = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24801, KeyboardPort = 24800, DataPort = 24802,
            HostName = "host", PeerName = "peer", CertificateSha256 = host, Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        return (identity, offer, candidate);
    }

    [Fact]
    public async Task NewProtectedV3BindsCanonicalFullOfferAndPreservesOldActiveInvitation()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, candidate) = OfflineProposal(now); var stage = Guid.NewGuid();
        try
        {
            var equivalent = offer with { KeyboardFingerprint = string.Join(":", Enumerable.Range(0, 32).Select(i => offer.KeyboardFingerprint.Substring(i * 2, 2).ToLowerInvariant())), Nonce = offer.Nonce.ToUpperInvariant(), ExpiresAtUtc = offer.ExpiresAtUtc.ToOffset(TimeSpan.FromHours(8)) };
            var old = candidate with { Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            await using (var store = await PeerInvitationStore.OpenAsync(root, protector))
            {
                await store.SaveConfirmedAsync(old, identity.HostFingerprint);
                await store.StagePeerV3IsolatedTestAsync(stage, identity, equivalent, candidate, 1, 24802, now, () => now);
                Assert.True(old == store.Get());
                var snapshot = await store.InspectPeerV3IsolatedTestAsync(now);
                Assert.Equal("v3-history", snapshot.State); Assert.False(snapshot.IsExpired); Assert.Equal(stage, snapshot.StageId);
                Assert.Equal(offer, snapshot.Offer); Assert.False(snapshot.ToString().Contains(candidate.Token, StringComparison.Ordinal));
                var pending = Path.Combine(root, "peer-pending-invitation.bin"); var bytes = await File.ReadAllBytesAsync(pending);
                await Assert.ThrowsAsync<InvalidDataException>(() => store.GetPendingMetadataAsync());
                await Assert.ThrowsAsync<InvalidDataException>(() => store.AbortPendingAsync(identity, PeerInvitationStore.ComputePendingDigest(candidate)));
                Assert.Equal(bytes, await File.ReadAllBytesAsync(pending));
            }
            await using var reopened = await PeerInvitationStore.OpenAsync(root, protector);
            var historical = await reopened.InspectPeerV3IsolatedTestAsync(now.AddHours(1));
            Assert.Equal("v3-history", historical.State); Assert.True(historical.IsExpired); Assert.Equal(offer, historical.Offer);
            Assert.True(old == reopened.Get());
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task UncertainEarlierPublicationBlocksV3EvenWhenNoPendingFileExists()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, candidate) = OfflineProposal(now);
        try
        {
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            var old = candidate with { Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            await store.SaveConfirmedAsync(old, identity.HostFingerprint);
            store.MarkLivePeerStageUncertain(); // Same state set after a v2 replacement error; no network in this slice.
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.StagePeerV3IsolatedTestAsync(Guid.NewGuid(), identity, offer, candidate, 1, 24802, now, () => now));
            Assert.False(File.Exists(Path.Combine(root, "peer-pending-invitation.bin"))); Assert.True(old == store.Get());
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task V3MoveFailureBlocksRetryUntilStoreIsReopened()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, candidate) = OfflineProposal(now); var barrier = new ControlledLivePublishBarrier();
        try
        {
            await using var store = await PeerInvitationStore.OpenAsync(root, protector); store.BeforePeerV3Move = barrier;
            var staging = store.StagePeerV3IsolatedTestAsync(Guid.NewGuid(), identity, offer, candidate, 1, 24802, now, () => now);
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                var path = Path.Combine(root, "peer-pending-invitation.bin"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
                barrier.Release(); await Assert.ThrowsAnyAsync<IOException>(() => staging.WaitAsync(TimeSpan.FromSeconds(5)));
                File.Delete(path);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.StagePeerV3IsolatedTestAsync(Guid.NewGuid(), identity, offer, candidate, 1, 24802, now, () => now));
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.StagePendingAsync(identity, candidate, identity.PeerName, identity.HostFingerprint, PeerInvitationStore.ComputePendingDigest(candidate), now));
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetPendingMetadataAsync());
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.AbortPendingAsync(identity, PeerInvitationStore.ComputePendingDigest(candidate)));
                Assert.Equal("unknown", (await store.InspectPeerV3IsolatedTestAsync(now)).State);
            }
            finally { barrier.Release(); }
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task LegacyMoveFailureAlsoBlocksV3UntilStoreIsReopened()
    {
        var root = VaultRoot(); var protector = new GateProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, candidate) = OfflineProposal(now);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            protector.BeforeProtect = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); };
            var staging = Task.Run(() => store.StagePendingAsync(identity, candidate, identity.PeerName, identity.HostFingerprint, PeerInvitationStore.ComputePendingDigest(candidate), now));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var path = Path.Combine(root, "peer-pending-invitation.bin"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
                release.Set(); await Assert.ThrowsAnyAsync<IOException>(() => staging.WaitAsync(TimeSpan.FromSeconds(5)));
                File.Delete(path); protector.BeforeProtect = null;
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.StagePeerV3IsolatedTestAsync(Guid.NewGuid(), identity, offer, candidate, 1, 24802, now, () => now));
                Assert.Equal("unknown", (await store.InspectPeerV3IsolatedTestAsync(now)).State);
            }
            finally { release.Set(); }
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyPendingVersionsRemainUnchangedAndCannotMatchV3(int version)
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, candidate) = OfflineProposal(now);
        try
        {
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            var path = Path.Combine(root, "peer-pending-invitation.bin");
            if (version == 1) await store.StagePendingAsync(identity, candidate, identity.PeerName, identity.HostFingerprint, PeerInvitationStore.ComputePendingDigest(candidate), now, clock: TimeProvider.System);
            else
            {
                var plain = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, stageId = Guid.NewGuid(), identity, slot = 1, dataPortBase = 24802, invitation = candidate, invitationDigest = PeerInvitationStore.ComputePendingDigest(candidate), baseInvitationDigest = (string?)null }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                try { await File.WriteAllBytesAsync(path, protector.Protect(plain)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            var before = await File.ReadAllBytesAsync(path);
            var inspection = await store.InspectPeerV3IsolatedTestAsync(now);
            Assert.Equal("legacy", inspection.State); Assert.Null(inspection.Offer);
            Assert.Null(await store.CaptureOfflineRecoveryTestAsync(now));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.StagePeerV3IsolatedTestAsync(Guid.NewGuid(), identity, offer, candidate, 1, 24802, now, () => now));
            Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Null(store.Get());
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData("missing-platform")]
    [InlineData("wrong-platform")]
    [InlineData("wrong-name")]
    [InlineData("wrong-fingerprint")]
    [InlineData("noncanonical-fingerprint")]
    [InlineData("wrong-nonce")]
    [InlineData("wrong-version")]
    [InlineData("early-expiry")]
    [InlineData("extra")]
    [InlineData("null")]
    public async Task CorruptV3OfferReportsUnknownAndPreservesProtectedBytes(string defect)
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, candidate) = OfflineProposal(now);
        try
        {
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            await store.StagePeerV3IsolatedTestAsync(Guid.NewGuid(), identity, offer, candidate, 1, 24802, now, () => now);
            var path = Path.Combine(root, "peer-pending-invitation.bin"); var plain = protector.Unprotect(await File.ReadAllBytesAsync(path));
            try
            {
                var node = JsonNode.Parse(plain)!; var field = node["offer"]!;
                switch (defect)
                {
                    case "missing-platform": field.AsObject().Remove("platform"); break;
                    case "wrong-platform": field["platform"] = "linux"; break;
                    case "wrong-name": field["name"] = "other"; break;
                    case "wrong-fingerprint": field["keyboardFingerprint"] = new string('C', 64); break;
                    case "noncanonical-fingerprint": field["keyboardFingerprint"] = offer.KeyboardFingerprint.ToLowerInvariant(); break;
                    case "wrong-nonce": field["nonce"] = Guid.NewGuid().ToString("N"); break;
                    case "wrong-version": field["version"] = 9; break;
                    case "early-expiry": field["expiresAtUtc"] = identity.ExpiresAtUtc.AddSeconds(-1); break;
                    case "extra": field["extra"] = true; break;
                    case "null": node["offer"] = null; break;
                }
                var changed = JsonSerializer.SerializeToUtf8Bytes(node);
                try { await File.WriteAllBytesAsync(path, protector.Protect(changed)); }
                finally { CryptographicOperations.ZeroMemory(changed); }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var before = await File.ReadAllBytesAsync(path); var inspection = await store.InspectPeerV3IsolatedTestAsync(now);
            Assert.Equal("unknown", inspection.State); Assert.Null(inspection.Offer);
            Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Null(store.Get());
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("rollback")]
    [InlineData("cancel")]
    [InlineData("protect")]
    public async Task InterruptedV3WriteKeepsOldInvitationAndNoPending(string interruption)
    {
        var root = VaultRoot(); var protector = new GateProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, candidate) = OfflineProposal(now); long ticks = now.UtcTicks;
        DateTimeOffset Clock() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        try
        {
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            var old = candidate with { Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            await store.SaveConfirmedAsync(old, identity.HostFingerprint);
            var before = await File.ReadAllBytesAsync(Path.Combine(root, "peer-invitation.bin"));
            using var cancel = new CancellationTokenSource();
            protector.BeforeProtect = () =>
            {
                if (interruption == "expired") Interlocked.Exchange(ref ticks, identity.ExpiresAtUtc.UtcTicks);
                if (interruption == "rollback") Interlocked.Exchange(ref ticks, now.AddSeconds(-1).UtcTicks);
                if (interruption == "cancel") cancel.Cancel();
                if (interruption == "protect") throw new CryptographicException("Synthetic protector failure.");
            };
            if (interruption == "expired") await Assert.ThrowsAsync<ArgumentException>(() => store.StagePeerV3IsolatedTestAsync(Guid.NewGuid(), identity, offer, candidate, 1, 24802, now, Clock, cancel.Token));
            if (interruption == "rollback") await Assert.ThrowsAsync<InvalidOperationException>(() => store.StagePeerV3IsolatedTestAsync(Guid.NewGuid(), identity, offer, candidate, 1, 24802, now, Clock, cancel.Token));
            if (interruption == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StagePeerV3IsolatedTestAsync(Guid.NewGuid(), identity, offer, candidate, 1, 24802, now, Clock, cancel.Token));
            if (interruption == "protect") await Assert.ThrowsAsync<CryptographicException>(() => store.StagePeerV3IsolatedTestAsync(Guid.NewGuid(), identity, offer, candidate, 1, 24802, now, Clock, cancel.Token));
            Assert.Equal("unknown", (await store.InspectPeerV3IsolatedTestAsync(now)).State);
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(root, "peer-invitation.bin"))); Assert.True(old == store.Get());
            Assert.False(File.Exists(Path.Combine(root, "peer-pending-invitation.bin")));
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("case")]
    [InlineData("unknown-version")]
    [InlineData("corrupt-ciphertext")]
    [InlineData("oversize")]
    public async Task InvalidV3RecordReportsUnknownWithoutRewriting(string defect)
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, candidate) = OfflineProposal(now);
        try
        {
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            await store.StagePeerV3IsolatedTestAsync(Guid.NewGuid(), identity, offer, candidate, 1, 24802, now, () => now);
            var path = Path.Combine(root, "peer-pending-invitation.bin");
            if (defect == "corrupt-ciphertext") await File.WriteAllBytesAsync(path, [1, 2, 3]);
            else if (defect == "oversize") await File.WriteAllBytesAsync(path, new byte[65537]);
            else
            {
                var plain = protector.Unprotect(await File.ReadAllBytesAsync(path));
                try
                {
                    var json = System.Text.Encoding.UTF8.GetString(plain);
                    json = defect switch
                    {
                        "duplicate" => json.Replace("\"platform\":\"windows\"", "\"platform\":\"windows\",\"platform\":\"windows\"", StringComparison.Ordinal),
                        "case" => json.Replace("\"platform\":\"windows\"", "\"platform\":\"windows\",\"Platform\":\"windows\"", StringComparison.Ordinal),
                        "unknown-version" => json.Replace("\"version\":3", "\"version\":99", StringComparison.Ordinal),
                        _ => throw new InvalidOperationException()
                    };
                    var changed = System.Text.Encoding.UTF8.GetBytes(json);
                    try { await File.WriteAllBytesAsync(path, protector.Protect(changed)); }
                    finally { CryptographicOperations.ZeroMemory(changed); }
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            var before = await File.ReadAllBytesAsync(path);
            Assert.Equal("unknown", (await store.InspectPeerV3IsolatedTestAsync(now)).State);
            Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Null(store.Get());
        }
        finally { CleanVault(root); }
    }
}
