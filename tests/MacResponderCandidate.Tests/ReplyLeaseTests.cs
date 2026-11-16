using System.Net;
using NianLink.MacResponderCandidate;

public sealed class ReplyLeaseTests
{
    [Fact]
    public void OnlyExactReceivedTokenCanBeConsumedOnceAndSourceIsFrozen()
    {
        var lease = new MacReplyLease();
        var packet = new ResponderPacket([1]);
        var source = new IPEndPoint(IPAddress.Parse("192.168.8.3"), 49100);
        lease.Publish(packet, source);
        source.Port = 49200;
        Assert.False(lease.TryTake(new ResponderPacket([1]), out _));
        Assert.True(lease.TryTake(packet, out var captured));
        Assert.Equal(49100, captured!.Port);
        Assert.False(lease.TryTake(packet, out _));
        lease.Close();
        Assert.Throws<ObjectDisposedException>(() => lease.Publish(packet, source));
    }
}
