using System.Net;
using Nian.Input.Control;
using Nian.Input.Security;
using Nian.Input.Windows;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>Local host construction only. Test seams never come from pairing or remote requests.</summary>
public sealed class NativeIntegratedDeskSwitchOptions
{
    public IPAddress? BindAddress {get;init;}
    public int InputPort {get;init;}=24931;
    public int BootstrapPort {get;init;}=24935;
    public int DataPort {get;init;}=24933;
    public IIdentityVault? IdentityVault {get;init;}
    public ICaptureFactory? CaptureFactory {get;init;}
    public Func<PreviewSourceGeometry?>? ReadSource {get;init;}
    public Func<DeviceIdentity,TrustedEndpoint,CancellationToken,Task<DesktopControlState>>? ReadPeerState {get;init;}
    public TimeProvider? Clock {get;init;}
}
public sealed record SavedNativePeer(int Version,TrustedEndpoint Endpoint,DevicePermission Permissions,bool LocalBidirectionalApproved);
