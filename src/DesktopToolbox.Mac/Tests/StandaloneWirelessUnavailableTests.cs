using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class StandaloneWirelessUnavailableTests
{
    [Fact]
    public async Task IndependentAppCannotChangeToolboxWirelessKeeper()
    {
        var service = new StandaloneWirelessUnavailable();
        foreach (var state in new[] { await service.RefreshAsync(), await service.SetSmoothModeAsync(true), await service.SetSmoothModeAsync(false) })
        {
            Assert.Equal(MacAwdlState.Unavailable, state.State);
            Assert.False(state.CanChange);
            Assert.Contains("暂不", state.Message);
        }
    }
}
