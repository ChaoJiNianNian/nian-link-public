using System.Net;
using Nian.Input.Control;
using Nian.Input.Security;

namespace Nian.Input.Runtime;

public enum PreviewControlDirection { Send, Receive }
public sealed record PreviewAdmission(bool Allowed, string? Reason = null);

/// <summary>Non-secret presentation state. Invitation secrets are returned only by CreateInvitation.</summary>
public sealed record PreviewOnlyDeviceStatus
{
    public bool Started { get; init; }
    public bool Paired { get; init; }
    public bool ControlActive { get; init; }
    public string? LocalFingerprint { get; init; }
    public string? PeerFingerprint { get; init; }
    public string? PeerName { get; init; }
    public int ReceiverPort { get; init; }
    public string Message { get; init; } = "预览操作尚未准备";
    public string? Error { get; init; }
}

/// <summary>Trusted platform callbacks. Access/prepare/renew callbacks must be synchronous and thread safe.</summary>
public sealed record PreviewOnlyDeviceOptions
{
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;
    public int ReceiverPort { get; init; }
    public TimeProvider? Clock { get; init; }
    public required Func<PreviewSourceGeometry?> ReadCurrentGeometry { get; init; }
    public required Func<string, bool> Prepare { get; init; }
    public required Func<string, long, bool> Renew { get; init; }
    /// <summary>Called on the PairAsync caller's actual click stack, before its first asynchronous operation.</summary>
    public required Action RequireLocalApproval { get; init; }
    /// <summary>The platform verifies its local guard in the actual Accept event before completing true.</summary>
    public required Func<PairingApproval, CancellationToken, Task<bool>> ConfirmIncomingPairingAsync { get; init; }
    public required Func<PreviewControlDirection, string, PreviewAdmission> CheckAccess { get; init; }
}
