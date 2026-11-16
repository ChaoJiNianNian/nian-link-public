using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Fact]
    public async Task RealPrepareV3LostReturnThenReopenAndNewMutualTlsQuery()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var beforeReady = new ControlledLivePublishBarrier(); var afterReady = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(throughProxy: true); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            string oldHost, pendingToken; byte[][] before;
            var files = new[] { Path.Combine(hostRoot, "multi-device-trust.bin"), Path.Combine(hostRoot, "pending-grant.bin"), Path.Combine(hostRoot, "pending-prepare-unknown.bin"), Path.Combine(peerRoot, "peer-invitation.bin"), Path.Combine(peerRoot, "peer-pending-invitation.bin") };
            var oldPeer = TestInvitation(f);
            await using (var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector))
            await using (var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector))
            {
                oldHost = await host.AddConfirmedAsync("old-device", new string('C', 64));
                await peer.SaveConfirmedAsync(oldPeer, f.Identity.HostFingerprint);
                var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
                var frozen = await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
                f.Peer.BeforeLiveReady = beforeReady; f.Peer.AfterLiveReadySent = afterReady;
                var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), writeV3: true);
                var send = host.SendPrepareAndReceiveReadyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
                try
                {
                    await beforeReady.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                    var staged = await peer.InspectPeerV3IsolatedTestAsync(now);
                    Assert.Equal("v3-history", staged.State); Assert.Equal(stage, staged.StageId); Assert.Equal(f.Offer, staged.Offer);
                    var peerSnapshot = await peer.CaptureOfflineRecoveryTestAsync(now); Assert.NotNull(peerSnapshot);
                    Assert.Equal(frozen.Slot, peerSnapshot.Binding.Slot); Assert.Equal(frozen.InvitationDigest, peerSnapshot.Binding.InvitationDigest);
                    var plain = protector.Unprotect(await File.ReadAllBytesAsync(files[1]));
                    try { using var json = JsonDocument.Parse(plain); pendingToken = json.RootElement.GetProperty("token").GetString()!; }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                    Assert.True(host.IsAuthorized("old-device", oldHost)); Assert.False(host.IsAuthorized(f.Identity.PeerName, pendingToken)); Assert.True(oldPeer == peer.Get());
                    f.Proxy!.DropPeerToHost(); beforeReady.Release();
                    await afterReady.Entered.WaitAsync(TimeSpan.FromSeconds(5)); await f.Proxy.Dropped.WaitAsync(TimeSpan.FromSeconds(5)); f.Proxy.CutConnections();
                    await Assert.ThrowsAnyAsync<IOException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
                    afterReady.Release(); Assert.Equal(stage, (await receive.WaitAsync(TimeSpan.FromSeconds(5))).StageId);
                    await f.Host.DisposeAsync(); await f.Peer.DisposeAsync();
                    before = await Task.WhenAll(files.Select(path => File.ReadAllBytesAsync(path)));
                }
                finally { beforeReady.Release(); afterReady.Release(); }
            }
            using var hostCertificate = PrivateFiles.LoadTlsCertificate(f.HostCertificatePath);
            using var peerCertificate = PrivateFiles.LoadTlsCertificate(f.PeerCertificatePath);
            Assert.NotSame(f.HostCertificate, hostCertificate); Assert.NotSame(f.PeerCertificate, peerCertificate);
            await using var hostAgain = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peerAgain = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var observation = await IsolatedRecoveryQuery.RunAsync(hostAgain, peerAgain, hostCertificate, peerCertificate, now, clock: () => now);
            Assert.NotNull(observation); Assert.True(observation.Matched); Assert.False(observation.HistoryExpired); Assert.Equal(2, observation.MarkerVersion);
            for (var i = 0; i < files.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(files[i]));
            Assert.True(hostAgain.IsAuthorized("old-device", oldHost)); Assert.False(hostAgain.IsAuthorized(f.Identity.PeerName, pendingToken));
            Assert.True(oldPeer == peerAgain.Get());
        }
        finally { beforeReady.Release(); afterReady.Release(); CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveV3CloseAndPublicationFollowSessionGateOrder(bool publishWins)
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        var publish = new ControlledLivePublishBarrier(); var ready = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            await using var peer = await PeerInvitationStore.OpenAsync(root, protector); var old = TestInvitation(f);
            await peer.SaveConfirmedAsync(old, f.Identity.HostFingerprint); var now = f.Clock(); var stage = Guid.NewGuid();
            if (publishWins) { f.Peer.BeforeLiveMove = publish; f.Peer.BeforeLiveReady = ready; }
            else peer.BeforeLivePeerPublish = publish;
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), writeV3: true);
            var frame = f.Host.MakePrepareFrameForTest(stage, TestInvitation(f), 1, 24802);
            try
            {
                await f.HostStream.WriteAsync(frame); await f.HostStream.WriteAsync(new byte[] { (byte)'\n' }); await f.HostStream.FlushAsync();
                await publish.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var closing = Task.Run(async () => { started.TrySetResult(); await f.Peer.DisposeAsync(); });
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (!publishWins)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while (f.Peer.Phase != PairingConsentPhase.Aborted) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
                }
                Assert.False(closing.IsCompleted); publish.Release();
                if (publishWins)
                {
                    await ready.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while (f.Peer.Phase != PairingConsentPhase.Aborted) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
                    ready.Release();
                }
                await Assert.ThrowsAsync<InvalidOperationException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                await closing.WaitAsync(TimeSpan.FromSeconds(5));
                var result = await peer.InspectPeerV3IsolatedTestAsync(now);
                if (publishWins) { Assert.Equal("v3-history", result.State); Assert.Equal(stage, result.StageId); }
                else Assert.Equal("unknown", result.State);
                Assert.True(old == peer.Get());
            }
            finally { publish.Release(); ready.Release(); CryptographicOperations.ZeroMemory(frame); }
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task LiveV3RequiresRealReceivedTicketAndCannotOverwriteLegacyPending()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var peer = await PeerInvitationStore.OpenAsync(root, protector); var candidate = TestInvitation(f);
            await peer.StagePendingAsync(f.Identity, candidate, f.Identity.PeerName, f.Identity.HostFingerprint, PeerInvitationStore.ComputePendingDigest(candidate), now, clock: new FixtureTimeProvider(f));
            var path = Path.Combine(root, "peer-pending-invitation.bin"); var before = await File.ReadAllBytesAsync(path);
            var capability = f.Peer.IssueIsolatedTestCapability();
            await Assert.ThrowsAsync<InvalidOperationException>(() => peer.StageLivePeerTestAsync(f.Peer, capability, Guid.NewGuid(), f.Identity, f.Offer, candidate, 1, 24802, writeV3: true));
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, capability, writeV3: true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => peer.StageLivePeerTestAsync(f.Peer, capability, Guid.NewGuid(), f.Identity, f.Offer, candidate, 1, 24802, protocolTicket: new object(), writeV3: true));
            var frame = f.Host.MakePrepareFrameForTest(Guid.NewGuid(), candidate, 1, 24802);
            try
            {
                await f.HostStream.WriteAsync(frame); await f.HostStream.WriteAsync(new byte[] { (byte)'\n' }); await f.HostStream.FlushAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally { CryptographicOperations.ZeroMemory(frame); }
            Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.NotNull(await peer.GetPendingMetadataAsync()); Assert.Null(peer.Get());
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expiry")]
    [InlineData("rollback")]
    [InlineData("move")]
    [InlineData("protect")]
    public async Task LiveV3FailureDoesNotActivateOrOverwriteOldInvitation(string scenario)
    {
        var root = VaultRoot(); var protector = new GateProtector(); var barrier = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var peer = await PeerInvitationStore.OpenAsync(root, protector); var old = TestInvitation(f);
            await peer.SaveConfirmedAsync(old, f.Identity.HostFingerprint);
            if (scenario == "protect") protector.BeforeProtect = () => throw new CryptographicException("Synthetic protector failure.");
            else peer.BeforeLivePeerPublish = barrier;
            using var cancel = new CancellationTokenSource();
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), cancel.Token, writeV3: true);
            var frame = f.Host.MakePrepareFrameForTest(Guid.NewGuid(), TestInvitation(f), 1, 24802);
            try
            {
                await f.HostStream.WriteAsync(frame); await f.HostStream.WriteAsync(new byte[] { (byte)'\n' }); await f.HostStream.FlushAsync();
                if (scenario != "protect")
                {
                    await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                    if (scenario == "cancel") cancel.Cancel();
                    if (scenario == "expiry") { Interlocked.Exchange(ref f.UtcTicks, f.Identity.ExpiresAtUtc.UtcTicks); Assert.Equal(PairingConsentPhase.Aborted, f.Peer.Phase); }
                    if (scenario == "rollback") { Interlocked.Exchange(ref f.UtcTicks, now.AddSeconds(-1).UtcTicks); Assert.Equal(PairingConsentPhase.Aborted, f.Peer.Phase); }
                    if (scenario == "move") await File.WriteAllBytesAsync(Path.Combine(root, "peer-pending-invitation.bin"), [1, 2, 3]);
                    barrier.Release();
                }
                if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                else if (scenario == "move") await Assert.ThrowsAnyAsync<IOException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                else if (scenario == "protect") await Assert.ThrowsAsync<CryptographicException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                else await Assert.ThrowsAsync<InvalidOperationException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal("unknown", (await peer.InspectPeerV3IsolatedTestAsync(now)).State); Assert.True(old == peer.Get());
                var path = Path.Combine(root, "peer-pending-invitation.bin");
                if (scenario == "move") Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
                else Assert.False(File.Exists(path));
            }
            finally { barrier.Release(); CryptographicOperations.ZeroMemory(frame); }
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("expiry")]
    public async Task LiveV3WaitingForPeerGateStopsWithoutPending(string scenario)
    {
        var root = VaultRoot(); var protector = new GateProtector();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var peer = await PeerInvitationStore.OpenAsync(root, protector); var old = TestInvitation(f);
            await peer.SaveConfirmedAsync(old, f.Identity.HostFingerprint);
            protector.BeforeProtect = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); };
            var blocker = Task.Run(() => peer.SaveConfirmedAsync(old, f.Identity.HostFingerprint));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); peer.BeforeLivePeerGateWait = waiting;
                using var cancel = new CancellationTokenSource();
                var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), cancel.Token, writeV3: true);
                var frame = f.Host.MakePrepareFrameForTest(Guid.NewGuid(), TestInvitation(f), 1, 24802);
                try { await f.HostStream.WriteAsync(frame); await f.HostStream.WriteAsync(new byte[] { (byte)'\n' }); await f.HostStream.FlushAsync(); }
                finally { CryptographicOperations.ZeroMemory(frame); }
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(receive.IsCompleted);
                if (scenario == "cancel") cancel.Cancel();
                if (scenario == "expiry") { Interlocked.Exchange(ref f.UtcTicks, f.Identity.ExpiresAtUtc.UtcTicks); Assert.Equal(PairingConsentPhase.Aborted, f.Peer.Phase); }
                if (scenario == "close") await f.Peer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.False(File.Exists(Path.Combine(root, "peer-pending-invitation.bin")));
            }
            finally { release.Set(); await blocker.WaitAsync(TimeSpan.FromSeconds(5)); }
            Assert.True(old == peer.Get()); Assert.Equal("unknown", (await peer.InspectPeerV3IsolatedTestAsync(now)).State);
        }
        finally { release.Set(); CleanVault(root); }
    }
}
