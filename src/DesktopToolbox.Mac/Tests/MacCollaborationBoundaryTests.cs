using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Services;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Fact]
    public async Task OwnerStopCancelsAndDrainsRegisteredWorkBeforeCompleting()
    {
        await using var harness = await BoundaryHarness.OpenAsync();
        var lease = Assert.IsType<MacMultiPeerClient.CollaborationLease>(await harness.Owner.GetCollaborationLeaseAsync());
        var operation = Enter(lease);
        Assert.NotNull(operation);
        var token = OperationToken(operation);
        Assert.False(token.IsCancellationRequested);
        var changed = 0;
        Assert.True(lease.TryCommit(() => changed = 1));
        Task? stopping = null;
        try
        {
            stopping = harness.Owner.StopAsync();
            Assert.True(token.IsCancellationRequested);
            Assert.False(lease.IsActive);
            Assert.Null(Enter(lease));
            Assert.False(lease.TryCommit(() => changed = 2));
            Assert.Equal(1, changed);
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            ((IDisposable)operation).Dispose();
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(harness.Process.HasExited);
    }

    [Fact]
    public async Task FailureSuspensionReturnsDrainWithoutWaitingForItsOwnOperation()
    {
        await using var harness = await BoundaryHarness.OpenAsync();
        var lease = Assert.IsType<MacMultiPeerClient.CollaborationLease>(await harness.Owner.GetCollaborationLeaseAsync());
        var operation = Enter(lease);
        Assert.NotNull(operation);
        try
        {
            var suspend = lease.GetType().GetMethod("Suspend", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(suspend);
            var drain = Assert.IsAssignableFrom<Task>(suspend.Invoke(lease, null));
            Assert.True(OperationToken(operation).IsCancellationRequested);
            Assert.False(lease.IsActive);
            Assert.Null(Enter(lease));
            Assert.False(lease.TryCommit(() => throw new Exception("stale commit ran")));
            Assert.False(drain.IsCompleted);
            ((IDisposable)operation).Dispose();
            await drain.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { ((IDisposable)operation).Dispose(); }
        await harness.Owner.StopAsync();
    }

    [Fact]
    public async Task StopAndConcurrentRegistrationsCannotEscapeTheDrain()
    {
        await using var harness = await BoundaryHarness.OpenAsync();
        var lease = Assert.IsType<MacMultiPeerClient.CollaborationLease>(await harness.Owner.GetCollaborationLeaseAsync());
        var first = Enter(lease);
        Assert.NotNull(first);
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 20).Select(_ => Task.Run(() => { start.Wait(); return Enter(lease); })).ToArray();
        Task? stopping = null;
        object?[] others = [];
        try
        {
            start.Set();
            stopping = harness.Owner.StopAsync();
            others = await Task.WhenAll(attempts);
            Assert.False(lease.IsActive);
            Assert.True(OperationToken(first).IsCancellationRequested);
            foreach (var operation in others.OfType<IDisposable>())
                Assert.True(OperationToken(operation).IsCancellationRequested);
            Assert.Null(Enter(lease));
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            start.Set();
            foreach (var operation in others.OfType<IDisposable>()) operation.Dispose();
            ((IDisposable)first).Dispose();
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task StopCannotCompleteWhileConcurrentRevocationCancellationIsStillRunning()
    {
        await using var harness = await BoundaryHarness.OpenAsync();
        var lease = Assert.IsType<MacMultiPeerClient.CollaborationLease>(await harness.Owner.GetCollaborationLeaseAsync());
        var operation = Assert.IsAssignableFrom<IDisposable>(Enter(lease));
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        using var registration = OperationToken(operation).Register(() => { callbackEntered.TrySetResult(); callbackRelease.Wait(); });
        Task? suspension = null;
        Task? stopping = null;
        try
        {
            suspension = Task.Run(() => lease.Suspend());
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            operation.Dispose();
            var concurrentDrain = lease.Suspend();
            Assert.False(concurrentDrain.IsCompleted);
            stopping = Task.Run(() => harness.Owner.StopAsync());
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            callbackRelease.Set();
            operation.Dispose();
            if (suspension is not null) await suspension.WaitAsync(TimeSpan.FromSeconds(10));
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RevocationRunsCancellationCallbacksOutsideTheOwnerGate()
    {
        await using var harness = await BoundaryHarness.OpenAsync();
        var lease = Assert.IsType<MacMultiPeerClient.CollaborationLease>(await harness.Owner.GetCollaborationLeaseAsync());
        var operation = Enter(lease);
        Assert.NotNull(operation);
        var callbackCompleted = false;
        using var registration = OperationToken(operation).Register(() =>
        {
            var contender = Task.Run(() => lease.TryCommit(() => { }));
            callbackCompleted = contender.Wait(TimeSpan.FromSeconds(3)) && !contender.Result;
        });
        Task? stopping = null;
        try
        {
            stopping = harness.Owner.StopAsync();
            Assert.True(callbackCompleted);
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            ((IDisposable)operation).Dispose();
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static object? Enter(MacMultiPeerClient.CollaborationLease lease)
    {
        var method = lease.GetType().GetMethod("TryEnter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] arguments = [null];
        var entered = Assert.IsType<bool>(method.Invoke(lease, arguments));
        if (!entered) { Assert.Null(arguments[0]); return null; }
        return Assert.IsAssignableFrom<IDisposable>(arguments[0]);
    }

    private static CancellationToken OperationToken(object operation)
    {
        var property = operation.GetType().GetProperty("Token", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsType<CancellationToken>(property.GetValue(operation));
    }

    private sealed class BoundaryHarness : IAsyncDisposable
    {
        private readonly RecoveryDirectory folder;
        private readonly MultiDeviceCredentialVault vault;
        private readonly MultiPeerRuntime runtime;
        private readonly TcpListener listener;
        private readonly TcpClient socket, serverSocket;
        public MacMultiPeerClient Owner { get; }
        public RecoveryProcess Process { get; }
        private BoundaryHarness(RecoveryDirectory folder, MultiDeviceCredentialVault vault, MultiPeerRuntime runtime,
            TcpListener listener, TcpClient socket, TcpClient serverSocket, MacMultiPeerClient owner, RecoveryProcess process)
        { this.folder=folder;this.vault=vault;this.runtime=runtime;this.listener=listener;this.socket=socket;this.serverSocket=serverSocket;Owner=owner;Process=process; }

        public static async Task<BoundaryHarness> OpenAsync()
        {
            var folder = new RecoveryDirectory();
            var root = System.IO.Path.Combine(folder.Path, "mac");
            var identities = new RecoveryIdentityVault();
            var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "host-vault"), new SyntheticProtector());
            var port = RecoveryPortBlock();
            var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "host-runtime"), "host", IPAddress.Loopback, port + 4, port, peers => new MultiKeyboard(peers));
            var fingerprint = await runtime.PrepareControlIdentityAsync(CancellationToken.None);
            var coordinator = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", port + 4, port, 24800, fingerprint);
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
            var listener = new TcpListener(IPAddress.Loopback, 0);listener.Start();
            var socket = new TcpClient();
            var connecting = socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var serverSocket = await listener.AcceptTcpClientAsync();await connecting;
            var process = new RecoveryProcess();
            var dependencies = new DeskflowSessionDependencies { HasConflictingCore = _ => false,
                StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
                ConnectCoreIpcAsync = (_, _) => Task.FromResult<Stream>(socket.GetStream()) };
            var owner = await MacMultiPeerClient.OpenAsync(root, engine, identities, dependencies);
            await owner.StartAsync();
            return new(folder, vault, runtime, listener, socket, serverSocket, owner, process);
        }

        public async ValueTask DisposeAsync()
        {
            Process.Exit();
            await Owner.DisposeAsync();
            serverSocket.Dispose();socket.Dispose();listener.Stop();
            await runtime.DisposeAsync();await vault.DisposeAsync();folder.Dispose();
        }
    }
}
