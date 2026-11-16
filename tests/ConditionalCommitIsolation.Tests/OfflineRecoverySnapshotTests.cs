using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    private static async Task<MultiPeerInvitation> MakeOfflineMarkerAsync(string root, WindowsCredentialProtector protector, bool writeMarker = true)
    {
        var ciphertext = await File.ReadAllBytesAsync(Path.Combine(root, "pending-grant.bin"));
        var plain = protector.Unprotect(ciphertext);
        try
        {
            using var json = JsonDocument.Parse(plain); var pending = json.RootElement; var frozen = pending.GetProperty("frozen");
            var token = pending.GetProperty("token").GetString()!; var bytes = Convert.FromBase64String(token);
            string tokenDigest;
            try { using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(Encoding.UTF8.GetBytes("nian-link-vault-token-v2\0")); hash.AppendData(bytes); tokenDigest = Convert.ToHexString(hash.GetHashAndReset()); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            var marker = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, stageId = pending.GetProperty("stageId"), identity = pending.GetProperty("identity"), invitationDigest = frozen.GetProperty("invitationDigest"), tokenDigest });
            try { if (writeMarker) await File.WriteAllBytesAsync(Path.Combine(root, "pending-prepare-unknown.bin"), protector.Protect(marker)); }
            finally { CryptographicOperations.ZeroMemory(marker); }
            var id = pending.GetProperty("identity");
            return new MultiPeerInvitation { Host = frozen.GetProperty("host").GetString()!, Port = frozen.GetProperty("controlPort").GetInt32(), KeyboardPort = frozen.GetProperty("keyboardPort").GetInt32(), DataPort = frozen.GetProperty("dataPort").GetInt32(),
                HostName = id.GetProperty("hostName").GetString()!, PeerName = id.GetProperty("peerName").GetString()!, CertificateSha256 = id.GetProperty("hostFingerprint").GetString()!, Token = token };
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static async Task<Guid> PrepareOfflineHostAsync(MultiDeviceCredentialVault vault, string root, WindowsCredentialProtector protector,
        PairingSessionIdentity identity, PeerIdentityOffer offer, DateTimeOffset now)
    {
        var stage = (await vault.StagePendingGrantAsync(identity, offer, now, clock: () => now)).StageId;
        await vault.FreezePendingInvitationForLoopbackTestAsync(stage, identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: () => now);
        _ = await MakeOfflineMarkerAsync(root, protector); return stage;
    }

    [Fact]
    public async Task OfflineHostAndPeerSnapshotsCompareCurrentProtectedBindingsWithoutMutation()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, oldCandidate) = OfflineProposal(now);
        try
        {
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var oldHost = await host.AddConfirmedAsync("old-device", new string('C', 64));
            var oldPeer = oldCandidate with { Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            await peer.SaveConfirmedAsync(oldPeer, identity.HostFingerprint);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, protector, identity, offer, now);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            await peer.StagePeerV3IsolatedTestAsync(stage, identity, offer, candidate, 2, 24802, now, () => now);
            var names = new[] { Path.Combine(hostRoot, "multi-device-trust.bin"), Path.Combine(hostRoot, "pending-grant.bin"), Path.Combine(hostRoot, "pending-prepare-unknown.bin"), Path.Combine(peerRoot, "peer-invitation.bin"), Path.Combine(peerRoot, "peer-pending-invitation.bin") };
            var before = await Task.WhenAll(names.Select(path => File.ReadAllBytesAsync(path)));
            var h = await host.CaptureOfflineRecoveryTestAsync(now); var p = await peer.CaptureOfflineRecoveryTestAsync(now);
            Assert.NotNull(h); Assert.NotNull(p); Assert.Equal(1, h.MarkerVersion); Assert.True(h.MatchesCurrentPeer(p)); Assert.False(h.IsExpired); Assert.False(p.IsExpired);
            Assert.False(h.Owner is MultiDeviceCredentialVault); Assert.False(p.Owner is PeerInvitationStore);
            Assert.False(p.Generation is MultiPeerInvitation);
            Assert.False(JsonSerializer.Serialize(h.Generation, h.Generation.GetType()).Contains(oldHost, StringComparison.Ordinal));
            Assert.False(JsonSerializer.Serialize(p.Generation, p.Generation.GetType()).Contains(oldPeer.Token, StringComparison.Ordinal));
            Assert.True(await host.IsOfflineRecoverySnapshotCurrentAsync(h, now)); Assert.True(await peer.IsOfflineRecoverySnapshotCurrentAsync(p, now));
            for (var i = 0; i < names.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(names[i]));
            Assert.True(host.IsAuthorized("old-device", oldHost)); Assert.True(oldPeer == peer.Get());
            Assert.False(host.IsAuthorized(identity.PeerName, candidate.Token));
            Assert.False(h.ToString()!.Contains(candidate.Token, StringComparison.Ordinal)); Assert.False(p.ToString()!.Contains(candidate.Token, StringComparison.Ordinal));
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    private static async Task<MultiPeerInvitation> ReadOfflineHostInvitationAsync(string root, WindowsCredentialProtector protector)
    {
        return await MakeOfflineMarkerAsync(root, protector, writeMarker: false);
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("identity")]
    [InlineData("invitation-digest")]
    [InlineData("token-digest")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("bad-ciphertext")]
    [InlineData("oversize")]
    public async Task CorruptOrMismatchedHostMarkerIsUnknownAndPreserved(string defect)
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, _) = OfflineProposal(now);
        try
        {
            await using var host = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            await PrepareOfflineHostAsync(host, root, protector, identity, offer, now);
            var path = Path.Combine(root, "pending-prepare-unknown.bin");
            if (defect == "bad-ciphertext") await File.WriteAllBytesAsync(path, [1, 2, 3]);
            else if (defect == "oversize") await File.WriteAllBytesAsync(path, new byte[65537]);
            else
            {
                var plain = protector.Unprotect(await File.ReadAllBytesAsync(path));
                try
                {
                    var node = JsonNode.Parse(plain)!;
                    switch (defect)
                    {
                        case "stage": node["stageId"] = Guid.NewGuid(); break;
                        case "identity": node["identity"]!["peerName"] = "other"; break;
                        case "invitation-digest": node["invitationDigest"] = new string('A', 64); break;
                        case "token-digest": node["tokenDigest"] = new string('A', 64); break;
                        case "extra": node["extra"] = true; break;
                        case "missing": node.AsObject().Remove("tokenDigest"); break;
                    }
                    var changed = JsonSerializer.SerializeToUtf8Bytes(node);
                    try { await File.WriteAllBytesAsync(path, protector.Protect(changed)); }
                    finally { CryptographicOperations.ZeroMemory(changed); }
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            var before = await File.ReadAllBytesAsync(path);
            Assert.Null(await host.CaptureOfflineRecoveryTestAsync(now));
            Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Empty(host.GetTrustedDevices());
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task LocalChangesInvalidateSnapshotsAndExpiredHistoryDoesNotAuthorize()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, candidate) = OfflineProposal(now);
        try
        {
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, protector, identity, offer, now);
            await peer.SaveConfirmedAsync(candidate, identity.HostFingerprint);
            var hostCandidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            await peer.StagePeerV3IsolatedTestAsync(stage, identity, offer, hostCandidate, 1, 24802, now, () => now);
            var h = await host.CaptureOfflineRecoveryTestAsync(now); var p = await peer.CaptureOfflineRecoveryTestAsync(now);
            Assert.NotNull(h); Assert.NotNull(p); Assert.True(h.MatchesCurrentPeer(p));
            var later = now.AddHours(1); Assert.True((await host.CaptureOfflineRecoveryTestAsync(later))!.IsExpired); Assert.True((await peer.CaptureOfflineRecoveryTestAsync(later))!.IsExpired);
            await host.AddConfirmedAsync("another", new string('D', 64));
            await peer.SaveConfirmedAsync(candidate with { Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }, identity.HostFingerprint);
            Assert.False(await host.IsOfflineRecoverySnapshotCurrentAsync(h, now));
            Assert.False(await peer.IsOfflineRecoverySnapshotCurrentAsync(p, now));
            Assert.Null(await peer.CaptureOfflineRecoveryTestAsync(now));
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task MissingActiveVaultVersionIsUnknownAndPreservesBothFiles()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, _) = OfflineProposal(now);
        try
        {
            await using var host = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var old = await host.AddConfirmedAsync("old-device", new string('C', 64));
            await PrepareOfflineHostAsync(host, root, protector, identity, offer, now);
            var path = Path.Combine(root, "multi-device-trust.bin"); var damaged = protector.Protect(Encoding.UTF8.GetBytes("{}"));
            await File.WriteAllBytesAsync(path, damaged); var pending = await File.ReadAllBytesAsync(Path.Combine(root, "pending-grant.bin"));
            Assert.Null(await host.CaptureOfflineRecoveryTestAsync(now));
            Assert.Equal(damaged, await File.ReadAllBytesAsync(path)); Assert.Equal(pending, await File.ReadAllBytesAsync(Path.Combine(root, "pending-grant.bin")));
            Assert.True(host.IsAuthorized("old-device", old));
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task DuplicateActivePeerFieldsAreUnknownAndPreserved()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, candidate) = OfflineProposal(now);
        try
        {
            await using var peer = await PeerInvitationStore.OpenAsync(root, protector);
            await peer.SaveConfirmedAsync(candidate, identity.HostFingerprint);
            await peer.StagePeerV3IsolatedTestAsync(Guid.NewGuid(), identity, offer, candidate, 1, 24802, now, () => now);
            var path = Path.Combine(root, "peer-invitation.bin"); var plain = protector.Unprotect(await File.ReadAllBytesAsync(path));
            try
            {
                var json = Encoding.UTF8.GetString(plain).Replace("\"host\":\"127.0.0.1\"", "\"host\":\"127.0.0.1\",\"host\":\"127.0.0.1\"", StringComparison.Ordinal);
                var changed = Encoding.UTF8.GetBytes(json);
                try { await File.WriteAllBytesAsync(path, protector.Protect(changed)); }
                finally { CryptographicOperations.ZeroMemory(changed); }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var before = await File.ReadAllBytesAsync(path);
            Assert.Null(await peer.CaptureOfflineRecoveryTestAsync(now));
            Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.True(candidate == peer.Get());
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("platform")]
    [InlineData("offer-expiry")]
    [InlineData("invitation-port")]
    public async Task DifferentCurrentPeerBindingDoesNotCompareEqual(string difference)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, _) = OfflineProposal(now);
        try
        {
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, protector, identity, offer, now);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            if (difference == "stage") stage = Guid.NewGuid();
            if (difference == "platform") offer = offer with { Platform = "mac" };
            if (difference == "offer-expiry") offer = offer with { ExpiresAtUtc = offer.ExpiresAtUtc.AddSeconds(1) };
            if (difference == "invitation-port") candidate = candidate with { Port = candidate.Port + 10 };
            await peer.StagePeerV3IsolatedTestAsync(stage, identity, offer, candidate, 1, 24802, now, () => now);
            var h = await host.CaptureOfflineRecoveryTestAsync(now); var p = await peer.CaptureOfflineRecoveryTestAsync(now);
            Assert.NotNull(h); Assert.NotNull(p); Assert.False(h.MatchesCurrentPeer(p));
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task AbortedLedgerCannotProduceCurrentHostSnapshot()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, _) = OfflineProposal(now);
        try
        {
            await using var host = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var stage = (await host.StagePendingGrantAsync(identity, offer, now, clock: () => now)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: () => now);
            Assert.Equal("aborted", await host.AbortIsolatedAsync(stage, identity)); await MakeOfflineMarkerAsync(root, protector);
            var activePath = Path.Combine(root, "multi-device-trust.bin"); var before = await File.ReadAllBytesAsync(activePath);
            Assert.Null(await host.CaptureOfflineRecoveryTestAsync(now)); Assert.Equal(before, await File.ReadAllBytesAsync(activePath)); Assert.Empty(host.GetTrustedDevices());
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedOrRevokedLedgerCannotProducePendingSnapshot(bool revoked)
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, _) = OfflineProposal(now);
        try
        {
            await using var host = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var stage = (await host.StagePendingGrantAsync(identity, offer, now, clock: () => now)).StageId;
            var frozen = await host.FreezePendingInvitationForLoopbackTestAsync(stage, identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: () => now);
            var flow = new IsolatedConfirmation.Flow(identity, offer, stage, frozen, () => now);
            flow.ConfirmLocal(identity); flow.ConfirmRemote(identity);
            var receipt = await host.CommitAndLocateIsolatedAsync(flow.Issue(), now, () => now);
            if (revoked) Assert.Equal("revoked", await host.RevokeIsolatedAsync(receipt.Locator));
            await MakeOfflineMarkerAsync(root, protector);
            var path = Path.Combine(root, "multi-device-trust.bin"); var before = await File.ReadAllBytesAsync(path);
            Assert.Null(await host.CaptureOfflineRecoveryTestAsync(now)); Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.Equal(revoked ? 0 : 1, host.GetTrustedDevices().Count);
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("pending")]
    public async Task HostFileChangesInvalidateCapturedSnapshot(string changed)
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, _) = OfflineProposal(now);
        try
        {
            await using var host = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            await PrepareOfflineHostAsync(host, root, protector, identity, offer, now);
            var snapshot = await host.CaptureOfflineRecoveryTestAsync(now); Assert.NotNull(snapshot);
            var file = Path.Combine(root, changed == "marker" ? "pending-prepare-unknown.bin" : "pending-grant.bin");
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
            Assert.False(await host.IsOfflineRecoverySnapshotCurrentAsync(snapshot, now)); Assert.Null(await host.CaptureOfflineRecoveryTestAsync(now));
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task UnavailableProtectorReturnsUnknownAndKeepsHostAndPeerFiles()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var windows = new WindowsCredentialProtector(); var protector = new RecoveryFaultProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, candidate) = OfflineProposal(now);
        try
        {
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, windows, identity, offer, now);
            await peer.SaveConfirmedAsync(candidate, identity.HostFingerprint);
            var hostCandidate = await ReadOfflineHostInvitationAsync(hostRoot, windows);
            await peer.StagePeerV3IsolatedTestAsync(stage, identity, offer, hostCandidate, 1, 24802, now, () => now);
            var paths = new[] { Path.Combine(hostRoot, "pending-grant.bin"), Path.Combine(hostRoot, "pending-prepare-unknown.bin"), Path.Combine(peerRoot, "peer-pending-invitation.bin"), Path.Combine(peerRoot, "peer-invitation.bin") };
            var before = await Task.WhenAll(paths.Select(path => File.ReadAllBytesAsync(path)));
            protector.Unavailable = true;
            Assert.Null(await host.CaptureOfflineRecoveryTestAsync(now)); Assert.Null(await peer.CaptureOfflineRecoveryTestAsync(now));
            for (var i = 0; i < paths.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(paths[i]));
            Assert.True(candidate == peer.Get());
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    private sealed class RecoveryFaultProtector : IMultiDeviceSecretProtector
    {
        private readonly WindowsCredentialProtector inner = new();
        internal bool Unavailable { get; set; }
        public byte[] Protect(byte[] plaintext) => inner.Protect(plaintext);
        public byte[] Unprotect(byte[] ciphertext) => Unavailable ? throw new CryptographicException("Synthetic protection key unavailable.") : inner.Unprotect(ciphertext);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SnapshotWaitingForStoreGateObservesConcurrentMutation(bool hostSide)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new GateProtector(); var windows = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer, oldPeer) = OfflineProposal(now);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, windows, identity, offer, now);
            await peer.SaveConfirmedAsync(oldPeer, identity.HostFingerprint);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, windows);
            await peer.StagePeerV3IsolatedTestAsync(stage, identity, offer, candidate, 1, 24802, now, () => now);
            var initialHost = await host.CaptureOfflineRecoveryTestAsync(now); var initialPeer = await peer.CaptureOfflineRecoveryTestAsync(now);
            Assert.NotNull(initialHost); Assert.NotNull(initialPeer);
            protector.BeforeProtect = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); };
            var blocker = hostSide
                ? Task.Run(async () => { await host.AddConfirmedAsync("other", new string('D', 64)); })
                : Task.Run(async () => { await peer.SaveConfirmedAsync(oldPeer with { Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }, identity.HostFingerprint); });
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var capture = hostSide ? host.CaptureOfflineRecoveryTestAsync(now) : peer.CaptureOfflineRecoveryTestAsync(now);
                Assert.False(capture.IsCompleted); release.Set(); await blocker.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(await capture.WaitAsync(TimeSpan.FromSeconds(5)));
                if (hostSide) Assert.False(await host.IsOfflineRecoverySnapshotCurrentAsync(initialHost, now));
                else Assert.False(await peer.IsOfflineRecoverySnapshotCurrentAsync(initialPeer, now));
            }
            finally { release.Set(); }
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }
}
