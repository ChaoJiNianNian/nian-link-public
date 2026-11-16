using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesktopToolbox.DeskLink.Coordination;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.Services.Display;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacMultiDisplayDeviceTests
{
    [Fact]
    public async Task MatchingApprovedDisplayExecutesOnceAndReportsUnverified()
    {
        var identity = new MacDisplayIdentity(1, 2);
        var bridge = new FakeBridge(identity);
        var device = new MacMultiDisplayDevice(bridge, "monitor", identity, [15u, 16u], () => true);
        var guard = new MultiPeerDisplayCommandGuard("monitor", [15u, 16u]);
        var executor = new MultiPeerDisplayExecutor(guard, device);
        var command = new MultiPeerDisplayRequest(Guid.NewGuid(), guard.Epoch, "monitor", 15, 16, 1, DateTimeOffset.UtcNow.AddSeconds(8));
        Assert.Equal("acceptedUnverified", (await executor.ExecuteAsync(command)).Status);
        Assert.Equal("failed", (await executor.ExecuteAsync(command)).Status);
        Assert.Equal(1, bridge.Writes);
    }

    [Fact]
    public async Task AmbiguousIdentityOrUnconfirmedVideoPathCannotWrite()
    {
        var identity = new MacDisplayIdentity(1, 2);
        var bridge = new FakeBridge(identity) { Duplicate = true };
        var device = new MacMultiDisplayDevice(bridge, "monitor", identity, [15u, 16u], () => true);
        Assert.False((await device.ReadAsync("monitor", default)).Reliable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.WriteAsync("monitor", 16, default));
        bridge.Duplicate = false;
        var notVisible = new MacMultiDisplayDevice(bridge, "monitor", identity, [15u, 16u], () => false);
        Assert.False((await notVisible.ReadAsync("monitor", default)).Reliable);
        Assert.Equal(0, bridge.Writes);
    }

    private sealed class FakeBridge(MacDisplayIdentity identity) : IMacDisplayControl
    {
        public bool Duplicate { get; set; }
        public int Writes { get; private set; }
        public Task<IReadOnlyList<MacDisplayDescriptor>> ListAsync(CancellationToken ct = default)
        {
            MacDisplayDescriptor entry = new(identity, 1, 1, "monitor", null, 0);
            return Task.FromResult<IReadOnlyList<MacDisplayDescriptor>>(Duplicate ? [entry, entry] : [entry]);
        }
        public Task<MacDisplayReadResult> ReadInputAsync(MacDisplayIdentity value, CancellationToken ct = default) => Task.FromResult(new MacDisplayReadResult(true, 15, 255, 0, null));
        public Task<MacDisplaySetResult> SetInputAsync(MacDisplayIdentity value, ushort input, CancellationToken ct = default) { Writes++; return Task.FromResult(new MacDisplaySetResult(true, 0, null)); }
        public void Dispose() { }
    }
}
