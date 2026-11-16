using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyRealV3ReadyOnSameTlsCanCommitFrozenGrant(bool v3Ready)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var oldHost = await host.AddConfirmedAsync("old-device", new string('C', 64));
            var oldPeer = TestInvitation(f); await peer.SaveConfirmedAsync(oldPeer, f.Identity.HostFingerprint);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var pending = protector.Unprotect(await File.ReadAllBytesAsync(Path.Combine(hostRoot, "pending-grant.bin"))); string token;
            try { using var json = JsonDocument.Parse(pending); token = json.RootElement.GetProperty("token").GetString()!; }
            finally { CryptographicOperations.ZeroMemory(pending); }
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), writeV3: true, readyV3: v3Ready);
            var commit = host.SendPrepareReadyAndCommitTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            if (v3Ready)
            {
                Assert.Equal("committed", await commit.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(stage, (await receive.WaitAsync(TimeSpan.FromSeconds(5))).StageId);
                Assert.True(host.IsAuthorized(f.Identity.PeerName, token));
                Assert.Equal("v3-history", (await peer.InspectPeerV3IsolatedTestAsync(now)).State);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => commit.WaitAsync(TimeSpan.FromSeconds(5)));
                await receive.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(host.IsAuthorized(f.Identity.PeerName, token));
            }
            Assert.True(host.IsAuthorized("old-device", oldHost)); Assert.True(oldPeer == peer.Get());
            Assert.True(File.Exists(Path.Combine(hostRoot, "pending-prepare-unknown.bin")));
            Assert.True(File.Exists(Path.Combine(hostRoot, "pending-grant.bin")));
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("marker")]
    [InlineData("cancel")]
    [InlineData("expiry")]
    [InlineData("close")]
    public async Task ReadyCommitStopsBeforePublicationWhenLocalEvidenceOrSessionChanges(string scenario)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var beforePublish = new ControlledLivePublishBarrier(); using var cancel = new CancellationTokenSource();
        Task? commit = null; Task? close = null;
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = await host.AddConfirmedAsync("old-device", new string('C', 64));
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var activePath = Path.Combine(hostRoot, "multi-device-trust.bin"); var active = await File.ReadAllBytesAsync(activePath);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            host.BeforeLivePublish = beforePublish;
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), writeV3: true, readyV3: true);
            commit = host.SendPrepareReadyAndCommitTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock, cancel.Token);
            try
            {
                await beforePublish.Entered.WaitAsync(TimeSpan.FromSeconds(5)); await receive.WaitAsync(TimeSpan.FromSeconds(5));
                if (scenario is "pending" or "marker")
                {
                    var path = Path.Combine(hostRoot, scenario == "pending" ? "pending-grant.bin" : "pending-prepare-unknown.bin");
                    var plain = protector.Unprotect(await File.ReadAllBytesAsync(path));
                    try { await File.WriteAllBytesAsync(path, protector.Protect(plain)); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                if (scenario == "cancel") cancel.Cancel();
                if (scenario == "expiry") { Interlocked.Exchange(ref f.UtcTicks, f.Identity.ExpiresAtUtc.UtcTicks); Assert.Equal(PairingConsentPhase.Aborted, f.Host.Phase); }
                if (scenario == "close")
                {
                    close = Task.Run(async () => await f.Host.DisposeAsync());
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while (f.Host.Phase != PairingConsentPhase.Aborted) { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
                }
                beforePublish.Release();
                var error = await Record.ExceptionAsync(() => commit.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error is InvalidOperationException or OperationCanceledException, $"Unexpected result: {error}");
                if (close is not null) await close.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(active, await File.ReadAllBytesAsync(activePath));
                Assert.True(host.IsAuthorized("old-device", old)); Assert.False(host.IsAuthorized(f.Identity.PeerName, candidate.Token));
                Assert.True(File.Exists(Path.Combine(hostRoot, "pending-prepare-unknown.bin")));
                Assert.Equal("v3-history", (await peer.InspectPeerV3IsolatedTestAsync(now)).State);
            }
            finally { beforePublish.Release(); cancel.Cancel(); if (commit is not null) { try { await commit.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } } }
        }
        finally { beforePublish.Release(); CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task ReadyCapabilityCannotBeUsedFromAnotherSessionOrReused()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var a = await Fixture.OpenAsync(); await a.BeginAsync(); await a.ConfirmAsync(); var now = a.Clock();
            await using var b = await Fixture.OpenAsync(); await b.BeginAsync(); await b.ConfirmAsync();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = (await host.StagePendingGrantAsync(a.Identity, a.Offer, now, clock: a.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, a.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: a.Clock);
            var wrong = b.Host.IssueIsolatedTestCapability();
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.SendPrepareReadyAndCommitTestAsync(a.Host, wrong, stage, a.Identity, a.Offer, now, a.Clock));
            Assert.False(File.Exists(Path.Combine(hostRoot, "pending-prepare-unknown.bin"))); Assert.Empty(host.GetTrustedDevices());
            var right = a.Host.IssueIsolatedTestCapability();
            var receive = a.Peer.ReceivePrepareAndStageTestAsync(peer, a.Peer.IssueIsolatedTestCapability(), writeV3: true, readyV3: true);
            Assert.Equal("committed", await host.SendPrepareReadyAndCommitTestAsync(a.Host, right, stage, a.Identity, a.Offer, now, a.Clock));
            await receive.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.SendPrepareReadyAndCommitTestAsync(a.Host, right, stage, a.Identity, a.Offer, now, a.Clock));
            Assert.Single(host.GetTrustedDevices());
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task RestoringOriginalPendingAfterAChangedSnapshotWasReadCannotCommit()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var beforeReady = new ControlledLivePublishBarrier(); var afterRead = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = await host.AddConfirmedAsync("old-device", new string('C', 64));
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            var path = Path.Combine(hostRoot, "pending-grant.bin"); var original = await File.ReadAllBytesAsync(path);
            f.Peer.BeforeLiveReady = beforeReady; host.AfterReadyPendingRead = afterRead;
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), writeV3: true, readyV3: true);
            var commit = host.SendPrepareReadyAndCommitTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            try
            {
                await beforeReady.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                var plain = protector.Unprotect(original);
                try { await File.WriteAllBytesAsync(path, protector.Protect(plain)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                beforeReady.Release(); await afterRead.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                await File.WriteAllBytesAsync(path, original); afterRead.Release();
                await Assert.ThrowsAsync<InvalidOperationException>(() => commit.WaitAsync(TimeSpan.FromSeconds(5)));
                await receive.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(host.IsAuthorized("old-device", old)); Assert.False(host.IsAuthorized(f.Identity.PeerName, candidate.Token));
            }
            finally { beforeReady.Release(); afterRead.Release(); }
        }
        finally { beforeReady.Release(); afterRead.Release(); CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expiry")]
    public async Task InterruptionAfterLastCheckMayStillPublishAndMustBeReportedAsCommitted(string interruption)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var afterCheck = new ControlledLivePublishBarrier(); using var cancellation = new CancellationTokenSource();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            f.Host.AfterLiveMoveCheck = afterCheck;
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), writeV3: true, readyV3: true);
            var key = f.Host.IssueIsolatedTestCapability();
            var commit = Task.Run(() => host.SendPrepareReadyAndCommitTestAsync(f.Host, key, stage, f.Identity, f.Offer, now, f.Clock, cancellation.Token));
            try
            {
                await afterCheck.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                if (interruption == "cancel") cancellation.Cancel();
                else Interlocked.Exchange(ref f.UtcTicks, f.Identity.ExpiresAtUtc.UtcTicks);
                afterCheck.Release();
                Assert.Equal("committed", await commit.WaitAsync(TimeSpan.FromSeconds(5)));
                await receive.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(host.IsAuthorized(f.Identity.PeerName, candidate.Token));
            }
            finally { afterCheck.Release(); }
        }
        finally { afterCheck.Release(); CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task LostReadyDoesNotCommitAndLeavesProtectedEvidenceForRecovery()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var beforeReady = new ControlledLivePublishBarrier(); var afterReady = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(throughProxy: true); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = await host.AddConfirmedAsync("old-device", new string('C', 64));
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            f.Peer.BeforeLiveReady = beforeReady; f.Peer.AfterLiveReadySent = afterReady;
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), writeV3: true, readyV3: true);
            var commit = host.SendPrepareReadyAndCommitTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            try
            {
                await beforeReady.Entered.WaitAsync(TimeSpan.FromSeconds(5)); f.Proxy!.DropPeerToHost(); beforeReady.Release();
                await afterReady.Entered.WaitAsync(TimeSpan.FromSeconds(5)); await f.Proxy.Dropped.WaitAsync(TimeSpan.FromSeconds(5)); f.Proxy.CutConnections();
                await Assert.ThrowsAnyAsync<IOException>(() => commit.WaitAsync(TimeSpan.FromSeconds(5)));
                afterReady.Release(); await receive.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(host.IsAuthorized("old-device", old)); Assert.False(host.IsAuthorized(f.Identity.PeerName, candidate.Token));
                Assert.True(File.Exists(Path.Combine(hostRoot, "pending-prepare-unknown.bin")));
                Assert.Equal("v3-history", (await peer.InspectPeerV3IsolatedTestAsync(now)).State);
            }
            finally { beforeReady.Release(); afterReady.Release(); }
        }
        finally { beforeReady.Release(); afterReady.Release(); CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task ProtectionFailureAfterReadyPreservesOldGrantAndPending()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new GateProtector(); var peerProtector = new WindowsCredentialProtector();
        var beforeReady = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, peerProtector);
            var old = await host.AddConfirmedAsync("old-device", new string('C', 64));
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, peerProtector);
            f.Peer.BeforeLiveReady = beforeReady;
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), writeV3: true, readyV3: true);
            var commit = host.SendPrepareReadyAndCommitTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            try
            {
                await beforeReady.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                protector.BeforeProtect = () => throw new CryptographicException("Synthetic protect failure."); beforeReady.Release();
                await Assert.ThrowsAsync<CryptographicException>(() => commit.WaitAsync(TimeSpan.FromSeconds(5)));
                await receive.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(host.IsAuthorized("old-device", old)); Assert.False(host.IsAuthorized(f.Identity.PeerName, candidate.Token));
                Assert.True(File.Exists(Path.Combine(hostRoot, "pending-prepare-unknown.bin")));
                Assert.True(File.Exists(Path.Combine(hostRoot, "pending-grant.bin")));
            }
            finally { beforeReady.Release(); }
        }
        finally { beforeReady.Release(); CleanVault(hostRoot); CleanVault(peerRoot); }
    }
}
