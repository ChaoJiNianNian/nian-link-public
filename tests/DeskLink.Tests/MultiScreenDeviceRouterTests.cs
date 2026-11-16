using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class MultiScreenDeviceRouterTests
{
    [Fact]
    public async Task ExecutesThroughTheComputerCurrentlyShowingTheSharedScreen()
    {
        var hostGuard = new MultiPeerDisplayCommandGuard("shared27", [15u, 16u]);
        var macGuard = new MultiPeerDisplayCommandGuard("shared27", [15u, 16u]);
        var hostDevice = new FakeDevice(15); var macDevice = new FakeDevice(16);
        var macQueue = new MultiPeerDisplayQueue();
        var router = new MultiScreenDeviceRouter("desktop",
            screen => screen == "shared27" ? new LocalScreenExecutor(hostGuard.Epoch, "win-monitor", new(hostGuard, hostDevice)) : null,
            peer => peer == "mac" ? new RemoteScreenQueue(macGuard.Epoch, "mac-monitor", macQueue) : null);
        var toMac = new MultiScreenPendingSwitch(Guid.NewGuid(), "mac", 1, new("shared27", "desktop", "win-monitor", 15, 16));
        Assert.Equal("acceptedUnverified", (await router.ExecuteAsync(toMac)).Status);
        Assert.Equal(1, hostDevice.Writes); Assert.Equal(0, macDevice.Writes);
        var back = new MultiScreenPendingSwitch(Guid.NewGuid(), "desktop", 2, new("shared27", "mac", "mac-monitor", 16, 15));
        var result = router.ExecuteAsync(back);
        var request = macQueue.Deliver(macGuard.Epoch);
        Assert.NotNull(request);
        var outcome = await new MultiPeerDisplayExecutor(macGuard, macDevice).ExecuteAsync(request);
        macQueue.Complete(request.Id, outcome.Status);
        Assert.Equal("acceptedUnverified", (await result).Status);
        Assert.Equal(1, macDevice.Writes);
    }

    [Fact]
    public async Task MissingPeerAndManualScreensAreNeverDispatched()
    {
        var router = new MultiScreenDeviceRouter("desktop", _ => null, _ => null);
        var remote = new MultiScreenPendingSwitch(Guid.NewGuid(), "desktop", 2, new("shared27", "mac", "mac-monitor", 16, 15));
        Assert.Equal("notAttempted", (await router.ExecuteAsync(remote)).Status);
        var manual = remote with { Plan = remote.Plan with { RequiresManualSwitch = true } };
        Assert.Equal("notAttempted", (await router.ExecuteAsync(manual)).Status);
    }

    private sealed class FakeDevice(uint current) : IMultiPeerDisplayDevice
    {
        public int Writes { get; private set; }
        public Task<DisplayInputObservation> ReadAsync(string monitor, CancellationToken ct) => Task.FromResult(new DisplayInputObservation(current, true));
        public Task<DisplaySwitchOutcome> WriteAsync(string monitor, uint input, CancellationToken ct)
        { Writes++; return Task.FromResult(new DisplaySwitchOutcome("acceptedUnverified")); }
    }
}
