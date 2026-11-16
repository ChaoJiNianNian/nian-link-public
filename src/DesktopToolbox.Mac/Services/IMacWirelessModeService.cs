using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Services;

public interface IMacWirelessModeService
{
    Task<MacWirelessSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    Task<MacWirelessSnapshot> SetSmoothModeAsync(bool enabled, CancellationToken cancellationToken = default);
}
