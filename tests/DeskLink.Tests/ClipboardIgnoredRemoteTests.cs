using DesktopToolbox.Windows.DeskSwitch.Collaboration;

namespace DesktopToolbox.DeskLink.Tests;

public sealed class ClipboardIgnoredRemoteTests
{
    [Fact]
    public void IgnoringRemoteImageDoesNotHideAConcurrentLocalCopy()
    {
        var cursor = new ClipboardBridgeCursor("Windows");
        Assert.False(cursor.ShouldPublish(5, true));
        Assert.True(cursor.ShouldApply("legacy-image", "Mac"));
        cursor.Ignore("legacy-image");
        Assert.False(cursor.ShouldApply("legacy-image", "Mac"));
        Assert.True(cursor.ShouldPublish(6, true));
        Assert.True(cursor.ShouldApply("fresh-text", "Mac"));
    }
}
