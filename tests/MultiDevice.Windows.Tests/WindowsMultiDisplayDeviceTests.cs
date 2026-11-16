using System.IO;
using System.Text.Json;
using DesktopToolbox.Windows.DeskSwitch;
using DesktopToolbox.DeskLink.Coordination;

namespace MultiDevice.Windows.Tests;

public sealed class WindowsMultiDisplayDeviceTests
{
    [Fact]
    public async Task ExecutorChecksPermissionBeforeUsingTheWindowsBridge()
    {
        var root = Path.Combine(Path.GetTempPath(), "nian-display-executor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "desk-switch.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new DeskSwitchSettings { MonitorId = "local-monitor", LocalInput = 15, RemoteInput = 16, LinkDisplay = true }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        try
        {
            var bridge = new FakeBridge { MonitorId = "local-monitor" };
            var device = new WindowsMultiDisplayDevice(bridge, "host-monitor", "local-monitor", (_, _, _) => Task.FromResult(path), () => true);
            var guard = new MultiPeerDisplayCommandGuard("host-monitor", [15u, 16u]);
            var executor = new MultiPeerDisplayExecutor(guard, device);
            var command = new MultiPeerDisplayRequest(Guid.NewGuid(), guard.Epoch, "host-monitor", 15, 16, 1, DateTimeOffset.UtcNow.AddSeconds(8));
            Assert.Equal("acceptedUnverified", (await executor.ExecuteAsync(command)).Status);
            Assert.Equal("failed", (await executor.ExecuteAsync(command)).Status);
            Assert.Equal(1, bridge.Writes);
        }
        finally { File.Delete(path); Directory.Delete(root); }
    }

    [Fact]
    public async Task OnlyAConfirmedCurrentVideoPathAndMatchingConfigurationCanSwitch()
    {
        var root = Path.Combine(Path.GetTempPath(), "nian-display-config-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "desk-switch.json");
        var settings = new DeskSwitchSettings { MonitorId = "monitor", LocalInput = 15, RemoteInput = 16, LinkDisplay = true };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        try
        {
            var bridge = new FakeBridge();
            var trusted = false;
            var device = new WindowsMultiDisplayDevice(bridge, "monitor", "monitor", (_, _, _) => Task.FromResult(path), () => trusted);
            Assert.False((await device.ReadAsync("monitor", default)).Reliable);
            trusted = true;
            var input = await device.ReadAsync("monitor", default);
            Assert.True(input.Reliable); Assert.Equal(15u, input.CurrentInput);
            Assert.Equal("acceptedUnverified", (await device.WriteAsync("monitor", 16, default)).Status);
            Assert.Equal(1, bridge.Writes);
            await Assert.ThrowsAsync<InvalidOperationException>(() => device.WriteAsync("other", 16, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => device.WriteAsync("monitor", 17, default));
            Assert.Equal(1, bridge.Writes);
        }
        finally { File.Delete(path); Directory.Delete(root); }
    }

    private sealed class FakeBridge : IDisplayBridge
    {
        public int Writes { get; private set; }
        public string MonitorId { get; init; } = "monitor";
        public Task<DisplayMonitorInfo[]> ListAsync(CancellationToken cancellationToken) => Task.FromResult(new[]
        { new DisplayMonitorInfo(MonitorId, "Monitor", 15, [new(15, "DP 1"), new(16, "DP 2")], true, "") });
        public Task ApplyAsync(string configurationFile, string target, bool test, CancellationToken cancellationToken)
        { Writes++; return Task.CompletedTask; }
    }
}
