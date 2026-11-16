using DesktopToolbox.App.DeskLink;

namespace NianLink.Smoke.Tests;

public sealed class MultiRecipientBarrierTests
{
    [Fact]
    public void AStaleRenderedDeviceCannotAuthorizeADropAfterSelectionChanges()
    {
        var barrier = new MultiRecipientBarrier();
        var first = barrier.Publish("one");
        Assert.False(barrier.TryGetRecipient(out _));
        Assert.True(barrier.Acknowledge("one", first));
        Assert.True(barrier.TryGetRecipient(out var recipient)); Assert.Equal("one", recipient);
        var second = barrier.Publish("two");
        Assert.False(barrier.TryGetRecipient(out _));
        Assert.False(barrier.Acknowledge("one", first));
        Assert.True(barrier.Acknowledge("two", second));
        Assert.True(barrier.TryGetRecipient(out recipient)); Assert.Equal("two", recipient);
        var third = barrier.Publish("one");
        Assert.False(barrier.Acknowledge("one", first));
        Assert.True(barrier.Acknowledge("one", third));
    }
}
