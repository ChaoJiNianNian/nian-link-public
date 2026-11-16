using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacStartupFailureTests
{
    [Fact]
    public void StartupFailureReportsTheOriginalExceptionButDefersShutdownUntilTheLoopRuns()
    {
        var pending = new Queue<Action>();
        var loopStarted = false; var exitCode = 0; int? shutdownCode = null; string? error = null;
        var original = new InvalidOperationException("original native initialization failure");
        MacStartupFailure.Schedule(original, message => error = message, code => exitCode = code, pending.Enqueue, code =>
        {
            Assert.True(loopStarted, "Shutdown during framework construction would close the dispatcher before MainLoop starts.");
            shutdownCode = code;
        });
        Assert.Equal(2, exitCode); Assert.Null(shutdownCode);
        Assert.Contains(original.GetType().FullName!, error); Assert.Contains(original.Message, error);
        Assert.Single(pending);
        loopStarted = true; pending.Dequeue()();
        Assert.Equal(2, shutdownCode);
    }

    [Fact]
    public void ADispatcherSchedulingFailureIsNotConvertedIntoASuccessfulExit()
    {
        var original = new IOException("original failure");
        var scheduling = new InvalidOperationException("dispatcher unavailable");
        var exitCode = 0; string? error = null;
        var thrown = Assert.Throws<InvalidOperationException>(() => MacStartupFailure.Schedule(original,
            message => error = message, code => exitCode = code, _ => throw scheduling, _ => Assert.Fail("Shutdown cannot run synchronously.")));
        Assert.Same(scheduling, thrown); Assert.Equal(2, exitCode); Assert.Contains(original.Message, error);
    }
}
