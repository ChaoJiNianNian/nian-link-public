using DesktopToolbox.DeskLink.Collaboration;

namespace MultiPeerTrust.Tests;

public sealed class ReverseShareQueueTests
{
    private static readonly string PeerA = new('A', 64), PeerB = new('B', 64);
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultsToDeniedAndScopesAuthorizationAndRepliesToOneGrant()
    {
        var queue = new ReverseShareQueue(PeerA, () => Start);
        Assert.Throws<UnauthorizedAccessException>(() => queue.Enqueue(PeerA, Guid.Empty, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(10)));
        var authorization = queue.Authorize(PeerA);
        Assert.Throws<UnauthorizedAccessException>(() => queue.Enqueue(PeerB, authorization, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(10)));
        var request = queue.Enqueue(PeerA, authorization, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(10));
        Assert.Null(queue.Deliver(PeerB, authorization));
        Assert.Equal(request, queue.Deliver(PeerA, authorization));
        Assert.False(queue.Complete(PeerB, authorization, request.Id, [1]));
        Assert.True(queue.Complete(PeerA, authorization, request.Id, [1]));
        Assert.False(queue.Complete(PeerA, authorization, request.Id, [2]));
        Assert.Equal(new byte[] { 1 }, queue.TakeResult(PeerA, authorization, request.Id));
        Assert.Null(queue.TakeResult(PeerA, authorization, request.Id));
    }

    [Fact]
    public void ExpiredCancelledAndRevokedRequestsNeverAcceptLateReplies()
    {
        var now = Start;
        var queue = new ReverseShareQueue(PeerA, () => now);
        var auth = queue.Authorize(PeerA);
        var expired = queue.Enqueue(PeerA, auth, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(1));
        Assert.NotNull(queue.Deliver(PeerA, auth));
        now = now.AddSeconds(1);
        Assert.False(queue.Complete(PeerA, auth, expired.Id, [1]));
        var cancelled = queue.Enqueue(PeerA, auth, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(5));
        Assert.NotNull(queue.Deliver(PeerA, auth));
        Assert.True(queue.Cancel(PeerA, auth, cancelled.Id));
        Assert.False(queue.Complete(PeerA, auth, cancelled.Id, [1]));
        var revoked = queue.Enqueue(PeerA, auth, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(5));
        Assert.NotNull(queue.Deliver(PeerA, auth));
        queue.Revoke(PeerA);
        var newAuth = queue.Authorize(PeerA);
        Assert.NotEqual(auth, newAuth);
        Assert.False(queue.Complete(PeerA, auth, revoked.Id, [1]));
        Assert.False(queue.Complete(PeerA, newAuth, revoked.Id, [1]));
        Assert.Null(queue.TakeResult(PeerA, newAuth, revoked.Id));
    }

    [Fact]
    public void IsBoundedRejectsInvalidRequestsAndDoesNotRedeliver()
    {
        var queue = new ReverseShareQueue(PeerA, () => Start);
        var auth = queue.Authorize(PeerA);
        Assert.Throws<ArgumentException>(() => queue.Enqueue(PeerA, auth, ReverseShareOperation.List, "root", "../secret", TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentException>(() => queue.Enqueue(PeerA, auth, ReverseShareOperation.Preview, null, "a.txt", TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentException>(() => queue.Enqueue(PeerA, auth, ReverseShareOperation.Download, null, "a.txt", TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentException>(() => queue.Enqueue(PeerA, auth, ReverseShareOperation.List, null, "", TimeSpan.FromMinutes(6)));
        var request = queue.Enqueue(PeerA, auth, ReverseShareOperation.Preview, "root", "a.txt", TimeSpan.FromSeconds(10));
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(PeerA, auth, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(10)));
        Assert.False(queue.Complete(PeerA, auth, request.Id, [1]));
        Assert.NotNull(queue.Deliver(PeerA, auth));
        Assert.Null(queue.Deliver(PeerA, auth));
        Assert.False(queue.Complete(PeerA, auth, request.Id, new byte[ReverseShareQueue.MaximumReplyBytes + 1]));
        Assert.True(queue.Complete(PeerA, auth, request.Id, [3]));
        Assert.Equal(new byte[] { 3 }, queue.TakeResult(PeerA, auth, request.Id));
        var next = queue.Enqueue(PeerA, auth, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(10));
        Assert.NotEqual(request.Id, next.Id);
    }
    [Fact]
    public void ReauthorizationDiscardsUnconsumedResultsAndCopiesReply()
    {
        var queue = new ReverseShareQueue(PeerA, () => Start);
        var auth = queue.Authorize(PeerA);
        var request = queue.Enqueue(PeerA, auth, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(10));
        Assert.NotNull(queue.Deliver(PeerA, auth));
        var bytes = new byte[] { 4 };
        Assert.True(queue.Complete(PeerA, auth, request.Id, bytes));
        bytes[0] = 9;
        Assert.Equal(new byte[] { 4 }, queue.TakeResult(PeerA, auth, request.Id));
        request = queue.Enqueue(PeerA, auth, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(10));
        Assert.NotNull(queue.Deliver(PeerA, auth));
        Assert.True(queue.Complete(PeerA, auth, request.Id, [5]));
        var nextAuth = queue.Authorize(PeerA);
        Assert.Null(queue.TakeResult(PeerA, auth, request.Id));
        Assert.Null(queue.TakeResult(PeerA, nextAuth, request.Id));
    }

    [Fact]
    public async Task ConcurrentRequestsCannotOccupyTheSameGrant()
    {
        var queue = new ReverseShareQueue(PeerA, () => Start);
        var auth = queue.Authorize(PeerA);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            try { return queue.Enqueue(PeerA, auth, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(10)); }
            catch (InvalidOperationException) { return null; }
        })));
        Assert.Single(attempts, x => x is not null);
    }
    [Fact]
    public void DisposingReleasesStateAndRejectsReuse()
    {
        var queue = new ReverseShareQueue(PeerA, () => Start);
        var auth = queue.Authorize(PeerA);
        var request = queue.Enqueue(PeerA, auth, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(10));
        Assert.NotNull(queue.Deliver(PeerA, auth));
        Assert.True(queue.Complete(PeerA, auth, request.Id, [1]));
        queue.Dispose();
        queue.Dispose();
        Assert.Throws<ObjectDisposedException>(() => queue.Authorize(PeerA));
        Assert.Throws<ObjectDisposedException>(() => queue.TakeResult(PeerA, auth, request.Id));
        Assert.Throws<ObjectDisposedException>(() => queue.Complete(PeerA, auth, request.Id, [2]));
    }

    [Fact]
    public async Task ConcurrentCompletionAndRevocationCannotExposeAResultAfterRevocation()
    {
        for (var i = 0; i < 30; i++)
        {
            using var queue = new ReverseShareQueue(PeerA, () => Start);
            var auth = queue.Authorize(PeerA);
            var request = queue.Enqueue(PeerA, auth, ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(10));
            Assert.NotNull(queue.Deliver(PeerA, auth));
            await Task.WhenAll(Task.Run(() => queue.Complete(PeerA, auth, request.Id, [1])), Task.Run(() => queue.Revoke(PeerA)));
            Assert.Null(queue.TakeResult(PeerA, auth, request.Id));
            Assert.False(queue.Complete(PeerA, auth, request.Id, [2]));
        }
    }
}



