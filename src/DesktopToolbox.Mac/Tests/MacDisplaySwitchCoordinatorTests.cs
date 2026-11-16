using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacDisplaySwitchCoordinatorTests
{
    private static DeskLinkDisplayBinding Binding(bool enabled = true) => new() { Enabled = enabled, VendorId = 0x61a9, ProductId = 0x27b1, WindowsInput = 15, MacInput = 16 };
    private static DeskLinkState Focus(string active, long revision, int pid = 100) => new() { ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true, ProcessId = pid, ActiveComputer = active, Revision = revision };
    private static MacLocalDisplaySwitchResult Accepted() => new(true, "acceptedUnverified", null);

    [Fact]
    public async Task InitialSnapshotNeverChangesTheDisplayButANewMacToWindowsTransitionDoes()
    {
        var writes = 0;
        var coordinator = new MacDisplaySwitchCoordinator((binding, token) => { writes++; Assert.Equal(15u, binding.WindowsInput); return Task.FromResult(Accepted()); });
        coordinator.Configure(Binding(), "Win-PC", "My-Mac");
        Assert.Null(await coordinator.ObserveAsync(Focus("Win-PC", 0)));
        Assert.Null(await coordinator.ObserveAsync(Focus("My-Mac", 1)));
        Assert.Equal("acceptedUnverified", (await coordinator.ObserveAsync(Focus("Win-PC", 2)))?.VideoStatus);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task ANewWindowsIntentDoesNotRequirePollingTheIntermediateMacIntent()
    {
        var writes = 0;
        using var coordinator = new MacDisplaySwitchCoordinator((binding, _) =>
        {
            writes++; Assert.Equal(15u, binding.WindowsInput); return Task.FromResult(Accepted());
        });
        coordinator.Configure(Binding(), "Win-PC", "My-Mac");
        Assert.Null(await coordinator.ObserveAsync(Focus("Win-PC", 0)));
        Assert.Equal(0, writes);
        // Polling can miss Mac rev1; Windows rev2 still represents a new explicit return intent.
        Assert.Equal("acceptedUnverified", (await coordinator.ObserveAsync(Focus("Win-PC", 2)))?.VideoStatus);
        Assert.Equal(1, writes);
        Assert.Null(await coordinator.ObserveAsync(Focus("Win-PC", 2)));
        Assert.Null(await coordinator.ObserveAsync(Focus("Win-PC", 1)));
        Assert.Null(await coordinator.ObserveAsync(Focus("My-Mac", 1)));
        Assert.Equal(1, writes);
        // A new process's first observation is only a baseline, regardless of its revision.
        Assert.Null(await coordinator.ObserveAsync(Focus("Win-PC", 50, 200)));
        Assert.Null(await coordinator.ObserveAsync(Focus("Win-PC", 50, 200)));
        Assert.Null(await coordinator.ObserveAsync(Focus("Win-PC", 4, 100)));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task ButtonAndPollingShareOneAttemptForTheSameNativeFocusRevision()
    {
        var writes = 0;
        var coordinator = new MacDisplaySwitchCoordinator((_, _) => { writes++; return Task.FromResult(Accepted()); });
        coordinator.Configure(Binding(), "Win-PC", "My-Mac");
        await coordinator.ObserveAsync(Focus("My-Mac", 1));
        await coordinator.ReturnAfterConfirmedFocusAsync(Focus("Win-PC", 2));
        await coordinator.ObserveAsync(Focus("Win-PC", 2));
        Assert.Equal(1, writes);
        await coordinator.ReturnAfterConfirmedFocusAsync(Focus("Win-PC", 2));
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task RestartedProcessCanResetRevisionWithoutReplayingAnInitialSnapshot()
    {
        var writes = 0;
        var coordinator = new MacDisplaySwitchCoordinator((_, _) => { writes++; return Task.FromResult(Accepted()); });
        coordinator.Configure(Binding(), "Win-PC", "My-Mac");
        await coordinator.ObserveAsync(Focus("My-Mac", 50));
        Assert.Null(await coordinator.ObserveAsync(Focus("Win-PC", 0, 200)));
        await coordinator.ObserveAsync(Focus("My-Mac", 1, 200));
        await coordinator.ObserveAsync(Focus("Win-PC", 2, 200));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task OldFocusCannotRollBackTheBaselineOrTriggerAVideoWrite()
    {
        var writes = 0;
        var coordinator = new MacDisplaySwitchCoordinator((_, _) => { writes++; return Task.FromResult(Accepted()); });
        coordinator.Configure(Binding(), "Win-PC", "My-Mac");
        await coordinator.ObserveAsync(Focus("My-Mac", 5));
        Assert.Null(await coordinator.ObserveAsync(Focus("Win-PC", 4)));
        await coordinator.ObserveAsync(Focus("Win-PC", 6));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task LegacyOrDisabledPairingCannotWriteAnyDisplayCommand()
    {
        var writes = 0;
        var coordinator = new MacDisplaySwitchCoordinator((_, _) => { writes++; return Task.FromResult(Accepted()); });
        foreach (var binding in new DeskLinkDisplayBinding?[] { null, Binding(false) })
        {
            coordinator.Configure(binding, "Win-PC", "My-Mac");
            await coordinator.ObserveAsync(Focus("My-Mac", 1));
            Assert.Null(await coordinator.ObserveAsync(Focus("Win-PC", 2)));
            Assert.Equal("disabled", (await coordinator.ReturnAfterConfirmedFocusAsync(Focus("Win-PC", 2))).VideoStatus);
        }
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task ADisconnectClearsTheTransitionBaselineWithoutInventingAFocusTransfer()
    {
        var writes = 0;
        var coordinator = new MacDisplaySwitchCoordinator((_, _) => { writes++; return Task.FromResult(Accepted()); });
        coordinator.Configure(Binding(), "Win-PC", "My-Mac");
        await coordinator.ObserveAsync(Focus("My-Mac", 1));
        coordinator.ResetBaseline();
        Assert.Null(await coordinator.ObserveAsync(Focus("Win-PC", 2)));
        Assert.Equal(0, writes);
        await coordinator.ReturnAfterConfirmedFocusAsync(Focus("Win-PC", 2));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task ExplicitVideoRecoveryRequiresFreshNativeWindowsFocus()
    {
        var writes = 0;
        var coordinator = new MacDisplaySwitchCoordinator((_, _) => { writes++; return Task.FromResult(Accepted()); });
        coordinator.Configure(Binding(), "Win-PC", "My-Mac");
        Assert.Equal("notAttempted", (await coordinator.ReturnAfterConfirmedFocusAsync(Focus("My-Mac", 1))).VideoStatus);
        Assert.Equal("notAttempted", (await coordinator.ReturnAfterConfirmedFocusAsync(Focus("Win-PC", 2) with { NativeControlAvailable = false })).VideoStatus);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task TimedOutNativeWorkStaysInFlightAndDoesNotQueueDuplicateWrites()
    {
        var completion = new TaskCompletionSource<MacLocalDisplaySwitchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var coordinator = new MacDisplaySwitchCoordinator((_, _) => { Interlocked.Increment(ref writes); return completion.Task; }, TimeSpan.FromMilliseconds(50));
        coordinator.Configure(Binding(), "Win-PC", "My-Mac");
        var result = await coordinator.ReturnAfterConfirmedFocusAsync(Focus("Win-PC", 2));
        Assert.Equal("failed", result.VideoStatus);
        Assert.True(coordinator.IsWriteInFlight);
        await coordinator.ReturnAfterConfirmedFocusAsync(Focus("Win-PC", 2));
        await coordinator.ReturnAfterConfirmedFocusAsync(Focus("Win-PC", 3));
        Assert.Equal(1, writes);
        completion.SetResult(Accepted());
        await Task.Delay(30);
        Assert.False(coordinator.IsWriteInFlight);
    }
}
