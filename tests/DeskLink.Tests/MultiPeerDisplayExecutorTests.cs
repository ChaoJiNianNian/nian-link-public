using DesktopToolbox.DeskLink.Coordination;

namespace DeskLink.Tests;

public sealed class MultiPeerDisplayExecutorTests
{
    [Fact]
    public async Task RevokingPermissionWhileReadingTheDisplayPreventsTheWrite()
    {
        var guard = new MultiPeerDisplayCommandGuard("screen", [15u, 16u]);
        var allowed = true;
        var device = new WaitingReadDevice();
        var executor = new MultiPeerDisplayExecutor(guard, device);
        var work = executor.ExecuteAsync(Request(guard), default, () => allowed);
        await device.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        allowed = false; device.Continue.TrySetResult();
        Assert.Equal("failed", (await work).Status);
        Assert.Equal(0, device.Writes);
    }

    private sealed class WaitingReadDevice : IMultiPeerDisplayDevice
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes { get; private set; }
        public async Task<DisplayInputObservation> ReadAsync(string monitor, CancellationToken ct)
        { Entered.TrySetResult(); await Continue.Task.WaitAsync(ct); return new(15, true); }
        public Task<DisplaySwitchOutcome> WriteAsync(string monitor, uint input, CancellationToken ct)
        { Writes++; return Task.FromResult(new DisplaySwitchOutcome("acceptedUnverified")); }
    }

    [Fact]
    public async Task CurrentInputMustBeReliableBeforeAnyWrite()
    {
        var guard = new MultiPeerDisplayCommandGuard("screen", [15u, 16u]);
        var device = new FakeDevice { Observation = new(15, false) };
        var executor = new MultiPeerDisplayExecutor(guard, device);
        var request = Request(guard);
        Assert.Equal("failed", (await executor.ExecuteAsync(request with { MonitorId = "unapproved" })).Status);
        Assert.Equal(0, device.Reads);
        Assert.Equal("failed", (await executor.ExecuteAsync(request)).Status);
        Assert.Equal(0, device.Writes);
        device.Observation = new(15, true);
        Assert.Equal("acceptedUnverified", (await executor.ExecuteAsync(request)).Status);
        Assert.Equal(1, device.Writes);
        Assert.Equal("failed", (await executor.ExecuteAsync(request)).Status);
        Assert.Equal(1, device.Writes);
    }

    [Fact]
    public async Task DriverMustFinishBeforeAnotherCommandCanStartEvenAfterCancellation()
    {
        var guard = new MultiPeerDisplayCommandGuard("screen", [15u, 16u]);
        var device = new FakeDevice { Observation = new(15, true), WaitForWrite = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var executor = new MultiPeerDisplayExecutor(guard, device);
        using var cancel = new CancellationTokenSource();
        var first = executor.ExecuteAsync(Request(guard), cancel.Token);
        await device.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        Assert.False(first.IsCompleted);
        Assert.Equal("failed", (await executor.ExecuteAsync(Request(guard) with { Revision = 2 })).Status);
        Assert.Equal(1, device.Writes);
        device.WaitForWrite.SetResult(new("acceptedUnverified"));
        Assert.Equal("acceptedUnverified", (await first).Status);
    }

    private static MultiPeerDisplayRequest Request(MultiPeerDisplayCommandGuard guard) =>
        new(Guid.NewGuid(), guard.Epoch, "screen", 15, 16, 1, DateTimeOffset.UtcNow.AddSeconds(8));

    private sealed class FakeDevice : IMultiPeerDisplayDevice
    {
        public DisplayInputObservation Observation { get; set; } = new(null, false);
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DisplaySwitchOutcome>? WaitForWrite { get; set; }
        public Task<DisplayInputObservation> ReadAsync(string monitor, CancellationToken ct) { Reads++; return Task.FromResult(Observation); }
        public Task<DisplaySwitchOutcome> WriteAsync(string monitor, uint input, CancellationToken ct)
        {
            Writes++; WriteEntered.TrySetResult();
            return WaitForWrite?.Task ?? Task.FromResult(new DisplaySwitchOutcome("acceptedUnverified"));
        }
    }
}
