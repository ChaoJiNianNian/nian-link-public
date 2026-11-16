using Nian.Input.Control;

namespace Nian.Input.Security;

/// <summary>Local host policy. None of these callbacks or objects can be supplied over the wire.</summary>
public sealed class DesktopInputServerOptions
{
    public DesktopControlArbiter? Arbiter {get;init;}
    public PreviewFrameAuthority? PreviewFrames {get;init;}
    /// <summary>Synchronous local safety/consent preparation after acquiring the arbiter. Never await UI or network.</summary>
    public Func<string,DesktopControlMode,bool>? PrepareInput {get;init;}
    /// <summary>Renew only the existing local consent generation; never re-arm a revoked target.</summary>
    public Func<string,DesktopControlMode,long,bool>? RenewInput {get;init;}
    public TimeSpan InputLease {get;init;}=TimeSpan.FromSeconds(2);
    /// <summary>Called after explicit receiver approval and trust commit, before replying success. Does not automatically grant reverse trust.</summary>
    public Action<PairedDevice>? Paired {get;init;}
    /// <summary>Authenticated Control-only state query. Runs outside the input gate and cannot implicitly acquire input ownership.</summary>
    public Func<string,CancellationToken,Task<DesktopControlState>>? ReadStateAsync {get;init;}
    /// <summary>The host must check cancellation and atomically compare ExpectedDisplayIntentRevision before input/display effects.</summary>
    public Func<string,DesktopFullSwitchRequest,CancellationToken,Task<DesktopSwitchResult>>? FullSwitchAsync {get;init;}
}
