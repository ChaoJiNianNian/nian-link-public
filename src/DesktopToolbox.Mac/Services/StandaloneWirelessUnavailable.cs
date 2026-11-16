using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Services;

/// <summary>Do not operate the toolbox's system-wide wireless keeper from the standalone app.</summary>
internal sealed class StandaloneWirelessUnavailable : IMacWirelessModeService
{
    private static MacWirelessSnapshot Unavailable => new(MacAwdlState.Unavailable,
        "独立版暂不支持无线顺畅模式", "此功能暂不可用，不会更改现有无线设置。");

    public Task<MacWirelessSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Unavailable);
    public Task<MacWirelessSnapshot> SetSmoothModeAsync(bool enabled, CancellationToken cancellationToken = default) => Task.FromResult(Unavailable);
}
