using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Services;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task StopDuringStartOrFailedCleanupDoesNotAuthorizeNewSession(int scenario)
    {
        using var folder = new RecoveryDirectory();
        var root = System.IO.Path.Combine(folder.Path, "mac");
        var identities = new RecoveryIdentityVault();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "host-vault"), new SyntheticProtector());
        var dataPort = RecoveryPortBlock();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "host-runtime"), "host",
            IPAddress.Loopback, dataPort + 4, dataPort, peers => new MultiKeyboard(peers));
        var fingerprint = await runtime.PrepareControlIdentityAsync(CancellationToken.None);
        var coordinator = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", dataPort + 4, dataPort, 24800, fingerprint);
        var engine = System.IO.Path.Combine(folder.Path, "synthetic-engine");
        var now = DateTimeOffset.UtcNow;
        await using (var pairingOwner = await MacMultiPeerClient.OpenAsync(root, engine, identities))
        {
            var identity = await pairingOwner.CreateIdentityAsync(now);
            var review = coordinator.Review(identity.ToJson(now), now);
            var invitation = await new MultiPeerTrustChangeCoordinator(vault, runtime).AddAsync(coordinator, review.Id, identity.KeyboardFingerprint, now);
            var hostReview = await pairingOwner.ReviewInvitationAsync(invitation.ToJson());
            await pairingOwner.ConfirmInvitationAsync(hostReview.Id, fingerprint);
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var socket = new TcpClient();
        var connecting = socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var serverSocket = await listener.AcceptTcpClientAsync(); await connecting;
        var process = new RecoveryProcess { BlockStop = scenario == 3 };
        var processStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        var dependencies = new DeskflowSessionDependencies { HasConflictingCore = _ => false,
            StartProcessAsync = async (_, _) => { Interlocked.Increment(ref launches); processStarting.TrySetResult(); await releaseStart.Task; return process; },
            ConnectCoreIpcAsync = (_, _) => Task.FromResult<Stream>(socket.GetStream()) };
        await using var owner = await MacMultiPeerClient.OpenAsync(root, engine, identities, dependencies);
        if (scenario == 6)
        {
            releaseStart.TrySetResult();
            process.FailStopOnce = true;
            owner.TestAfterCandidateDataCreated = _ => throw new IOException("synthetic failure before guard binding");
            await Assert.ThrowsAsync<AggregateException>(() => owner.StartAsync());
            await owner.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.HasExited);
            Assert.False(owner.PreviewControlUnconfirmed);
            Assert.Null(await owner.GetCollaborationLeaseAsync());
            return;
        }
        if (scenario is 4 or 5)
        {
            releaseStart.TrySetResult();
            process.FailStopOnce = scenario == 5;
            Task? cleanupStop = null;
            var callbackCalled = false;
            owner.TestAfterCandidateDataCreated = data =>
            {
                var field = data.LivePreview.GetType().GetField("lifetime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(field);
                var lifetime = Assert.IsType<CancellationTokenSource>(field.GetValue(data.LivePreview));
                lifetime.Token.Register(() => { callbackCalled = true; throw new IOException("synthetic cancellation callback failure"); });
                cleanupStop = owner.StopAsync();
            };
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => owner.StartAsync());
                Assert.NotNull(cleanupStop);
                await cleanupStop;
                Assert.True(callbackCalled);
                Assert.True(process.HasExited); // Dispose failure must not orphan the keyboard process.
            }
            finally { process.Exit(); }
            return;
        }
        if (scenario == 2)
        {
            releaseStart.TrySetResult();
            await owner.StartAsync();
            process.FailStopOnce = true;
            await Assert.ThrowsAnyAsync<Exception>(() => owner.StopAsync());
            await Assert.ThrowsAnyAsync<Exception>(() => owner.StartAsync());
            Assert.Equal(1, Volatile.Read(ref launches));
            return;
        }
        if (scenario == 3)
        {
            releaseStart.TrySetResult();
            await owner.StartAsync();
            var lease = await owner.GetCollaborationLeaseAsync();
            Assert.NotNull(lease);
            Assert.True(lease.IsActive);
            var blockedStop = owner.StopAsync();
            Task? concurrentStop = null;
            try
            {
                Assert.False(lease.IsActive);
                await process.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
                // The first stop has retired the guarded keyboard but still owns
                // it while the process exits. Another stop must remain usable.
                concurrentStop = owner.StopAsync();
                Assert.False(concurrentStop.IsCompleted);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                try { Assert.Null(await owner.GetCollaborationLeaseAsync(deadline.Token)); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                Assert.False(lease.IsActive);
            }
            finally { process.ReleaseStop.TrySetResult(); await blockedStop; if (concurrentStop is not null) await concurrentStop; }
            return;
        }
        var starting = owner.StartAsync();
        await processStarting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var stopCancellation = new CancellationTokenSource();
        var stopping = owner.StopAsync(stopCancellation.Token);
        try
        {
            if (scenario == 1)
            {
                stopCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
            }
            releaseStart.TrySetResult();
            await Assert.ThrowsAnyAsync<Exception>(() => starting);
            if (scenario == 0) await stopping;
            Assert.True(process.HasExited);
        }
        finally
        {
            releaseStart.TrySetResult();
            process.ReleaseStop.TrySetResult();
            process.Exit();
            try { await starting; } catch (Exception) { }
            try { await stopping; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ExplicitSuccessfulStopAllowsNewSessionWithFreshLease()
    {
        using var folder = new RecoveryDirectory();
        var root = System.IO.Path.Combine(folder.Path, "mac");
        var identities = new RecoveryIdentityVault();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "host-vault"), new SyntheticProtector());
        var dataPort = RecoveryPortBlock();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "host-runtime"), "host",
            IPAddress.Loopback, dataPort + 4, dataPort, peers => new MultiKeyboard(peers));
        var fingerprint = await runtime.PrepareControlIdentityAsync(CancellationToken.None);
        var coordinator = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", dataPort + 4, dataPort, 24800, fingerprint);
        var engine = System.IO.Path.Combine(folder.Path, "synthetic-engine");
        var now = DateTimeOffset.UtcNow;
        await using (var pairingOwner = await MacMultiPeerClient.OpenAsync(root, engine, identities))
        {
            var identity = await pairingOwner.CreateIdentityAsync(now);
            var review = coordinator.Review(identity.ToJson(now), now);
            var invitation = await new MultiPeerTrustChangeCoordinator(vault, runtime).AddAsync(coordinator, review.Id, identity.KeyboardFingerprint, now);
            var hostReview = await pairingOwner.ReviewInvitationAsync(invitation.ToJson());
            await pairingOwner.ConfirmInvitationAsync(hostReview.Id, fingerprint);
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var first = new TcpClient();
        var firstConnect = first.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var firstServer = await listener.AcceptTcpClientAsync();
        await firstConnect;
        using var second = new TcpClient();
        var secondConnect = second.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var secondServer = await listener.AcceptTcpClientAsync();
        await secondConnect;
        var processes = new Queue<RecoveryProcess>([new(), new()]);
        var streams = new Queue<Stream>([first.GetStream(), second.GetStream()]);
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(processes.Dequeue()),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(streams.Dequeue())
        };
        await using var owner = await MacMultiPeerClient.OpenAsync(root, engine, identities, dependencies);
        await owner.StartAsync();
        var firstLease = await owner.GetCollaborationLeaseAsync();
        Assert.NotNull(firstLease);
        Assert.True(firstLease.IsActive);
        await owner.StopAsync();
        Assert.False(firstLease.IsActive);
        Assert.Null(await owner.GetCollaborationLeaseAsync());

        await owner.StartAsync();
        var secondLease = await owner.GetCollaborationLeaseAsync();
        Assert.NotNull(secondLease);
        Assert.True(secondLease.IsActive);
        Assert.False(firstLease.IsActive);
        Assert.NotSame(firstLease, secondLease);
        await owner.StopAsync();
        Assert.False(secondLease.IsActive);
    }

    private sealed class RecoveryIdentityVault : IIdentityVault
    {
        private readonly Dictionary<string, byte[]> entries = new();
        public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(entries.TryGetValue(key, out var bytes) ? bytes.ToArray() : null);
        public Task StoreAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        { entries.Add(key, value.ToArray()); return Task.CompletedTask; }
    }

    private sealed class RecoveryProcess : IDeskflowOwnedProcess
    {
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockStop { get; init; }
        public bool FailStopOnce { get; set; }
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => 456790;
        public bool HasExited => completion.Task.IsCompleted;
        public Task<int> Completion => completion.Task;
        public void Exit() => completion.TrySetResult(0);
        public async Task StopAsync(CancellationToken ct = default)
        { Stopping.TrySetResult(); if (BlockStop) await ReleaseStop.Task; if (FailStopOnce) { FailStopOnce = false; throw new IOException("synthetic stop failure"); } Exit(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static int RecoveryPortBlock()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var first = Random.Shared.Next(40000, 59000);
            var listeners = new List<TcpListener>();
            try
            {
                for (var i = 0; i < 5; i++) { var listener = new TcpListener(IPAddress.Loopback, first + i); listeners.Add(listener); listener.Start(); }
                return first;
            }
            catch (SocketException) { }
            finally { foreach (var listener in listeners) listener.Stop(); }
        }
        throw new IOException("No free synthetic port block was found.");
    }

    private sealed class RecoveryDirectory : IDisposable
    {
        private readonly SyntheticWorkDirectory directory = SyntheticWorkDirectory.Create("mac-collaboration-recovery-");
        public string Path => directory.Path;
        public void Dispose() => directory.Dispose();
    }
}
