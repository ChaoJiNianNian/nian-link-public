using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppingCancelsBackgroundPollingBeforeBlockedOrFailingPreviewDisposal(bool fail)
    {
        using var folder = new RecoveryDirectory();
        var enteredPoll = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ControlledStopSource(fail);
        var service = new MacCollaborationService(folder.Path, async token =>
        {
            enteredPoll.TrySetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return (DeskLinkPairingBundle?)null;
        }, new FakeMedia());
        var session = service.RegisterDeskflowSessionForFixture(new DeskflowPreviewSession(new HandoffBackend(), source));
        await service.StartAsync();
        var pollToken = await enteredPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = service.DisposeAsync().AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(stopping.IsCompleted);
            var observedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = pollToken.Register(() => observedCancellation.TrySetResult());
            await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(pollToken.IsCancellationRequested);
        }
        finally { source.Release.TrySetResult(); }
        if (fail) await Assert.ThrowsAsync<IOException>(() => stopping);
        else await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        GC.KeepAlive(session);
    }

    [Fact]
    public async Task PairingReplacementCannotPublishAClientAfterDispose()
    {
        using var folder = new RecoveryDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        DeskLinkPairingBundle bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), new FakeMedia(), server.Port);
        await service.RefreshOnceAsync();
        bundle = bundle with { };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.TestBeforeClientPublish = async () => { entered.TrySetResult(); await release.Task; };
        var listing = service.ListSharedAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => listing);
    }

    [Fact]
    public async Task StoppingRejectsNewSharedListingWithoutReadingPairing()
    {
        using var folder = new RecoveryDirectory();
        var pairingCalls = 0;
        var source = new ControlledStopSource(false);
        var service = new MacCollaborationService(folder.Path, _ =>
        {
            Interlocked.Increment(ref pairingCalls);
            return Task.FromResult<DeskLinkPairingBundle?>(null);
        }, new FakeMedia());
        var session = service.RegisterDeskflowSessionForFixture(new DeskflowPreviewSession(new HandoffBackend(), source));
        var stopping = service.DisposeAsync().AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ListSharedAsync());
            Assert.Equal(0, Volatile.Read(ref pairingCalls));
        }
        finally { source.Release.TrySetResult(); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        GC.KeepAlive(session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCallbackCannotPreventAttemptingPreviewCleanup(bool fail)
    {
        using var folder = new RecoveryDirectory();
        using var releaseCallback = new ManualResetEventSlim();
        var pollEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ControlledStopSource(false);
        var service = new MacCollaborationService(folder.Path, async token =>
        {
            using var registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Wait();
                if (fail) throw new IOException("synthetic cancellation failure");
            });
            pollEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return (DeskLinkPairingBundle?)null;
        }, new FakeMedia());
        var session = service.RegisterDeskflowSessionForFixture(new DeskflowPreviewSession(new HandoffBackend(), source));
        await service.StartAsync();
        await pollEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var invocation = Task.Run(() => service.DisposeAsync().AsTask());
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { releaseCallback.Set(); source.Release.TrySetResult(); }
        if (fail) await Assert.ThrowsAnyAsync<Exception>(() => invocation);
        else await invocation.WaitAsync(TimeSpan.FromSeconds(5));
        GC.KeepAlive(session);
    }

    private sealed class ControlledStopSource(bool fail) : IControlLivePreviewSource
    {
        public bool IsSupported => true;
        public string UnavailableHint => "synthetic";
        public event Action<Guid, LivePreviewCapturePacket>? PacketReceived { add { } remove { } }
        public event Action<Guid, string>? StreamClosed { add { } remove { } }
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(Guid operationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(Guid operationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            Entered.TrySetResult();
            await Release.Task;
            if (fail) throw new IOException("synthetic source cleanup failure");
        }
    }
}
