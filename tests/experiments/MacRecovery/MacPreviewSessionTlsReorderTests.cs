using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Theory]
    [InlineData(false)] // First cleanup read responds Viewing.
    [InlineData(true)]  // First read fails; periodic recovery read responds Viewing.
    public async Task RealSessionMustNotTreatViewingAsReturnProofAfterTimedOutTlsBegin(bool failFirstRead)
    {
        await using var server = new ReorderingTlsControlServer
        { HoldBeginResponse = true, FailFirstRead = failFirstRead };
        server.Start();
        var invitation = new MultiPeerInvitation
        {
            Host = "127.0.0.1", Port = server.Port, HostName = "host", PeerName = "synthetic-peer",
            CertificateSha256 = server.Fingerprint, Token = server.Token
        };
        await using var client = new MultiPeerControlClient(invitation, true);
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new DeskflowPreviewSession(new TlsPreviewBackend(client), (enabled, _) =>
        {
            if (enabled) captureStarted.TrySetResult();
            return Task.CompletedTask;
        }, new NoTimerTimeProvider());
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += () =>
        {
            if (session.Status.Contains("尚未确认", StringComparison.Ordinal))
                settled.TrySetResult();
        };
        try
        {
            var beginning = session.EnableAsync();
            await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.ObserveFrame(new PreviewFrame
            { State = "live", Width = 1, Height = 1, JpegBytes = [1], CapturedAtUtc = DateTimeOffset.UtcNow });
            await server.BeginReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => beginning.WaitAsync(TimeSpan.FromSeconds(10)));
            // The actual session's 8-second Begin deadline cancels its TLS wait;
            // the server deliberately still holds that already-arrived operation.
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (failFirstRead) await Assert.ThrowsAnyAsync<IOException>(() => client.ReadPreviewAsync());
            Assert.Equal(DeskflowPreviewPhase.Viewing, (await client.ReadPreviewAsync()).Phase);
            var check = typeof(DeskflowPreviewSession).GetMethod("CheckSafetyAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await ((Task)check.Invoke(session, null)!).WaitAsync(TimeSpan.FromSeconds(5));
            await session.StopAsync("retry").WaitAsync(TimeSpan.FromSeconds(5));
            var incorrectlyAllowsStart = session.CanStart;
            server.ReleaseBegin.TrySetResult();
            await server.BeginApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(server.Active); // The prior request did in fact execute late.
            Assert.False(incorrectlyAllowsStart); // RED until the actual shared session is safe.
        }
        finally { server.ReleaseBegin.TrySetResult(); server.ReleaseRead.TrySetResult(); }
    }

    [Fact]
    public async Task RealSessionMustNotTreatViewingAsReturnProofAfterTimedOutTlsEnd()
    {
        await using var server = new ReorderingTlsControlServer
        { ImmediateBegin = true, HoldEndResponse = true, ReadAsViewing = true };
        server.Start();
        var invitation = new MultiPeerInvitation
        {
            Host = "127.0.0.1", Port = server.Port, HostName = "host", PeerName = "synthetic-peer",
            CertificateSha256 = server.Fingerprint, Token = server.Token
        };
        await using var client = new MultiPeerControlClient(invitation, true);
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new DeskflowPreviewSession(new TlsPreviewBackend(client), (enabled, _) =>
        {
            if (enabled) captureStarted.TrySetResult();
            return Task.CompletedTask;
        }, new NoTimerTimeProvider());
        try
        {
            var beginning = session.EnableAsync();
            await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.ObserveFrame(new PreviewFrame
            { State = "live", Width = 1, Height = 1, JpegBytes = [1], CapturedAtUtc = DateTimeOffset.UtcNow });
            await beginning.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.IsActive);
            var stopping = session.StopAsync("stop");
            await server.EndReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stopping.WaitAsync(TimeSpan.FromSeconds(5)); // End's response times out while server still holds the request.
            Assert.False(session.CanStart);
            Assert.Equal(DeskflowPreviewPhase.Viewing, (await client.ReadPreviewAsync()).Phase);
            var check = typeof(DeskflowPreviewSession).GetMethod("CheckSafetyAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await ((Task)check.Invoke(session, null)!).WaitAsync(TimeSpan.FromSeconds(5));
            await session.StopAsync("retry").WaitAsync(TimeSpan.FromSeconds(5));
            var incorrectlyAllowsStart = session.CanStart;
            server.ReleaseEnd.TrySetResult();
            await server.EndApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(incorrectlyAllowsStart); // An already sent End may still execute after the read.
        }
        finally { server.ReleaseEnd.TrySetResult(); server.ReleaseRead.TrySetResult(); }
    }

    private sealed class NoTimerTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
