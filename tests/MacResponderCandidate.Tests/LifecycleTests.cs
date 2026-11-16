using NianLink.MacResponderCandidate;
using System.Threading.Channels;

public sealed class LifecycleTests
{
    [Fact]
    public async Task InvalidDurationIsRejected()
    {
        var t = new FakeTransport(); var p = new FakePolicy(); var n = new FakeNetwork();
        Assert.Throws<ArgumentOutOfRangeException>(() => ResponderRun.StartForTest(n, p, t, TimeSpan.FromSeconds(31)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResponderRun.StartForTest(n, p, t, TimeSpan.Zero));
        await t.DisposeAsync();
    }

    [Fact]
    public async Task RevokedNetworkClosesAndDoesNotSend()
    {
        var t = new FakeTransport(); var p = new FakePolicy(); var n = new FakeNetwork { Available = false };
        await using var run = ResponderRun.StartForTest(n, p, t, TimeSpan.FromSeconds(2));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(t.Closed); Assert.Equal(0, t.Sends); Assert.True(p.Closed);
    }

    [Fact]
    public async Task StopDuringLateRevalidationNeverSends()
    {
        var t = new FakeTransport(); var p = new FakePolicy(); var n = new FakeNetwork();
        n.DelayOn = 2;
        await using var run = ResponderRun.StartForTest(n, p, t, TimeSpan.FromSeconds(5));
        t.Deliver();
        await n.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await run.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        n.Pending.TrySetResult(true);
        Assert.True(t.Closed); Assert.True(p.Closed); Assert.Equal(0, t.Sends);
    }

    [Fact]
    public async Task TimeoutClosesEvenWithNoPackets()
    {
        var t = new FakeTransport(); var p = new FakePolicy(); var n = new FakeNetwork();
        await using var run = ResponderRun.StartForTest(n, p, t, TimeSpan.FromMilliseconds(100));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(t.Closed); Assert.True(p.Closed); Assert.Equal(0, t.Sends);
    }

    [Fact]
    public async Task ValidPacketIsRevalidatedAndRepliedOnce()
    {
        var t = new FakeTransport(); var p = new FakePolicy(); var n = new FakeNetwork();
        await using var run = ResponderRun.StartForTest(n, p, t, TimeSpan.FromSeconds(2));
        t.Deliver();
        await t.Sent.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, t.Sends);
        Assert.True(n.Checks >= 3);
    }

    private sealed class FakeNetwork : IResponderNetwork
    {
        public CancellationToken RevocationToken => CancellationToken.None;
        public void Dispose() { }
        internal bool Available = true;
        internal int DelayOn;
        private int calls;
        internal int Checks => Volatile.Read(ref calls);
        internal TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> RevalidateAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == DelayOn) { Waiting.TrySetResult(); return Pending.Task; }
            return Task.FromResult(Available);
        }
    }

    private sealed class FakePolicy : IResponderPolicy
    {
        internal bool Closed;
        public byte[]? Reply(ResponderPacket packet) => [1];
        public void Dispose() => Closed = true;
    }

    private sealed class FakeTransport : IResponderTransport
    {
        private readonly Channel<ResponderPacket> packets = Channel.CreateUnbounded<ResponderPacket>();
        internal bool Closed;
        internal int Sends;
        internal TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Deliver() => packets.Writer.TryWrite(new ResponderPacket([1]));
        public Task<ResponderPacket> ReceiveAsync(CancellationToken ct) => packets.Reader.ReadAsync(ct).AsTask();
        public Task SendReplyAsync(ResponderPacket token, byte[] data, CancellationToken ct) { Interlocked.Increment(ref Sends); Sent.TrySetResult(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Closed = true; return ValueTask.CompletedTask; }
    }
}
