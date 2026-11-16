using System.Text.Json;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacCollaborationDisposalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAndLaterDisposalsShareTheFirstCleanupResult(bool fail)
    {
        var media = new ControlledMedia(fail);
        var service = NewService(media);
        var first = service.DisposeAsync().AsTask();
        await media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.DisposeAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, media.Calls);
        }
        finally { media.Release.TrySetResult(); }

        if (fail)
        {
            Assert.Equal("synthetic cleanup failure", (await Assert.ThrowsAsync<IOException>(() => first)).Message);
            Assert.Equal("synthetic cleanup failure", (await Assert.ThrowsAsync<IOException>(() => second)).Message);
            Assert.Equal("synthetic cleanup failure", (await Assert.ThrowsAsync<IOException>(() => service.DisposeAsync().AsTask())).Message);
        }
        else
        {
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            await service.DisposeAsync();
        }
        Assert.Equal(1, media.Calls);
    }

    [Fact]
    public async Task FailedCleanupRemainsFailedOnLaterStopAttempt()
    {
        var media = new ControlledMedia(true);
        var service = NewService(media);
        var first = service.DisposeAsync().AsTask();
        await media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        media.Release.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAsync<IOException>(() => service.DisposeAsync().AsTask());
        Assert.Equal(1, media.Calls);
    }

    [Fact]
    public async Task CleanupCallbackCanEnterDisposeWithoutBeingBlockedByTheDisposalLock()
    {
        var media = new ControlledMedia(false);
        var service = NewService(media);
        media.OnEnter = () =>
        {
            var captured = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            var other = Task.Run(() => captured.TrySetResult(service.DisposeAsync().AsTask()));
            Assert.True(other.Wait(TimeSpan.FromSeconds(3)), "Dispose held its lock across a cleanup callback");
            Assert.False(captured.Task.Result.IsCompleted);
        };
        var first = service.DisposeAsync().AsTask();
        try { await media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { media.Release.TrySetResult(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static MacCollaborationService NewService(IMacMediaBridge media) =>
        new(Path.Combine(Path.GetTempPath(), "nian-synthetic-dispose-" + Guid.NewGuid().ToString("N")), _ => Task.FromResult<DesktopToolbox.DeskLink.DeskLinkPairingBundle?>(null), media);

    private sealed class ControlledMedia(bool fail) : IMacMediaBridge
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnEnter;
        public int Calls;
        public Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MacScreenCaptureReply> CaptureAsync(int width = 960, int height = 540, uint? windowId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Calls);
            OnEnter?.Invoke();
            Entered.TrySetResult();
            await Release.Task;
            if (fail) throw new IOException("synthetic cleanup failure");
        }
    }
}
