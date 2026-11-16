using System.Net;
using System.Net.Sockets;
using System.Text;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Theory]
    [InlineData("new-session")]
    [InlineData("switch")]
    [InlineData("restart")]
    [InlineData("emergency")]
    public async Task MacOwnerMustBlockNewControlAfterTimedOutBegin(string scenario)
    {
        using var folder = new RecoveryDirectory();
        await using var server = new ReorderingTlsControlServer { HoldBeginResponse = true };
        server.Start();
        using var ipc = await SyntheticIpcPair.CreateAsync();
        using var nextIpc = await SyntheticIpcPair.CreateAsync();
        var processes = new Queue<RecoveryProcess>([new(), new()]);
        var streams = new Queue<Stream>([ipc.Client.GetStream(), nextIpc.Client.GetStream()]);
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(processes.Dequeue()),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(streams.Dequeue())
        };
        await using var owner = await MacMultiPeerClient.OpenAsync(Path.Combine(folder.Path, "mac"),
            Path.Combine(folder.Path, "synthetic-engine"), new RecoveryIdentityVault(), dependencies);
        var invitation = new MultiPeerInvitation
        {
            Host = "127.0.0.1", Port = server.Port, HostName = "host", PeerName = owner.DeviceName,
            CertificateSha256 = server.Fingerprint, Token = server.Token
        };
        var review = await owner.ReviewInvitationAsync(invitation.ToJson());
        await owner.ConfirmInvitationAsync(review.Id, server.Fingerprint);
        await owner.StartAsync();
        await ConfirmSyntheticKeyboardAsync(owner, ipc.Server.GetStream());
        var backend = await owner.GetPreviewControlAsync();
        var capture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uncertain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new DeskflowPreviewSession(backend, (enabled, _) =>
        {
            if (enabled) capture.TrySetResult();
            return Task.CompletedTask;
        }, new NoTimerTimeProvider());
        first.Changed += () => { if (first.Status.Contains("尚未确认", StringComparison.Ordinal)) uncertain.TrySetResult(); };
        try
        {
            var enabling = first.EnableAsync();
            await capture.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first.ObserveFrame(new PreviewFrame
            { State = "live", Width = 1, Height = 1, JpegBytes = [1], CapturedAtUtc = DateTimeOffset.UtcNow });
            await server.BeginReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enabling.WaitAsync(TimeSpan.FromSeconds(10)));
            await uncertain.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.CanStart);
            await first.DisposeAsync();

            if (scenario == "restart")
            {
                var oldBackend = backend;
                await owner.StopAsync();
                await owner.StartAsync();
                await ConfirmSyntheticKeyboardAsync(owner, nextIpc.Server.GetStream());
                backend = await owner.GetPreviewControlAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => oldBackend.BeginPreviewAsync());
            }

            server.ImmediateBegin = true;
            if (scenario == "emergency")
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SwitchDetailedAsync(false));
                Assert.Equal(1, server.SwitchRequests); // Attempted, but never treated as an old-request barrier.
                Assert.True(owner.PreviewControlUnconfirmed);
            }
            if (scenario == "switch")
            {
                try { await owner.SwitchDetailedAsync(true); }
                catch (InvalidOperationException) { /* Expected once the owner is guarded. */ }
            }
            else
            {
                var secondCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var second = new DeskflowPreviewSession(backend, (enabled, _) =>
                {
                    if (enabled) secondCapture.TrySetResult();
                    return Task.CompletedTask;
                }, new NoTimerTimeProvider());
                var secondStart = second.EnableAsync();
                var firstStep = await Task.WhenAny(secondCapture.Task, secondStart, Task.Delay(TimeSpan.FromSeconds(5)));
                if (ReferenceEquals(firstStep, secondCapture.Task))
                    second.ObserveFrame(new PreviewFrame
                    { State = "live", Width = 1, Height = 1, JpegBytes = [1], CapturedAtUtc = DateTimeOffset.UtcNow });
                else if (!ReferenceEquals(firstStep, secondStart)) throw new TimeoutException("Second preview did not start or reject.");
                try { await secondStart.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (InvalidOperationException) { /* Expected once the owner is guarded. */ }
                catch (NotSupportedException) { /* The owner may disable new handoff before capture. */ }
            }

            server.ReleaseBegin.TrySetResult();
            await server.DeferredBeginApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(server.Active); // Previously sent Begin executed after the owner allowed the next action.
            if (scenario == "switch") Assert.Equal(0, server.SwitchRequests);
            else Assert.Equal(1, server.BeginRequests);
        }
        finally
        {
            server.ReleaseBegin.TrySetResult();
            await first.DisposeAsync();
        }
    }

    private static async Task ConfirmSyntheticKeyboardAsync(MacMultiPeerClient owner, Stream server)
    {
        await JsonLineProtocol.ReadAsync(server).WaitAsync(TimeSpan.FromSeconds(5));
        var fingerprint = new string('D', 64);
        await server.WriteAsync(Encoding.UTF8.GetBytes("peerFingerprint=" + fingerprint + "\nconnectionState=Connected\n"));
        await server.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        DeskLinkState state;
        do { state = await owner.GetStateAsync(deadline.Token); if (state.PendingPeerFingerprint is null) await Task.Delay(10, deadline.Token); }
        while (state.PendingPeerFingerprint is null && state.ConnectionState != DeskLinkConnectionState.Connected);
        if (state.PendingPeerFingerprint is not null) await owner.ConfirmKeyboardAsync(fingerprint, deadline.Token);
        state = await owner.GetStateAsync(deadline.Token);
        Assert.Equal(DeskLinkConnectionState.Connected, state.ConnectionState);
        Assert.Null(state.PendingPeerFingerprint);
    }

    private sealed class SyntheticIpcPair : IDisposable
    {
        private readonly TcpListener listener;
        public TcpClient Client { get; }
        public TcpClient Server { get; }
        private SyntheticIpcPair(TcpListener listener, TcpClient client, TcpClient server)
        { this.listener = listener; Client = client; Server = server; }
        public static async Task<SyntheticIpcPair> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await listener.AcceptTcpClientAsync();
            await connect;
            return new(listener, client, server);
        }
        public void Dispose() { Client.Dispose(); Server.Dispose(); listener.Stop(); }
    }
}
