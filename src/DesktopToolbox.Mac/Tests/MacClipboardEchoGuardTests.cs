using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacClipboardEchoGuardTests
{
    [Fact]
    public void EnablingDoesNotSendThePreviouslyCopiedContent()
    {
        var guard = new MacClipboardEchoGuard();
        Assert.False(guard.ShouldPublish(4, "old"));
        Assert.False(guard.ShouldPublish(4, "old"));
        Assert.True(guard.ShouldPublish(5, "new"));
    }
    [Fact]
    public void ApplyingRemoteContentDoesNotEchoItBackButLaterUserCopyWorks()
    {
        var guard = new MacClipboardEchoGuard();
        guard.ShouldPublish(1, "baseline");
        guard.MarkRemoteApplied(2);
        Assert.False(guard.ShouldPublish(2, "remote"));
        Assert.True(guard.ShouldPublish(3, "remote"));
        Assert.True(guard.ShouldPublish(4, "local"));
    }
    [Fact]
    public void AnUnsupportedOrProtectedLocalCopyClearsAnOlderRemoteValueOnlyOnce()
    {
        var guard = new MacClipboardEchoGuard();
        Assert.Equal(MacClipboardAction.None, guard.Observe(1, "baseline"));
        Assert.Equal(MacClipboardAction.Clear, guard.Observe(2, null));
        guard.Commit(2);
        Assert.Equal(MacClipboardAction.None, guard.Observe(2, null));
    }
    [Fact]
    public void AFailedPublishRemainsPendingUntilTheNetworkConfirmsIt()
    {
        var guard = new MacClipboardEchoGuard(); guard.Observe(1, "baseline");
        Assert.Equal(MacClipboardAction.Publish, guard.Observe(2, "copy"));
        Assert.Equal(MacClipboardAction.Publish, guard.Observe(2, "copy"));
        guard.Commit(2); Assert.Equal(MacClipboardAction.None, guard.Observe(2, "copy"));
    }
    [Fact]
    public void DisablingResetsBaselineSoReenableCannotReplayAnOldClipboard()
    {
        var guard = new MacClipboardEchoGuard(); guard.ShouldPublish(1, "baseline");
        Assert.True(guard.ShouldPublish(2, "copy"));
        guard.Reset();
        Assert.False(guard.ShouldPublish(3, "copied while off"));
    }
}
