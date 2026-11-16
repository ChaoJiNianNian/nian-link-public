using System.Net;
using System.Text;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class WindowsPeerLinkTests
{
    [Theory]
    [InlineData("disconnect")]
    [InlineData("pending")]
    public async Task RenewRequiresConfirmedKeyboardButReadAndReturnStillWork(string reason)
    {
        using var folder = new TestFolder();
        using var credentials = new MultiDeviceCredentials();
        var token = credentials.AddConfirmed("Win-Two", new string('1', 64));
        var keyboard = new PreviewKeyboard();
        await using var preview = new MultiPeerPreviewManager("Win-PC", ["Win-Two"], keyboard);
        await using var control = new MultiPeerControlServer(Path.Combine(folder.Path, "host"), IPAddress.Loopback, 0,
            "Win-PC", credentials, keyboard.GetStateAsync,
            (target, expected, revision, ct) => keyboard.SwitchIfCurrentAsync(target, expected, revision, ct), preview);
        await control.StartAsync();
        var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = control.Port,
            HostName = "Win-PC", PeerName = "Win-Two", CertificateSha256 = control.CertificateSha256!, Token = token };
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var dependencies = new DeskflowSessionDependencies {
            HasConflictingCore = _ => false, StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client)
        };
        await using var peer = new WindowsPeerLink(Path.Combine(folder.Path, "peer"), Path.Combine(folder.Path, "core.exe"), invitation, true, dependencies);
        await peer.StartAsync();
        await JsonLineProtocol.ReadAsync(server);
        var fingerprint = new string('D', 64);
        await server.WriteAsync(Encoding.UTF8.GetBytes($"peerFingerprint={fingerprint}\nconnectionState=Connected\n"));
        await server.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while ((await peer.GetStateAsync()).PendingPeerFingerprint is null) await Task.Delay(5, deadline.Token);
        await peer.ConfirmKeyboardPeerAsync(fingerprint);
        var started = await peer.BeginPreviewAsync();
        Assert.True(started.IsActive);
        Assert.True((await peer.RenewPreviewAsync(started.Id!)).IsActive);
        await server.WriteAsync(Encoding.UTF8.GetBytes(reason == "disconnect" ? "bye\n" : $"peerFingerprint={new string('E', 64)}\n"));
        await server.FlushAsync();
        while (true)
        {
            var state = await peer.GetStateAsync();
            if (reason == "disconnect" ? state.ConnectionState == DeskLinkConnectionState.Disconnected : state.PendingPeerFingerprint is not null) break;
            await Task.Delay(5, deadline.Token);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.RenewPreviewAsync(started.Id!));
        Assert.True((await peer.ReadPreviewAsync()).IsActive);
        Assert.False((await peer.EndPreviewAsync(started.Id!)).HasSession);
        Assert.Equal("Win-Two", keyboard.Active);
        process.Exit();
        await server.DisposeAsync();
    }

    private sealed class PreviewKeyboard : IMultiPeerKeyboard
    {
        private long revision = 1;
        public string Active { get; private set; } = "Win-Two";
        public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
        public Task<DeskLinkState> GetStateAsync(CancellationToken ct) => Task.FromResult(new DeskLinkState
        { ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true, ActiveComputer = Active,
            ConnectedComputers = ["Win-Two"], Revision = revision });
        public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long expectedRevision, CancellationToken ct)
        {
            if (Active != expected || revision != expectedRevision) return Task.FromResult(new DeskLinkSwitchResult { Error = "stale" });
            Active = target; revision++;
            return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target });
        }
        public Task<DeskLinkState> StopAsync(CancellationToken ct) => GetStateAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task WindowsPeerConnectsWithIndependentKeyboardTrustBeforeRequestingControl()
    {
        using var folder = new TestFolder();
        using var credentials = new MultiDeviceCredentials();
        var token = credentials.AddConfirmed("Win-Two", new string('1', 64));
        var switched = new List<string>();
        await using var control = new MultiPeerControlServer(System.IO.Path.Combine(folder.Path, "host"),
            IPAddress.Loopback, 0, "Win-PC", credentials, _ => MultiPeerControlTests.ReadyState("Win-PC", "Win-Two"),
            (target, expected, revision, _) => { switched.Add(target); return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target }); });
        await control.StartAsync();
        var invite = new MultiPeerInvitation { Host = "127.0.0.1", Port = control.Port,
            HostName = "Win-PC", PeerName = "Win-Two", CertificateSha256 = control.CertificateSha256!, Token = token };
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client)
        };
        await using var peer = new WindowsPeerLink(
            System.IO.Path.Combine(folder.Path, "peer"), System.IO.Path.Combine(folder.Path, "deskflow-core.exe"), invite, true, dependencies);
        await peer.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.SwitchAsync("Win-Two"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.BeginPreviewAsync());
        await JsonLineProtocol.ReadAsync(server);
        await server.WriteAsync(Encoding.UTF8.GetBytes("versionMismatch=1.27.0+synthetic\n"));
        await JsonLineProtocol.ReadAsync(server);
        var keyboardFingerprint = new string('D', 64);
        await server.WriteAsync(Encoding.UTF8.GetBytes("hello=1.27.0+synthetic\npeerFingerprint=" + keyboardFingerprint + "\nconnectionState=Connected\n"));
        await server.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await peer.GetStateAsync()).PendingPeerFingerprint is null) await Task.Delay(5, timeout.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.SwitchAsync("Win-Two"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.BeginPreviewAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.ConfirmKeyboardPeerAsync(new string('F', 64)));
        await peer.ConfirmKeyboardPeerAsync(keyboardFingerprint);
        Assert.True(await peer.SwitchAsync("Win-Two"));
        Assert.True(await peer.SwitchAsync("Win-PC"));
        Assert.Equal(new[] { "Win-Two", "Win-PC" }, switched);
        process.Exit();
        await server.DisposeAsync();
    }

    private sealed class FakeProcess : IDeskflowOwnedProcess
    {
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => 5678;
        public bool HasExited => completion.Task.IsCompleted;
        public Task<int> Completion => completion.Task;
        public void Exit() => completion.TrySetResult(0);
        public Task StopAsync(CancellationToken ct = default) { Exit(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
