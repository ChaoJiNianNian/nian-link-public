using System.Net;
using DesktopToolbox.DeskLink;
using Nian.Input.Control;
using Nian.Input.Mac;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Services;

// The production constructor always supplies the real GUI guard. This internal
// boundary lets platform-independent tests exercise the actual TLS host lifecycle.
internal interface IMacNativeDesktopHost
{
    IPreviewInputTarget Target { get; }
    bool IsGuardHealthy { get; }
    bool IsCurrentEventEligibleForLocalApproval();
    void SetArmed(bool value, TimeSpan? lease);
    bool RenewLease(long expectedConsentVersion, TimeSpan lease);
    void RefreshSafety();
    PreviewSourceGeometry? ReadPreviewGeometry();
}

internal sealed class MacNativeDesktopHost(MacDesktopInputTarget target, MacDesktopLocalEventGuard guard) : IMacNativeDesktopHost
{
    public IPreviewInputTarget Target => target;
    public bool IsGuardHealthy => guard.IsHealthy;
    public bool IsCurrentEventEligibleForLocalApproval() => MacDesktopLocalEventGuard.IsCurrentEventEligibleForLocalApproval();
    public void SetArmed(bool value, TimeSpan? lease) => target.SetArmed(value, lease);
    public bool RenewLease(long expected, TimeSpan lease) => target.RenewLease(expected, lease);
    public void RefreshSafety() => target.RefreshSafety();
    public PreviewSourceGeometry? ReadPreviewGeometry() => MacDesktopSource.ReadPreviewGeometry(1, 1);
}

internal sealed record MacNativeLinkOptions
{
    public IPAddress? BindAddress { get; init; }
    public int ReceiverPort { get; init; } = 24932;
    public IIdentityVault? IdentityVault { get; init; }
    public DesktopControlArbiter? SharedArbiter { get; init; }
    public TimeProvider? Clock { get; init; }
    public Func<DeskLinkDisplayBinding, CancellationToken, Task<MacLocalDisplaySwitchResult>>? SwitchDisplayAsync { get; init; }
}
