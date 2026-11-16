using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class MultiPeerDisplayQueueTests
{
    [Fact]
    public async Task CommandIsDeliveredOnlyToItsBoundSessionAndCompletesOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new MultiPeerDisplayQueue(() => now);
        var request = new MultiPeerDisplayRequest(Guid.NewGuid(), Guid.NewGuid(), "screen", 15, 16, 1, now.AddSeconds(5));
        var completion = queue.Enqueue(request);
        Assert.Null(queue.Deliver(Guid.NewGuid()));
        Assert.Equal(request, queue.Deliver(request.Epoch));
        Assert.Null(queue.Deliver(request.Epoch));
        Assert.False(queue.CancelBeforeDelivery(request.Id));
        Assert.Throws<InvalidOperationException>(() => queue.Complete(Guid.NewGuid(), "verified"));
        queue.Complete(request.Id, "acceptedUnverified");
        Assert.Equal("acceptedUnverified", (await completion).Status);
        queue.Complete(request.Id, "acceptedUnverified");
        Assert.Throws<InvalidOperationException>(() => { _ = queue.Enqueue(request); });
        Assert.Throws<InvalidOperationException>(() => queue.Complete(request.Id, "verified"));
    }

    [Fact]
    public async Task ExpiredUndeliveredCommandCanFinishWithoutRiskOfLateExecution()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new MultiPeerDisplayQueue(() => now);
        var request = new MultiPeerDisplayRequest(Guid.NewGuid(), Guid.NewGuid(), "screen", 15, 16, 1, now.AddSeconds(2));
        var completion = queue.Enqueue(request);
        now = now.AddSeconds(3);
        Assert.Null(queue.Deliver(request.Epoch));
        Assert.Equal("notAttempted", (await completion).Status);
        Assert.False(queue.HasUnresolvedCommand);
    }

    [Fact]
    public async Task LostAcknowledgementKeepsDeliveredCommandPendingEvenAfterDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new MultiPeerDisplayQueue(() => now);
        var request = new MultiPeerDisplayRequest(Guid.NewGuid(), Guid.NewGuid(), "screen", 15, 16, 1, now.AddSeconds(2));
        var completion = queue.Enqueue(request);
        Assert.NotNull(queue.Deliver(request.Epoch));
        now = now.AddSeconds(3);
        Assert.Null(queue.Deliver(request.Epoch));
        Assert.True(queue.HasUnresolvedCommand);
        Assert.Throws<InvalidOperationException>(() => { _ = queue.Enqueue(request with { Id = Guid.NewGuid(), ExpiresUtc = now.AddSeconds(2) }); });
        queue.Complete(request.Id, "failed");
        Assert.Equal("failed", (await completion).Status);
        Assert.False(queue.HasUnresolvedCommand);
    }

    [Fact]
    public async Task UnreachablePeerExpiresWithoutPollingAndDoesNotExecuteLater()
    {
        var queue = new MultiPeerDisplayQueue();
        var request = new MultiPeerDisplayRequest(Guid.NewGuid(), Guid.NewGuid(), "screen", 15, 16, 1, DateTimeOffset.UtcNow.AddMilliseconds(80));
        var completion = queue.Enqueue(request);
        Assert.Equal("notAttempted", (await completion.WaitAsync(TimeSpan.FromSeconds(2))).Status);
        Assert.False(queue.HasUnresolvedCommand);
        Assert.Null(queue.Deliver(request.Epoch));
    }

    [Fact]
    public async Task DeliveredCommandRemainsPendingWhenRealDeadlinePasses()
    {
        var queue = new MultiPeerDisplayQueue();
        var request = new MultiPeerDisplayRequest(Guid.NewGuid(), Guid.NewGuid(), "screen", 15, 16, 1, DateTimeOffset.UtcNow.AddMilliseconds(100));
        var completion = queue.Enqueue(request);
        Assert.NotNull(queue.Deliver(request.Epoch));
        await Task.Delay(180);
        Assert.True(queue.HasUnresolvedCommand);
        Assert.False(completion.IsCompleted);
        queue.Complete(request.Id, "failed");
        Assert.Equal("failed", (await completion).Status);
    }
}
