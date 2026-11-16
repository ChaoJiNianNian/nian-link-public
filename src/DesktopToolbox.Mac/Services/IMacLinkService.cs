using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Services;

public interface IMacLinkService : IAsyncDisposable
{
    event Action<MacLinkSnapshot>? Changed;
    Task<MacLinkSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    Task StartAsync(MacConnectionSettings settings, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task ConfirmPeerAsync(string fingerprint, bool accept, CancellationToken cancellationToken = default);
    Task<PairingPreview> PreviewPairingAsync(string json, CancellationToken cancellationToken = default);
    Task AcceptPairingAsync(string previewId, CancellationToken cancellationToken = default);
    Task<MacSwitchOutcome> SwitchAsync(string target, CancellationToken cancellationToken = default);
}
