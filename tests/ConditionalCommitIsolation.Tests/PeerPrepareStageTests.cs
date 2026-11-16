using System.Net;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    private static MultiPeerInvitation TestInvitation(Fixture f) => new()
    {
        Host = IPAddress.Loopback.ToString(), Port = 24801, KeyboardPort = 24800, DataPort = 24802,
        HostName = f.Identity.HostName, PeerName = f.Identity.PeerName,
        CertificateSha256 = f.Identity.HostFingerprint, Token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
    };

    [Fact]
    public async Task LivePeerStagePersistsStageAndKeepsExistingInvitationInactive()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            var invitation = TestInvitation(f); var stage = Guid.NewGuid(); var key = f.Peer.IssueIsolatedTestCapability();
            await using (var store = await PeerInvitationStore.OpenAsync(root, protector))
            {
                var old = invitation with { Token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
                await store.SaveConfirmedAsync(old, f.Identity.HostFingerprint);
                await store.StageLivePeerTestAsync(f.Peer, key, stage, f.Identity, f.Offer, invitation, 1, 24802);
                Assert.True(old == store.Get());
                var metadata = await store.ReadLivePeerStageTestAsync();
                Assert.NotNull(metadata); Assert.Equal(stage, metadata.StageId); Assert.Equal(1, metadata.Slot);
                Assert.Equal(PeerInvitationStore.ComputePendingDigest(invitation), metadata.Digest);
                Assert.False(metadata.ToString().Contains(invitation.Token, StringComparison.Ordinal));
            }
            await using var reopened = await PeerInvitationStore.OpenAsync(root, protector);
            Assert.Equal(stage, (await reopened.ReadLivePeerStageTestAsync())!.StageId);
            Assert.False(string.Equals(invitation.Token, reopened.Get()!.Token, StringComparison.Ordinal));
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData("old-pending")]
    [InlineData("closed")]
    [InlineData("slot")]
    public async Task StageRejectsConflictsAndInvalidSessionWithoutChangingOldState(string kind)
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            var invitation = TestInvitation(f); var now = f.Clock(); var key = f.Peer.IssueIsolatedTestCapability();
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            if (kind == "old-pending") await store.StagePendingAsync(f.Identity, invitation, f.Identity.PeerName, f.Identity.HostFingerprint, PeerInvitationStore.ComputePendingDigest(invitation), now, clock: new FixtureTimeProvider(f));
            if (kind == "closed") await f.Peer.DisposeAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => store.StageLivePeerTestAsync(f.Peer, key, Guid.NewGuid(), f.Identity, f.Offer, invitation, kind == "slot" ? 2 : 1, 24802));
            if (kind == "old-pending") await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadLivePeerStageTestAsync());
            else Assert.Null(await store.ReadLivePeerStageTestAsync());
            Assert.Null(store.Get());
            if (kind == "old-pending") Assert.NotNull(await store.GetPendingMetadataAsync());
        }
        finally { CleanVault(root); }
    }

    private sealed class FixtureTimeProvider(Fixture fixture) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => fixture.Clock();
    }

    [Fact]
    public async Task PrepareAndReadyTravelOverConfirmedTlsAndDoNotActivateEitherSide()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var store = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var previous = TestInvitation(f); await store.SaveConfirmedAsync(previous, f.Identity.HostFingerprint);
            var oldHostToken = await vault.AddConfirmedAsync("old", new string('C', 64)); var now = f.Clock();
            var stage = (await vault.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            var frozen = await vault.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(store, f.Peer.IssueIsolatedTestCapability());
            var send = vault.SendPrepareAndReceiveReadyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            var results = await Task.WhenAll(receive, send).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(results, item => { Assert.Equal(stage, item.StageId); Assert.Equal(frozen.InvitationDigest, item.Digest); Assert.Equal(frozen.Slot, item.Slot); });
            Assert.True(previous == store.Get()); Assert.True(vault.IsAuthorized("old", oldHostToken));
            Assert.DoesNotContain(vault.GetTrustedDevices(), item => item.Name == f.Identity.PeerName);
            Assert.Equal(stage, (await store.ReadLivePeerStageTestAsync())!.StageId);
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Theory]
    [InlineData("version", false)] [InlineData("version", true)]
    [InlineData("role", false)] [InlineData("role", true)]
    [InlineData("extra", false)] [InlineData("extra", true)]
    [InlineData("duplicate", false)] [InlineData("duplicate", true)]
    [InlineData("case", false)] [InlineData("case", true)]
    [InlineData("slot", false)] [InlineData("slot", true)]
    [InlineData("digest", false)] [InlineData("digest", true)]
    [InlineData("lower-digest", false)] [InlineData("lower-digest", true)]
    [InlineData("utf8", false)] [InlineData("utf8", true)]
    [InlineData("oversized", false)] [InlineData("oversized", true)]
    public async Task BadPrepareFramesFailClosedWithoutStaging(string defect, bool writeV3)
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            var candidate = TestInvitation(f); var raw = f.Host.MakePrepareFrameForTest(Guid.NewGuid(), candidate, 1, 24802);
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(raw);
                json = defect switch
                {
                    "version" => json.Replace("\"version\":1", "\"version\":3", StringComparison.Ordinal),
                    "role" => json.Replace("\"role\":\"host\"", "\"role\":\"peer\"", StringComparison.Ordinal),
                    "extra" => json.Replace("\"type\":\"prepare\"", "\"type\":\"prepare\",\"extra\":1", StringComparison.Ordinal),
                    "duplicate" => json.Replace("\"type\":\"prepare\"", "\"type\":\"prepare\",\"type\":\"prepare\"", StringComparison.Ordinal),
                    "case" => json.Replace("\"type\":\"prepare\"", "\"type\":\"prepare\",\"Type\":\"prepare\"", StringComparison.Ordinal),
                    "slot" => json.Replace("\"slot\":1", "\"slot\":2", StringComparison.Ordinal),
                    "digest" => json.Replace(PeerInvitationStore.ComputePendingDigest(candidate), new string('A', 64), StringComparison.Ordinal),
                    "lower-digest" => json.Replace(PeerInvitationStore.ComputePendingDigest(candidate), PeerInvitationStore.ComputePendingDigest(candidate).ToLowerInvariant(), StringComparison.Ordinal),
                    _ => json
                };
                byte[] bytes = defect switch
                {
                    "utf8" => [0xff, (byte)'\n'],
                    "oversized" => Enumerable.Repeat((byte)'x', 8192).Append((byte)'\n').ToArray(),
                    _ => System.Text.Encoding.UTF8.GetBytes(json + "\n")
                };
                try
                {
                    var receive = f.Peer.ReceivePrepareAndStageTestAsync(store, f.Peer.IssueIsolatedTestCapability(), writeV3: writeV3);
                    await f.HostStream.WriteAsync(bytes); await f.HostStream.FlushAsync();
                    await Assert.ThrowsAsync<InvalidDataException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Null(await store.ReadLivePeerStageTestAsync()); Assert.Null(store.Get());
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw); }
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CapturedPrepareCannotCrossToSecondIdenticalIdentityConnection(bool writeV3)
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var a = await Fixture.OpenAsync(); await using var b = await Fixture.OpenAsync(sameIdentity: a);
            await a.BeginAsync(); await a.ConfirmAsync(); await b.BeginAsync(); await b.ConfirmAsync(); Assert.NotEqual(a.Port, b.Port);
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            var raw = a.Host.MakePrepareFrameForTest(Guid.NewGuid(), TestInvitation(a), 1, 24802);
            try
            {
                var receive = b.Peer.ReceivePrepareAndStageTestAsync(store, b.Peer.IssueIsolatedTestCapability(), writeV3: writeV3);
                await b.HostStream.WriteAsync(raw); await b.HostStream.WriteAsync(new byte[] { (byte)'\n' }); await b.HostStream.FlushAsync();
                await Assert.ThrowsAsync<InvalidDataException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Null(await store.ReadLivePeerStageTestAsync());
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw); }
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task CancellingPendingBeforePrepareSendMustNotStageAtPeer()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var barrier = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var store = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var now = f.Clock(); var stage = (await vault.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await vault.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            vault.BeforeLivePrepareSend = barrier;
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(store, f.Peer.IssueIsolatedTestCapability());
            var send = vault.SendPrepareAndReceiveReadyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<InvalidOperationException>(() => vault.AbortPendingGrantAsync(stage, f.Identity));
                var close = Task.Run(async () => await f.Host.DisposeAsync());
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    while (f.Host.Phase != PairingConsentPhase.Aborted) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
                barrier.Release();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
                await Assert.ThrowsAnyAsync<IOException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                await close.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(await store.ReadLivePeerStageTestAsync());
                Assert.True(File.Exists(Path.Combine(hostRoot, "pending-prepare-unknown.bin")));
            }
            finally { barrier.Release(); }
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task PendingReceiveReservesCapabilityBeforeWaitingForPrepare()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            var key = f.Peer.IssueIsolatedTestCapability(); using var cts = new CancellationTokenSource();
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(store, key, cts.Token);
            var receiver = new ControlledConsentTestReceiver(); receiver.Release();
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Peer.ReceivePrepareAndStageTestAsync(store, key));
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Peer.RunIsolatedTestActionAsync(key, receiver));
            }
            finally
            {
                cts.Cancel();
                try { await receive.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
            }
            Assert.Equal(0, receiver.Completed);
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task PendingReceiveCannotBeHijackedToStageUnreceivedInvitation()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            var key = f.Peer.IssueIsolatedTestCapability(); using var cts = new CancellationTokenSource();
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(store, key, cts.Token);
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.StageLivePeerTestAsync(f.Peer, key, Guid.NewGuid(), f.Identity, f.Offer, TestInvitation(f), 1, 24802, protocolTicket: new object()));
                Assert.Null(await store.ReadLivePeerStageTestAsync());
            }
            finally { cts.Cancel(); try { await receive.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task LivePublishHelperRejectsForeignPaths()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var key = f.Host.IssueIsolatedTestCapability(); f.Host.BeginLiveVaultTest(key, f.Identity, f.Offer);
            try
            {
                var source = Path.Combine(root, "foreign.tmp"); var target = Path.Combine(root, "foreign.bin");
                await File.WriteAllBytesAsync(source, [1, 2, 3]);
                Assert.Throws<InvalidOperationException>(() => f.Host.PublishLiveVaultTest(key, vault, source, target, CancellationToken.None));
                Assert.True(File.Exists(source)); Assert.False(File.Exists(target));
            }
            finally { f.Host.FinishLiveVaultTest(); }
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            var peerKey = f.Peer.IssueIsolatedTestCapability(); f.Peer.BeginLivePeerStageTest(peerKey, f.Identity, f.Offer);
            try
            {
                var source = Path.Combine(root, "peer-foreign.tmp"); var target = Path.Combine(root, "peer-foreign.bin");
                await File.WriteAllBytesAsync(source, [4, 5, 6]);
                Assert.Throws<InvalidOperationException>(() => f.Peer.PublishLivePeerStageTest(peerKey, store, source, target, CancellationToken.None));
                Assert.True(File.Exists(source)); Assert.False(File.Exists(target));
            }
            finally { f.Peer.FinishLiveVaultTest(); }
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public void PublishPathGuardRejectsBadSourceBadTargetAndNonisolatedRoot()
    {
        var root = VaultRoot(); var target = Path.Combine(root, "multi-device-trust.bin");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        IsolatedTestPathGuard.Validate(root, temporary, target, "multi-device-trust.bin");
        Assert.Throws<InvalidOperationException>(() => IsolatedTestPathGuard.Validate(root, Path.Combine(root, "wrong.tmp"), target, "multi-device-trust.bin"));
        Assert.Throws<InvalidOperationException>(() => IsolatedTestPathGuard.Validate(root, temporary, Path.Combine(root, "wrong.bin"), "multi-device-trust.bin"));
        var otherRoot = Path.Combine(Path.GetTempPath(), "not-an-isolated-vault");
        var otherTarget = Path.Combine(otherRoot, "multi-device-trust.bin");
        Assert.Throws<InvalidOperationException>(() => IsolatedTestPathGuard.Validate(otherRoot, otherTarget + "." + Guid.NewGuid().ToString("N") + ".tmp", otherTarget, "multi-device-trust.bin"));
    }

    [Fact]
    public void PublishPathGuardRejectsDirectoryJunctions()
    {
        var actual = VaultRoot(); var link = VaultRoot(); Directory.CreateDirectory(actual);
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("/c"); info.ArgumentList.Add("mklink"); info.ArgumentList.Add("/J"); info.ArgumentList.Add(link); info.ArgumentList.Add(actual);
            using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("Unable to create test junction.");
            if (!process.WaitForExit(5000) || process.ExitCode != 0) throw new IOException("Unable to create test junction.");
            var target = Path.Combine(link, "multi-device-trust.bin");
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Assert.Throws<IOException>(() => IsolatedTestPathGuard.Validate(link, temporary, target, "multi-device-trust.bin"));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); CleanVault(actual); }
    }

    [Fact]
    public async Task DirectPrepareExchangeWithoutVaultRegistrationIsRejected()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var candidate = TestInvitation(f); var key = f.Host.IssueIsolatedTestCapability();
            var payload = new LivePreparePayload(Guid.NewGuid(), 1, 24802, PeerInvitationStore.ComputePendingDigest(candidate), candidate);
            f.Host.BeginLiveVaultTest(key, f.Identity, f.Offer);
            try
            {
                var receive = f.Peer.ReceivePrepareAndStageTestAsync(store, f.Peer.IssueIsolatedTestCapability());
                var send = f.Host.ExchangePrepareLiveTestAsync(key, vault, new object(), payload, CancellationToken.None);
                await Assert.ThrowsAsync<InvalidOperationException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
                await Assert.ThrowsAnyAsync<IOException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Null(await store.ReadLivePeerStageTestAsync());
            }
            finally { f.Host.FinishLiveVaultTest(); }
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task DisposeKeepsInstanceLeaseUntilSendDrainsAndRestartPreservesUnknown()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var barrier = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            var vault = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var store = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var now = f.Clock(); var stage = (await vault.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await vault.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            vault.BeforeLivePrepareSend = barrier;
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(store, f.Peer.IssueIsolatedTestCapability());
            var send = vault.SendPrepareAndReceiveReadyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                var closing = vault.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
                await Assert.ThrowsAnyAsync<IOException>(async () => { await using var blocked = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector); });
                barrier.Release();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
                await Assert.ThrowsAnyAsync<IOException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                await closing.WaitAsync(TimeSpan.FromSeconds(5));
                await using var reopened = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
                await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AbortPendingGrantAsync(stage, f.Identity));
                await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock));
                await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AbortIsolatedAsync(stage, f.Identity));
                Assert.NotNull(await reopened.GetPendingGrantMetadataAsync(now));
                Assert.Null(store.Get()); Assert.Null(await store.ReadLivePeerStageTestAsync());
            }
            finally { barrier.Release(); await vault.DisposeAsync(); }
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task DisposeStopFailureStillDrainsBeforeReleasingInstanceLease()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var barrier = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            var vault = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var store = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var now = f.Clock(); var stage = (await vault.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await vault.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            vault.BeforeLivePrepareSend = barrier; f.Host.ThrowAfterLivePrepareStop = true;
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(store, f.Peer.IssueIsolatedTestCapability());
            var send = vault.SendPrepareAndReceiveReadyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                var closing = vault.DisposeAsync().AsTask();
                await Assert.ThrowsAnyAsync<IOException>(async () => { await using var blocked = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector); });
                barrier.Release();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
                await Assert.ThrowsAnyAsync<IOException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                await Assert.ThrowsAsync<IOException>(() => closing.WaitAsync(TimeSpan.FromSeconds(5)));
                await using var reopened = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
                await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AbortPendingGrantAsync(stage, f.Identity));
            }
            finally { barrier.Release(); try { await vault.DisposeAsync(); } catch (IOException) { } }
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeerCloseAroundReadyKeepsPendingAndReportsOnlyObservedOutcome(bool readyAlreadySent)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var barrier = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var store = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = TestInvitation(f); await store.SaveConfirmedAsync(old, f.Identity.HostFingerprint);
            var now = f.Clock(); var stage = (await vault.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await vault.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            if (readyAlreadySent) f.Peer.AfterLiveReadySent = barrier; else f.Peer.BeforeLiveReady = barrier;
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(store, f.Peer.IssueIsolatedTestCapability());
            var send = vault.SendPrepareAndReceiveReadyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                if (readyAlreadySent) Assert.Equal(stage, (await send.WaitAsync(TimeSpan.FromSeconds(5))).StageId);
                var closing = Task.Run(async () => await f.Peer.DisposeAsync());
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    while (f.Peer.Phase != PairingConsentPhase.Aborted) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
                Assert.False(closing.IsCompleted); barrier.Release();
                if (readyAlreadySent) Assert.Equal(stage, (await receive.WaitAsync(TimeSpan.FromSeconds(5))).StageId);
                else
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
                    await Assert.ThrowsAnyAsync<IOException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                await closing.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(stage, (await store.ReadLivePeerStageTestAsync())!.StageId); Assert.True(old == store.Get());
                Assert.NotNull(await vault.GetPendingGrantMetadataAsync(now));
                await Assert.ThrowsAsync<InvalidOperationException>(() => vault.AbortPendingGrantAsync(stage, f.Identity));
                Assert.True(File.Exists(Path.Combine(hostRoot, "pending-prepare-unknown.bin")));
            }
            finally { barrier.Release(); }
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task DamagedUnknownMarkerAfterReopenPreservesMarkerAndPending()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock(); Guid stage;
            await using (var vault = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector))
            await using (var store = await PeerInvitationStore.OpenAsync(peerRoot, protector))
            {
                stage = (await vault.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
                await vault.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
                var receive = f.Peer.ReceivePrepareAndStageTestAsync(store, f.Peer.IssueIsolatedTestCapability());
                var send = vault.SendPrepareAndReceiveReadyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
                await Task.WhenAll(receive, send).WaitAsync(TimeSpan.FromSeconds(5));
            }
            var marker = Path.Combine(hostRoot, "pending-prepare-unknown.bin"); var pending = Path.Combine(hostRoot, "pending-grant.bin");
            var pendingBefore = await File.ReadAllBytesAsync(pending); byte[] damaged = [1, 2, 3, 4]; await File.WriteAllBytesAsync(marker, damaged);
            await using var reopened = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AbortPendingGrantAsync(stage, f.Identity));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AbortIsolatedAsync(stage, f.Identity));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock));
            Assert.Equal(damaged, await File.ReadAllBytesAsync(marker)); Assert.Equal(pendingBefore, await File.ReadAllBytesAsync(pending));
            Assert.Empty(reopened.GetTrustedDevices());
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task MaximumPrepareFrameSucceedsAndReadyDoesNotRevealToken()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            var candidate = TestInvitation(f); var raw = f.Host.MakePrepareFrameForTest(Guid.NewGuid(), candidate, 1, 24802);
            var frame = new byte[8192];
            try
            {
                Assert.True(raw.Length < frame.Length - 1); raw.CopyTo(frame, 0);
                Array.Fill(frame, (byte)' ', raw.Length, frame.Length - raw.Length - 1); frame[^1] = (byte)'\n';
                var receive = f.Peer.ReceivePrepareAndStageTestAsync(store, f.Peer.IssueIsolatedTestCapability());
                await f.HostStream.WriteAsync(frame); await f.HostStream.FlushAsync();
                var ready = await JsonLineProtocol.ReadAsync(f.HostStream);
                Assert.NotNull(ready); Assert.False(ready.Contains(candidate.Token, StringComparison.Ordinal));
                await receive.WaitAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(await store.ReadLivePeerStageTestAsync());
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw); System.Security.Cryptography.CryptographicOperations.ZeroMemory(frame); }
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task SecretBearingTestRecordsHaveRedactedDefaultDisplay()
    {
        await using var f = await Fixture.OpenAsync(); var candidate = TestInvitation(f);
        var digest = PeerInvitationStore.ComputePendingDigest(candidate);
        var payload = new LivePreparePayload(Guid.NewGuid(), 1, 24802, digest, candidate);
        var docType = typeof(PeerInvitationStore).GetNestedType("LiveStageDocument", System.Reflection.BindingFlags.NonPublic)!;
        var doc = Activator.CreateInstance(docType, 2, Guid.NewGuid(), f.Identity, 1, 24802, candidate, digest, null)!;
        Assert.False(payload.ToString().Contains(candidate.Token, StringComparison.Ordinal)); Assert.False(doc.ToString()!.Contains(candidate.Token, StringComparison.Ordinal));
    }

    [Fact]
    public void SourceReviewDigestNormalizesOnlyCrLfPairs()
    {
        var utf8 = System.Text.Encoding.UTF8;
        Assert.Equal(SourceReviewDigest(utf8.GetBytes("alpha\r\nbeta\r\n")), SourceReviewDigest(utf8.GetBytes("alpha\nbeta\n")));
        Assert.NotEqual(SourceReviewDigest(utf8.GetBytes("alpha\nbeta\n")), SourceReviewDigest(utf8.GetBytes("alpha\nbeta!\n")));
        Assert.NotEqual(SourceReviewDigest(utf8.GetBytes("alpha\nbeta\n")), SourceReviewDigest(utf8.GetBytes("alpha\rbeta\n")));
    }

    private static string SourceReviewDigest(byte[] source)
    {
        // Git may check out LF as CRLF. Preserve every other byte, including lone CRs.
        var canonical = new byte[source.Length];
        var written = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == (byte)'\r' && i + 1 < source.Length && source[i + 1] == (byte)'\n') continue;
            canonical[written++] = source[i];
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(canonical.AsSpan(0, written)));
    }

    [Theory]
    // Review baselines, not a claim that historical test variants equal production:
    // see docs/Conditional隔离测试源码缺口.md for differences and production coverage.
    [InlineData("MultiDeviceCredentialVault.cs", "1A8624F45044AA550A2BAF04C12F303CB79FA88AA484F9062327BE8526973CFB")]
    [InlineData("MultiDeviceCredentialVault.PendingGrant.cs", "529C5CB702E3D09A01E68394EF5F2202E49ABEA348BDEDBB66C25D749EC0E380")]
    [InlineData("MultiDeviceCredentialVault.PendingFreeze.cs", "7147A07C84637BEA21EA8EFEDD3192EC3C712675A73B8A23E8B9408DA278FC0A")]
    [InlineData("PeerInvitationStore.Pending.cs", "7978C6A0241B875077EE942FAA595995A945094FD53C292C3FE87DA4B301B708")]
    public void TestVariantsMustBeReviewedWhenProductSourcesChange(string file, string expectedHash)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "DesktopToolbox.DeskLink", file))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllBytes(Path.Combine(root.FullName, "src", "DesktopToolbox.DeskLink", file));
        Assert.Equal(expectedHash, SourceReviewDigest(source));
    }

    [Fact]
    public async Task CorruptStageWithNullInvitationIsRejectedAndPreserved()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            var candidate = TestInvitation(f);
            await store.StageLivePeerTestAsync(f.Peer, f.Peer.IssueIsolatedTestCapability(), Guid.NewGuid(), f.Identity, f.Offer, candidate, 1, 24802);
            var path = Path.Combine(root, "peer-pending-invitation.bin");
            var plain = protector.Unprotect(await File.ReadAllBytesAsync(path));
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(plain)!; node["invitation"] = null;
                var malformed = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node);
                try { await File.WriteAllBytesAsync(path, protector.Protect(malformed)); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(malformed); }
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
            var before = await File.ReadAllBytesAsync(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadLivePeerStageTestAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetPendingMetadataAsync());
            Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Null(store.Get());
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task LegacyPendingCannotReadOverwriteOrDeleteStageBoundRecord()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            var candidate = TestInvitation(f); var stage = Guid.NewGuid(); var digest = PeerInvitationStore.ComputePendingDigest(candidate);
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            await store.StageLivePeerTestAsync(f.Peer, f.Peer.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, candidate, 1, 24802);
            var path = Path.Combine(root, "peer-pending-invitation.bin"); var before = await File.ReadAllBytesAsync(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetPendingMetadataAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() => store.AbortPendingAsync(f.Identity, digest));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.StagePendingAsync(f.Identity, candidate, f.Identity.PeerName, f.Identity.HostFingerprint, digest, f.Clock(), clock: new FixtureTimeProvider(f)));
            Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Equal(stage, (await store.ReadLivePeerStageTestAsync())!.StageId); Assert.Null(store.Get());
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeerStagePublicationAndCloseAreOrdered(bool publishWins)
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var barrier = new ControlledLivePublishBarrier();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            var candidate = TestInvitation(f); var key = f.Peer.IssueIsolatedTestCapability(); var stage = Guid.NewGuid();
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            if (publishWins) f.Peer.BeforeLiveMove = barrier; else store.BeforeLivePeerPublish = barrier;
            var staging = Task.Run(() => store.StageLivePeerTestAsync(f.Peer, key, stage, f.Identity, f.Offer, candidate, 1, 24802));
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var closing = Task.Run(async () => { started.TrySetResult(); await f.Peer.DisposeAsync(); });
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (!publishWins)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while (f.Peer.Phase != PairingConsentPhase.Aborted) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
                }
                Assert.False(closing.IsCompleted); barrier.Release();
                if (publishWins) await staging.WaitAsync(TimeSpan.FromSeconds(5));
                else await Assert.ThrowsAsync<InvalidOperationException>(() => staging.WaitAsync(TimeSpan.FromSeconds(5)));
                await closing.WaitAsync(TimeSpan.FromSeconds(5));
                var result = await store.ReadLivePeerStageTestAsync();
                if (publishWins) Assert.Equal(stage, result!.StageId); else Assert.Null(result);
                Assert.Null(store.Get());
            }
            finally { barrier.Release(); }
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task ExistingStageCannotBeOverwrittenByAnotherSession()
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await using var other = await Fixture.OpenAsync(sameIdentity: f);
            await f.BeginAsync(); await f.ConfirmAsync(); await other.BeginAsync(); await other.ConfirmAsync();
            var candidate = TestInvitation(f); var first = Guid.NewGuid();
            await using var store = await PeerInvitationStore.OpenAsync(root, protector);
            await store.StageLivePeerTestAsync(f.Peer, f.Peer.IssueIsolatedTestCapability(), first, f.Identity, f.Offer, candidate, 1, 24802);
            var before = await File.ReadAllBytesAsync(Path.Combine(root, "peer-pending-invitation.bin"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.StageLivePeerTestAsync(other.Peer, other.Peer.IssueIsolatedTestCapability(), Guid.NewGuid(), other.Identity, other.Offer, candidate, 1, 24802));
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(root, "peer-pending-invitation.bin")));
            Assert.Equal(first, (await store.ReadLivePeerStageTestAsync())!.StageId);
        }
        finally { CleanVault(root); }
    }
}
