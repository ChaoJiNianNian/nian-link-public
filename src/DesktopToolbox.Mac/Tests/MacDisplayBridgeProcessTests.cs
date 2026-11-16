using System.Diagnostics;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.Services.Display;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacDisplayBridgeProcessTests
{
    private static MacDisplayBridgeProcess Create(TimeSpan? timeout = null)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "bridge-fixture", "BridgeChildFixture.dll");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null && (dotnet is null || !File.Exists(dotnet)); current = current.Parent)
        {
            var bundled = Path.Combine(current.FullName, ".tools", "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(bundled)) dotnet = bundled;
        }
        if (dotnet is null || !File.Exists(fixture)) throw new IOException("The owned process test fixture or SDK host is missing.");
        return new(dotnet, timeout ?? TimeSpan.FromSeconds(3), [fixture]);
    }
    private static async Task<int> WaitForChildAsync(MacDisplayBridgeProcess bridge)
    {
        var deadline = Stopwatch.StartNew();
        while (bridge.OwnedChildIds.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
        return Assert.Single(bridge.OwnedChildIds);
    }
    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task AHardTimeoutKillsAndWaitsItsOwnedChildBeforeAnotherRequest()
    {
        await using var bridge = Create(TimeSpan.FromMilliseconds(500));
        var pending = bridge.SetInputAsync(new(0x61a9, 0x27b1), 1);
        var pid = await WaitForChildAsync(bridge);
        await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.False(IsAlive(pid)); Assert.False(bridge.HasUnfinishedChildren);
        Assert.True((await bridge.SetInputAsync(new(0x61a9, 0x27b1), 3)).CommandAccepted);
    }

    [Fact]
    public async Task DisposeCancelsAndAwaitsTheActualChildBeforeReturning()
    {
        var bridge = Create();
        var pending = bridge.SetInputAsync(new(0x61a9, 0x27b1), 1);
        var pid = await WaitForChildAsync(bridge);
        await bridge.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(IsAlive(pid)); Assert.Empty(bridge.OwnedChildIds);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => bridge.SetInputAsync(new(0x61a9, 0x27b1), 3));
    }

    [Fact]
    public async Task StopCancelsAndWaitsTheOwnedChildAndTheBridgeRemainsReusable()
    {
        await using var bridge = Create();
        var pending = bridge.SetInputAsync(new(0x61a9, 0x27b1), 1);
        var pid = await WaitForChildAsync(bridge);
        await bridge.StopOwnedChildrenAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(IsAlive(pid)); Assert.False(bridge.HasUnfinishedChildren);
        Assert.True((await bridge.SetInputAsync(new(0x61a9, 0x27b1), 3)).CommandAccepted);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task InvalidOrOversizedJsonNeverBecomesAnAcceptedDisplayCommand(ushort input)
    {
        await using var bridge = Create();
        await Assert.ThrowsAnyAsync<Exception>(() => bridge.SetInputAsync(new(0x61a9, 0x27b1), input));
        Assert.False(bridge.HasUnfinishedChildren);
    }
}
