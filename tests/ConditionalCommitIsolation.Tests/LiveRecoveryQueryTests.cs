using System.Net;
using System.Security.Cryptography;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewMutualTlsReadOnlyQueryComparesProtectedStoresWithoutWrites(bool expired)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var oldToken = await host.AddConfirmedAsync("old", new string('C', 64));
            var oldPeer = TestInvitation(f); await peer.SaveConfirmedAsync(oldPeer, f.Identity.HostFingerprint);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, protector, f.Identity, f.Offer, now);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            await peer.StagePeerV3IsolatedTestAsync(stage, f.Identity, f.Offer, candidate, 2, 24802, now, () => now);
            var files = new[] { Path.Combine(hostRoot, "multi-device-trust.bin"), Path.Combine(hostRoot, "pending-grant.bin"), Path.Combine(hostRoot, "pending-prepare-unknown.bin"), Path.Combine(peerRoot, "peer-invitation.bin"), Path.Combine(peerRoot, "peer-pending-invitation.bin") };
            var before = await Task.WhenAll(files.Select(path => File.ReadAllBytesAsync(path)));
            var queryNow = expired ? now.AddHours(1) : now;
            var result = await IsolatedRecoveryQuery.RunAsync(host, peer, f.HostCertificate, f.PeerCertificate, queryNow, clock: () => queryNow);
            Assert.NotNull(result); Assert.True(result.Matched); Assert.Equal(expired, result.HistoryExpired);
            Assert.False(result.ToString().Contains(candidate.Token, StringComparison.Ordinal));
            for (var i = 0; i < files.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(files[i]));
            Assert.True(host.IsAuthorized("old", oldToken)); Assert.False(host.IsAuthorized(f.Identity.PeerName, candidate.Token)); Assert.True(oldPeer == peer.Get());
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewRecoveryConnectionRejectsCertificateOutsideBothProtectedPins(bool wrongHost)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await using var wrong = await Fixture.OpenAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, protector, f.Identity, f.Offer, now);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            await peer.StagePeerV3IsolatedTestAsync(stage, f.Identity, f.Offer, candidate, 1, 24802, now, () => now);
            var marker = await File.ReadAllBytesAsync(Path.Combine(hostRoot, "pending-prepare-unknown.bin"));
            var result = await IsolatedRecoveryQuery.RunAsync(host, peer, wrongHost ? wrong.HostCertificate : f.HostCertificate,
                wrongHost ? f.PeerCertificate : wrong.PeerCertificate, now);
            Assert.Null(result); Assert.Equal(marker, await File.ReadAllBytesAsync(Path.Combine(hostRoot, "pending-prepare-unknown.bin")));
            Assert.Empty(host.GetTrustedDevices()); Assert.Null(peer.Get());
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Theory]
    [InlineData(RecoveryTestFrame.Init, RecoveryTestFault.WrongRole)]
    [InlineData(RecoveryTestFrame.Init, RecoveryTestFault.Duplicate)]
    [InlineData(RecoveryTestFrame.Hello, RecoveryTestFault.WrongChallenge)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.WrongSession)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.WrongNonce)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.WrongFingerprint)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.WrongStage)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.WrongPlatform)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.WrongOfferExpiry)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.WrongSlot)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.WrongPort)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.WrongDigest)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.Extra)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.CaseCollision)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.DuplicateIdentity)]
    [InlineData(RecoveryTestFrame.Query, RecoveryTestFault.Missing)]
    [InlineData(RecoveryTestFrame.State, RecoveryTestFault.WrongVersion)]
    [InlineData(RecoveryTestFrame.State, RecoveryTestFault.InvalidUtf8)]
    [InlineData(RecoveryTestFrame.State, RecoveryTestFault.Oversize)]
    [InlineData(RecoveryTestFrame.State, RecoveryTestFault.Truncated)]
    public async Task InvalidRecoveryFramesReturnUnknownAndPreserveFiles(RecoveryTestFrame frame, RecoveryTestFault fault)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, protector, f.Identity, f.Offer, now);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            await peer.StagePeerV3IsolatedTestAsync(stage, f.Identity, f.Offer, candidate, 1, 24802, now, () => now);
            var markerPath = Path.Combine(hostRoot, "pending-prepare-unknown.bin"); var peerPath = Path.Combine(peerRoot, "peer-pending-invitation.bin");
            var marker = await File.ReadAllBytesAsync(markerPath); var pending = await File.ReadAllBytesAsync(peerPath);
            var controls = new RecoveryQueryControls { FaultFrame = frame, Fault = fault };
            Assert.Null(await IsolatedRecoveryQuery.RunAsync(host, peer, f.HostCertificate, f.PeerCertificate, now, controls));
            Assert.Equal(marker, await File.ReadAllBytesAsync(markerPath)); Assert.Equal(pending, await File.ReadAllBytesAsync(peerPath));
            Assert.Empty(host.GetTrustedDevices()); Assert.Null(peer.Get());
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task DefaultClockRechecksDeadlineAfterActualTimePasses()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector(); var barrier = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); var now = DateTimeOffset.UtcNow;
            var identity = f.Identity with { ExpiresAtUtc = now.AddSeconds(2) }; var offer = f.Offer;
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, protector, identity, offer, now);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            await peer.StagePeerV3IsolatedTestAsync(stage, identity, offer, candidate, 1, 24802, now, () => now);
            var running = IsolatedRecoveryQuery.RunAsync(host, peer, f.HostCertificate, f.PeerCertificate, now, new RecoveryQueryControls { BeforeHostFinal = barrier });
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (DateTimeOffset.UtcNow < identity.ExpiresAtUtc) await Task.Delay(20, timeout.Token);
                barrier.Release(); Assert.Null(await running.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally { barrier.Release(); }
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Theory]
    [InlineData("host-change")]
    [InlineData("peer-change")]
    [InlineData("rollback")]
    [InlineData("cancel")]
    public async Task RecoveryObservesChangeCancellationAndClockRollback(string scenario)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector(); var barrier = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); var now = f.Clock(); long ticks = now.UtcTicks;
            DateTimeOffset Clock() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, protector, f.Identity, f.Offer, now);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            await peer.StagePeerV3IsolatedTestAsync(stage, f.Identity, f.Offer, candidate, 1, 24802, now, Clock);
            var controls = new RecoveryQueryControls();
            if (scenario == "peer-change" || scenario == "cancel") controls.BeforePeerState = barrier; else controls.BeforeHostFinal = barrier;
            using var cancel = new CancellationTokenSource();
            var running = IsolatedRecoveryQuery.RunAsync(host, peer, f.HostCertificate, f.PeerCertificate, now, controls, Clock, cancel.Token);
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                if (scenario == "host-change") await host.AddConfirmedAsync("other", new string('D', 64));
                if (scenario == "peer-change") await peer.SaveConfirmedAsync(candidate, f.Identity.HostFingerprint);
                if (scenario == "rollback") Interlocked.Exchange(ref ticks, now.AddSeconds(-1).UtcTicks);
                if (scenario == "cancel") cancel.Cancel();
                barrier.Release();
                if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
                else Assert.Null(await running.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(File.Exists(Path.Combine(hostRoot, "pending-prepare-unknown.bin")));
                Assert.True(File.Exists(Path.Combine(peerRoot, "peer-pending-invitation.bin")));
            }
            finally { barrier.Release(); }
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task ExpiryDuringFinalProtectedReadCannotReturnUnexpiredObservation()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var windows = new WindowsCredentialProtector();
        var peerProtector = new RecoveryReadGateProtector(); var barrier = new ControlledLivePublishBarrier();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            await using var f = await Fixture.OpenAsync(); var now = f.Clock(); long ticks = now.UtcTicks;
            DateTimeOffset Clock() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, windows);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, peerProtector);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, windows, f.Identity, f.Offer, now);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, windows);
            await peer.StagePeerV3IsolatedTestAsync(stage, f.Identity, f.Offer, candidate, 1, 24802, now, Clock);
            var running = IsolatedRecoveryQuery.RunAsync(host, peer, f.HostCertificate, f.PeerCertificate, now, new RecoveryQueryControls { BeforeFinalPeerRecheck = barrier }, Clock);
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                peerProtector.BeforeUnprotect = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); };
                barrier.Release(); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Interlocked.Exchange(ref ticks, f.Identity.ExpiresAtUtc.UtcTicks); release.Set();
                Assert.Null(await running.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally { barrier.Release(); release.Set(); }
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    private sealed class RecoveryReadGateProtector : IMultiDeviceSecretProtector
    {
        private readonly WindowsCredentialProtector inner = new(); internal Action? BeforeUnprotect { get; set; }
        public byte[] Protect(byte[] plaintext) => inner.Protect(plaintext);
        public byte[] Unprotect(byte[] ciphertext) { BeforeUnprotect?.Invoke(); return inner.Unprotect(ciphertext); }
    }

    [Fact]
    public async Task OldRecoveryStateCannotBeReplayedAcrossNewMutualTlsConnection()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var controls = new RecoveryQueryControls { CaptureState = true };
        try
        {
            await using var f = await Fixture.OpenAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, protector, f.Identity, f.Offer, now);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            await peer.StagePeerV3IsolatedTestAsync(stage, f.Identity, f.Offer, candidate, 1, 24802, now, () => now);
            Assert.NotNull(await IsolatedRecoveryQuery.RunAsync(host, peer, f.HostCertificate, f.PeerCertificate, now, controls));
            Assert.NotNull(controls.CapturedState);
            var replay = new RecoveryQueryControls { ReplayState = controls.CapturedState };
            Assert.Null(await IsolatedRecoveryQuery.RunAsync(host, peer, f.HostCertificate, f.PeerCertificate, now, replay));
            Assert.Empty(host.GetTrustedDevices()); Assert.Null(peer.Get());
        }
        finally
        {
            if (controls.CapturedState is not null) CryptographicOperations.ZeroMemory(controls.CapturedState);
            CleanVault(hostRoot); CleanVault(peerRoot);
        }
    }

    [Fact]
    public async Task StalledRecoveryIsStoppedByOverallDeadlineWithoutChangingRecords()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, protector, f.Identity, f.Offer, now);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            await peer.StagePeerV3IsolatedTestAsync(stage, f.Identity, f.Offer, candidate, 1, 24802, now, () => now);
            var markerPath = Path.Combine(hostRoot, "pending-prepare-unknown.bin"); var before = await File.ReadAllBytesAsync(markerPath);
            var controls = new RecoveryQueryControls { StallPeerBeforeState = true, Timeout = TimeSpan.FromMilliseconds(500) };
            var started = System.Diagnostics.Stopwatch.StartNew();
            Assert.Null(await IsolatedRecoveryQuery.RunAsync(host, peer, f.HostCertificate, f.PeerCertificate, now, controls));
            Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));
            Assert.Equal(before, await File.ReadAllBytesAsync(markerPath)); Assert.Empty(host.GetTrustedDevices()); Assert.Null(peer.Get());
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Theory]
    [InlineData("missing-marker")]
    [InlineData("legacy-peer")]
    [InlineData("corrupt-peer")]
    public async Task MissingOrLegacyRecoveryInputsRemainUnknownAndUnchanged(string scenario)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = await PrepareOfflineHostAsync(host, hostRoot, protector, f.Identity, f.Offer, now);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            await peer.StagePeerV3IsolatedTestAsync(stage, f.Identity, f.Offer, candidate, 1, 24802, now, () => now);
            var markerPath = Path.Combine(hostRoot, "pending-prepare-unknown.bin"); var peerPath = Path.Combine(peerRoot, "peer-pending-invitation.bin");
            if (scenario == "missing-marker") File.Delete(markerPath);
            if (scenario == "corrupt-peer") await File.WriteAllBytesAsync(peerPath, [1, 2, 3]);
            if (scenario == "legacy-peer")
            {
                var plain = protector.Unprotect(await File.ReadAllBytesAsync(peerPath));
                try
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(plain)!; node["version"] = 2; node.AsObject().Remove("offer");
                    var changed = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node);
                    try { await File.WriteAllBytesAsync(peerPath, protector.Protect(changed)); }
                    finally { CryptographicOperations.ZeroMemory(changed); }
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            var before = await File.ReadAllBytesAsync(peerPath);
            Assert.Null(await IsolatedRecoveryQuery.RunAsync(host, peer, f.HostCertificate, f.PeerCertificate, now));
            Assert.Equal(before, await File.ReadAllBytesAsync(peerPath));
            Assert.Equal(scenario != "missing-marker", File.Exists(markerPath)); Assert.Empty(host.GetTrustedDevices()); Assert.Null(peer.Get());
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }
}
