using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Services;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task MultiDeviceNetworkFailureRevokesShareAndCannotBeBypassedByRefresh(int scenario)
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
        using var socket = new TcpClient();
        var connecting = socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var serverSocket = await listener.AcceptTcpClientAsync();
        await connecting;
        var process = new RecoveryProcess();
        var dependencies = new DeskflowSessionDependencies { HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult<Stream>(socket.GetStream()) };
        await using var owner = await MacMultiPeerClient.OpenAsync(root, engine, identities, dependencies);
        await owner.StartAsync();
        var media = new FakeMedia();
        await using var service = new MacCollaborationService(System.IO.Path.Combine(folder.Path, "collaboration"),
            owner.GetApprovedCollaborationPairingAsync, media, 1, null, null, null, null, usePairedDataPort: true);
        await service.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!service.Snapshot.IsOnline) await Task.Delay(20, timeout.Token);
        await service.SetShareScreenEnabledAsync(true);
        Assert.True(service.Snapshot.Settings.ShareScreenEnabled);
        await runtime.StopAsync(CancellationToken.None);
        while (service.Snapshot.Error is null) await Task.Delay(20, timeout.Token);

        if (scenario == 0) Assert.False(service.Snapshot.Settings.ShareScreenEnabled);
        Assert.False(service.Snapshot.IsOnline);
        var captures = media.Captures;
        await runtime.StartAsync(vault.GetTrustedDevices(), CancellationToken.None);
        await service.RefreshOnceAsync(timeout.Token);
        Assert.False(service.Snapshot.IsOnline);
        Assert.Equal(captures, media.Captures);
        process.Exit();
    }

    private sealed class SyntheticProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }
    private sealed class MultiKeyboard(IReadOnlyList<TrustedDeviceIdentity> peers) : IMultiPeerKeyboard
    {
        public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
        public Task<DeskLinkState> GetStateAsync(CancellationToken ct) => Task.FromResult(new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
            NativeControlAvailable = true, ActiveComputer = "host", ConnectedComputers = peers.Select(x => x.Name).ToArray() });
        public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long revision, CancellationToken ct) =>
            Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target });
        public Task<DeskLinkState> StopAsync(CancellationToken ct) => GetStateAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeMedia : IMacMediaBridge
    {
        public int Captures { get; private set; }
        public Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default) => Task.FromResult(JsonDocument.Parse("{\"status\":\"ready\"}"));
        public Task<MacScreenCaptureReply> CaptureAsync(int width = 960, int height = 540, uint? windowId = null, CancellationToken cancellationToken = default)
        { Captures++; return Task.FromResult(new MacScreenCaptureReply { Status = "unavailable" }); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        private static readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nian-link", "synthetic-mac-collaboration-recovery");
        public string Path { get; } = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));
        public RecoveryDirectory() { CheckPath(Root); Directory.CreateDirectory(Path); }
        public void Dispose()
        {
            CheckPath(Path);
            if (!Directory.Exists(Path)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories)) CheckPath(entry);
            Directory.Delete(Path, true);
        }
        private static void CheckPath(string path)
        {
            for (var current = System.IO.Path.GetFullPath(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Synthetic test path contains a link.");
        }
    }
}
