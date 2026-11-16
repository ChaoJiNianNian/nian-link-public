using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacWindowModeTests
{
    private static MacLinkSnapshot Connected => new() { IsRunning = true, IsConnected = true, ControlConnected = true };

    [Fact]
    public void AnEstablishedInputAndControlConnectionDefaultsToTheTwoButtonWindow() =>
        Assert.True(MacWindowPresentation.UseCompact(Connected, false, false));

    [Fact]
    public void OpeningSettingsKeepsTheFullWindowWithoutInterruptingTheConnection()
    {
        Assert.False(MacWindowPresentation.UseCompact(Connected, true, false));
        Assert.True(MacWindowPresentation.UseCompact(Connected, false, false));
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("input")]
    [InlineData("control")]
    [InlineData("cleanup")]
    [InlineData("error")]
    [InlineData("trust")]
    public void RecoveryOrTrustActionsCannotBeHiddenInTheSmallWindow(string problem)
    {
        var state = problem switch
        {
            "stopped" => Connected with { IsRunning = false },
            "input" => Connected with { IsConnected = false },
            "control" => Connected with { ControlConnected = false },
            "cleanup" => Connected with { HasPendingDisplayCleanup = true },
            "error" => Connected with { Error = "连接需要处理" },
            "trust" => Connected with { PendingPeerFingerprint = "pending" },
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };
        Assert.False(MacWindowPresentation.UseCompact(state, false, false));
    }

    [Fact]
    public void APollingDisplayFailureKeepsRecoveryVisibleEvenWhileBothChannelsRemainConnected()
    {
        var state = Connected with { HasDisplayRecoveryError = true, DisplayRecoveryMessage = "Mac 显示切换失败：命令超时" };
        Assert.Empty(state.Error);
        Assert.False(MacWindowPresentation.UseCompact(state, false, false));
    }

    [Theory]
    [InlineData("failed", true)]
    [InlineData("canceled", true)]
    [InlineData("notAttempted", true)]
    [InlineData("acceptedUnverified", false)]
    [InlineData("verified", false)]
    [InlineData("disabled", false)]
    public void LocalDisplayResultsProjectAttentionWithoutParsingHumanMessages(string videoStatus, bool needsAttention)
    {
        var result = new MacLocalDisplaySwitchResult(true, videoStatus, null);
        Assert.Equal(needsAttention, result.NeedsAttention);
        Assert.Equal(!needsAttention, MacWindowPresentation.UseCompact(Connected with { HasDisplayRecoveryError = result.NeedsAttention }, false, false));
    }

    [Fact]
    public void CollaborationRemainsAvailableForAnApprovedPeerWhenKeyboardSharingStops()
    {
        var stopped = new MacLinkSnapshot { HasApprovedPairing = true };
        Assert.True(MacWindowPresentation.UseCollaboration(stopped, false));
        Assert.False(MacWindowPresentation.UseCollaboration(stopped, true));
        Assert.False(MacWindowPresentation.UseCollaboration(stopped with { PendingPeerFingerprint = "review" }, false));
        Assert.False(MacWindowPresentation.UseCollaboration(stopped with { HasApprovedPairing = false }, false));
    }
    [Fact]
    public void PairingReviewAlwaysStaysInTheFullWindow() =>
        Assert.False(MacWindowPresentation.UseCompact(Connected, false, true));
}