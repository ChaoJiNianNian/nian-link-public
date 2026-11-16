using NianLink.MacDiscoveryCandidate;
using DesktopToolbox.DeskLink;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacFilelessWorkSlotTests
{
    [Fact]
    public void SynchronouslyExpiredRequestDoesNotBlockNextRequest()
    {
        var slot = new FilelessWorkSlot();
        var firstCalls = 0;
        Assert.True(slot.TryStart(() => { firstCalls++; return Task.CompletedTask; }));
        Assert.Equal(1, firstCalls);
        Assert.Null(slot.Current);
        var secondCalls = 0;
        Assert.True(slot.TryStart(() => { secondCalls++; return Task.CompletedTask; }));
        Assert.Equal(1, secondCalls);
        Assert.Null(slot.Current);
    }

    [Fact]
    public async Task PendingRequestCannotBeReplacedAndBecomesAvailableAfterDrain()
    {
        var slot = new FilelessWorkSlot();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(slot.TryStart(() => pending.Task));
        var published = Assert.IsAssignableFrom<Task>(slot.Current);
        Assert.False(slot.TryStart(() => Task.CompletedTask));
        pending.SetResult();
        await published;
        Assert.Null(slot.Current);
        Assert.True(slot.TryStart(() => Task.CompletedTask));
    }

    [Fact]
    public void CleanupFailureBlocksAnyLaterRequest()
    {
        var slot = new FilelessWorkSlot();
        Assert.True(slot.TryStart(() => Task.FromException(new IOException("cleanup failed"))));
        Assert.True(slot.IsPoisoned);
        Assert.False(slot.TryStart(() => Task.CompletedTask));
    }

    [Fact]
    public async Task StartupCleanupFailureCannotBeSwallowedAndBlocksNewWork()
    {
        var slot = new FilelessWorkSlot();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(slot.TryStart(async () =>
        {
            started.TrySetResult();
            try { await failed.Task; }
            catch (Exception error) { FilelessWorkSlot.ThrowIfStartupCleanupFailed(error); }
        }));
        await started.Task;
        var work = Assert.IsAssignableFrom<Task>(slot.Current);
        failed.SetException(new PairingStartupCleanupException(new IOException("startup"), [new IOException("cleanup")]));
        await Assert.ThrowsAsync<PairingStartupCleanupException>(() => work);
        Assert.True(slot.IsPoisoned);
        Assert.False(slot.TryStart(() => Task.CompletedTask));
    }

    [Fact]
    public void OrdinaryStartupFailureCanBeReportedWithoutPoisoning()
    {
        var slot = new FilelessWorkSlot();
        Assert.True(slot.TryStart(() =>
        {
            try { throw new IOException("connection failed"); }
            catch (Exception error) { FilelessWorkSlot.ThrowIfStartupCleanupFailed(error); }
            return Task.CompletedTask;
        }));
        Assert.False(slot.IsPoisoned);
        Assert.True(slot.TryStart(() => Task.CompletedTask));
    }
}
