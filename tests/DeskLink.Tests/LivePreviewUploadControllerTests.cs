using DesktopToolbox.DeskLink.Collaboration.LivePreview;

namespace DeskLink.Tests;

public sealed class LivePreviewUploadControllerTests
{
    [Fact]
    public async Task OnlyApprovedDemandStartsAndRevokingApprovalStopsTheStream()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var controller = new LivePreviewUploadController(async (_, token) =>
        {
            Interlocked.Increment(ref calls); started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { ended.TrySetResult(); }
        });
        var demand = new LivePreviewDemand(Guid.NewGuid(), new());
        await controller.ObserveAsync(demand, false);
        Assert.Equal(0, calls);
        await controller.ObserveAsync(demand, true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await controller.ObserveAsync(demand, true);
        Assert.Equal(1, calls);
        await controller.ObserveAsync(demand, false);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await controller.ObserveAsync(demand, true);
        Assert.Equal(1, calls); // an old demand cannot restart after consent is withdrawn
    }

    [Fact]
    public async Task ANewDemandMayStartAfterAFormerStreamEnded()
    {
        var calls = 0;
        await using var controller = new LivePreviewUploadController(async (_, token) =>
        { Interlocked.Increment(ref calls); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        await controller.ObserveAsync(new(Guid.NewGuid(), new()), true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (Volatile.Read(ref calls) < 1) await Task.Delay(5, timeout.Token);
        await controller.ObserveAsync(null, false);
        await controller.ObserveAsync(new(Guid.NewGuid(), new()), true);
        while (Volatile.Read(ref calls) < 2) await Task.Delay(5, timeout.Token);
        Assert.Equal(2, calls);
    }
}
