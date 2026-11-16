using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class MultiPeerDisplayCommandGuardTests
{
    [Fact]
    public void AcceptsOnlyLocallyApprovedMonitorPortsAndCurrentInput()
    {
        var guard = new MultiPeerDisplayCommandGuard("screen", [15u, 16u, 17u]);
        var request = new MultiPeerDisplayRequest(Guid.NewGuid(), guard.Epoch, "screen", 16, 17, 5, DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Throws<InvalidOperationException>(() => guard.Begin(request, 15));
        Assert.Throws<InvalidOperationException>(() => guard.Begin(request with { MonitorId = "other" }, 16));
        Assert.Throws<InvalidOperationException>(() => guard.Begin(request with { TargetInput = 18 }, 16));
        guard.Begin(request, 16);
        Assert.Throws<InvalidOperationException>(() => guard.Begin(request with { Id = Guid.NewGuid(), Revision = 6 }, 16));
        guard.Complete(request.Id);
        Assert.Throws<InvalidOperationException>(() => guard.Begin(request, 16));
    }

    [Fact]
    public void RejectsExpiredOldSessionAndStaleRevision()
    {
        var now = DateTimeOffset.UtcNow;
        var guard = new MultiPeerDisplayCommandGuard("screen", [15u, 16u], () => now);
        var request = new MultiPeerDisplayRequest(Guid.NewGuid(), guard.Epoch, "screen", 15, 16, 8, now.AddSeconds(5));
        Assert.Throws<InvalidOperationException>(() => guard.Begin(request with { Epoch = Guid.NewGuid() }, 15));
        Assert.Throws<InvalidOperationException>(() => guard.Begin(request with { ExpiresUtc = now }, 15));
        Assert.Throws<InvalidOperationException>(() => guard.Begin(request with { ExpiresUtc = now.AddMinutes(1) }, 15));
        guard.Begin(request, 15); guard.Complete(request.Id);
        Assert.Throws<InvalidOperationException>(() => guard.Begin(request with { Id = Guid.NewGuid(), Revision = 7 }, 15));
    }

    [Fact]
    public void WrongCompletionCannotReleaseAnInFlightOperation()
    {
        var guard = new MultiPeerDisplayCommandGuard("screen", [15u, 16u]);
        var request = new MultiPeerDisplayRequest(Guid.NewGuid(), guard.Epoch, "screen", 15, 16, 1, DateTimeOffset.UtcNow.AddSeconds(5));
        guard.Begin(request, 15);
        Assert.Throws<InvalidOperationException>(() => guard.Complete(Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => guard.Begin(request with { Id = Guid.NewGuid(), Revision = 2 }, 15));
        guard.Complete(request.Id);
    }
}
