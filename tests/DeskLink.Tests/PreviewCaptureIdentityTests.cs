using DesktopToolbox.DeskLink.Collaboration;

namespace DeskLink.Tests;

public sealed class PreviewCaptureIdentityTests
{
    [Fact]
    public void LockAndUnlockChangeSourceRevisionWithoutReusingSequence()
    {
        var identity = new PreviewCaptureIdentity();
        var request = Guid.NewGuid();
        var enabled = identity.SetAvailability("enabled");
        Assert.Equal(1, identity.NextSequence(request));
        var locked = identity.SetAvailability("locked");
        Assert.True(locked.Revision > enabled.Revision);
        var unlocked = identity.SetAvailability("enabled");
        Assert.True(unlocked.Revision > locked.Revision);
        Assert.Equal(2, identity.NextSequence(request));
        Assert.Equal(1, identity.NextSequence(Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => identity.SetAvailability("unexpected"));
    }
}
