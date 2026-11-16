using DesktopToolbox.DeskLink;
using NianLink.MultiDevice;

namespace MultiDevice.Windows.Tests;

public sealed class PairingReviewGuardTests
{
    [Fact]
    public void ConfirmationRequiresSameReviewGenerationAndUnexpiredIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var original = new MultiPeerPairingPreview("one", "device", "windows", new string('A', 64), now.AddMinutes(1));
        PairingReviewGuard.RequireCurrent(original, original, 3, 3, now);
        Assert.Throws<InvalidOperationException>(() => PairingReviewGuard.RequireCurrent(original, null, 3, 3, now));
        Assert.Throws<InvalidOperationException>(() => PairingReviewGuard.RequireCurrent(original, original with { Id = "two" }, 3, 3, now));
        Assert.Throws<InvalidOperationException>(() => PairingReviewGuard.RequireCurrent(original, original, 3, 4, now));
        Assert.Throws<InvalidOperationException>(() => PairingReviewGuard.RequireCurrent(original, original, 3, 3, original.ExpiresAtUtc));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedReviewDuringEitherDialogPreventsContinuation(bool duringConfirmation)
    {
        var now = DateTimeOffset.UtcNow;
        var original = new MultiPeerPairingPreview("one", "device", "windows", new string('A', 64), now.AddMinutes(1));
        MultiPeerPairingPreview? current = original;
        long generation = 3;
        bool saveOpened = false;
        Assert.Throws<InvalidOperationException>(() => PairingReviewGuard.ChooseDestination(original, 3,
            () => current, () => generation, () => now,
            () => { if (duringConfirmation) { current = original with { Id = "new" }; generation++; } return true; },
            () => { saveOpened = true; if (!duringConfirmation) { current = null; generation++; } return "test.json"; }));
        Assert.Equal(!duringConfirmation, saveOpened);
    }

    [Fact]
    public void ExpiryWhileSavingPreventsContinuation()
    {
        var now = DateTimeOffset.UtcNow;
        var original = new MultiPeerPairingPreview("one", "device", "windows", new string('A', 64), now.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => PairingReviewGuard.ChooseDestination(original, 1,
            () => original, () => 1, () => now,
            () => true, () => { now = original.ExpiresAtUtc; return "test.json"; }));
    }

    [Fact]
    public void UserCancellationKeepsReviewAvailableWithoutChoosingDestination()
    {
        var now = DateTimeOffset.UtcNow;
        var original = new MultiPeerPairingPreview("one", "device", "windows", new string('A', 64), now.AddMinutes(1));
        bool saveOpened = false;
        var result = PairingReviewGuard.ChooseDestination(original, 1, () => original, () => 1, () => now,
            () => false, () => { saveOpened = true; return "test.json"; });
        Assert.Null(result);
        Assert.False(saveOpened);
    }
}
