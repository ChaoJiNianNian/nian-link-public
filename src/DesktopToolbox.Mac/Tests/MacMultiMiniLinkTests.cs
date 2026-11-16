using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.ViewModels;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacMultiMiniLinkTests
{
    [Fact]
    public async Task UnconfirmedHandoffBlocksLocalSwitchButKeepsEmergencyReturn()
    {
        var calls = new List<bool>();
        var link = new MacMultiMiniLink("host", "mac", _ => Task.CompletedTask,
            mac => { calls.Add(mac); return Task.CompletedTask; });
        link.Update(new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
            ConnectionState = DeskLinkConnectionState.Connected }, true, true);
        Assert.False(link.CanSwitch);
        Assert.True(link.CanReturnToWindows);
        Assert.Contains("尚未确认", link.ErrorMessage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.SwitchAsync("mac"));
        await link.SwitchAsync("host");
        Assert.Equal(new[] { false }, calls);
    }
    [Fact]
    public async Task OnlyConfirmedHostAndMacCanBeSelected()
    {
        var selected = new List<bool>();
        var link = new MacMultiMiniLink("host", "mac", _ => Task.CompletedTask,
            mac => { selected.Add(mac); return Task.CompletedTask; });
        link.Update(new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
            ConnectionState = DeskLinkConnectionState.Connected, ActiveComputer = "mac" }, true);
        Assert.True(link.CanSwitch);
        Assert.True(link.IsMacActive);
        Assert.False(link.IsWindowsActive);
        await link.SwitchAsync("host");
        await link.SwitchAsync("mac");
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.SwitchAsync("other"));
        Assert.Equal(new[] { false, true }, selected);
    }

    [Fact]
    public async Task DisconnectedOrUnconfirmedPeerCannotSwitch()
    {
        var link = new MacMultiMiniLink("host", "mac", _ => Task.CompletedTask, _ => Task.CompletedTask);
        link.Update(new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
            ConnectionState = DeskLinkConnectionState.Connected, PendingPeerFingerprint = "pending" }, true);
        Assert.False(link.CanSwitch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.SwitchAsync("host"));
        link.Update(new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
            ConnectionState = DeskLinkConnectionState.Connected }, false);
        Assert.False(link.CanSwitch);
        Assert.False(link.IsMacActive);
        Assert.False(link.IsWindowsActive);
    }

    [Fact]
    public async Task StopIsAvailableOnlyWhileRunningAndNotifiesOnStateChange()
    {
        var stopped = 0;
        var notifications = 0;
        var link = new MacMultiMiniLink("host", "mac", _ => { stopped++; return Task.CompletedTask; }, _ => Task.CompletedTask);
        link.PropertyChanged += (_, _) => notifications++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.StopAsync());
        link.Update(new DeskLinkState { ProcessState = DeskLinkProcessState.Running }, false);
        await link.StopAsync();
        Assert.Equal(1, stopped);
        Assert.Equal(1, notifications);
        Assert.Throws<ArgumentException>(() => new MacMultiMiniLink("same", "SAME", _ => Task.CompletedTask, _ => Task.CompletedTask));
    }
}
