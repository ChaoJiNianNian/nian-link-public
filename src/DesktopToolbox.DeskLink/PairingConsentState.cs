namespace DesktopToolbox.DeskLink;

/// <summary>Public identifiers of one proposed pairing, not authentication credentials.</summary>
internal sealed record PairingSessionIdentity(Guid SessionId, Guid OfferNonce, Guid TransactionId, string HostName, string PeerName,
    string HostFingerprint, string PeerFingerprint, DateTimeOffset ExpiresAtUtc);

internal enum PairingConsentPhase
{
    AwaitingConfirmation,
    ReadyToPrepare,
    Preparing,
    ReadyToCommit,
    Committing,
    AwaitingApplied,
    Completed,
    Aborted,
    ResultUnknown
}

/// <summary>
/// In-memory host-side protocol model. It creates no credential, performs no I/O and is not an authenticator.
/// Callers must bind its identity to the actual mutually authenticated TLS connection and genuine user actions.
/// Recovery, durable grant storage and remote operation cancellation are intentionally not implemented here.
/// </summary>
internal sealed class PairingConsentState
{
    private readonly object gate = new();
    private readonly PairingSessionIdentity identity;
    private readonly Func<DateTimeOffset> clock;
    private DateTimeOffset lastTime;
    private bool localConfirmed, remoteConfirmed;
    private PairingConsentPhase phase = PairingConsentPhase.AwaitingConfirmation;

    internal PairingConsentState(PairingSessionIdentity identity, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        var now = this.clock();
        DeskflowProfile.ValidateName(identity.HostName);
        DeskflowProfile.ValidateName(identity.PeerName);
        if (identity.SessionId == Guid.Empty || identity.OfferNonce == Guid.Empty || identity.TransactionId == Guid.Empty ||
            string.Equals(identity.HostName, identity.PeerName, StringComparison.OrdinalIgnoreCase) ||
            identity.ExpiresAtUtc <= now || identity.ExpiresAtUtc > now.AddMinutes(10) ||
            DeskLinkFingerprint.Normalize(identity.HostFingerprint) != identity.HostFingerprint ||
            DeskLinkFingerprint.Normalize(identity.PeerFingerprint) != identity.PeerFingerprint)
            throw new ArgumentException("Invalid pairing session identity.", nameof(identity));
        this.identity = identity; lastTime = now;
    }

    internal PairingConsentPhase Phase
    {
        get { lock (gate) { Expire(); return phase; } }
    }

    internal void ConfirmLocal(PairingSessionIdentity current)
    {
        lock (gate)
        {
            Require(current, PairingConsentPhase.AwaitingConfirmation);
            if (localConfirmed) throw new InvalidOperationException("Local confirmation was already recorded.");
            localConfirmed = true; UpdateConfirmationPhase();
        }
    }

    internal void ConfirmRemote(PairingSessionIdentity current)
    {
        lock (gate)
        {
            Require(current, PairingConsentPhase.AwaitingConfirmation);
            if (remoteConfirmed) throw new InvalidOperationException("Remote confirmation was already recorded.");
            remoteConfirmed = true; UpdateConfirmationPhase();
        }
    }

    // This transition grants no trust and creates no secret; it only allows a future caller
    // to begin its separately verified and durable preparation operation.
    internal void BeginPrepare(PairingSessionIdentity current) => Move(current, PairingConsentPhase.ReadyToPrepare, PairingConsentPhase.Preparing);
    internal void AcceptReady(PairingSessionIdentity current) => Move(current, PairingConsentPhase.Preparing, PairingConsentPhase.ReadyToCommit);
    internal void BeginCommit(PairingSessionIdentity current) => Move(current, PairingConsentPhase.ReadyToCommit, PairingConsentPhase.Committing);
    internal void CommitSucceeded(PairingSessionIdentity current) => Move(current, PairingConsentPhase.Committing, PairingConsentPhase.AwaitingApplied);
    internal void AcceptApplied(PairingSessionIdentity current) => Move(current, PairingConsentPhase.AwaitingApplied, PairingConsentPhase.Completed);

    internal void Stop()
    {
        lock (gate) { Expire(); Invalidate(); }
    }

    private void UpdateConfirmationPhase()
    { if (localConfirmed && remoteConfirmed) phase = PairingConsentPhase.ReadyToPrepare; }

    private void Move(PairingSessionIdentity current, PairingConsentPhase expected, PairingConsentPhase next)
    {
        lock (gate) { Require(current, expected); phase = next; }
    }

    private void Require(PairingSessionIdentity current, PairingConsentPhase expected)
    {
        Expire();
        if (current != identity || phase != expected) throw new InvalidOperationException("Pairing message does not match the active session or phase.");
    }

    private void Expire()
    {
        var now = clock();
        if (now < lastTime || now >= identity.ExpiresAtUtc) Invalidate();
        lastTime = now;
    }

    private void Invalidate()
    {
        if (phase is PairingConsentPhase.Completed or PairingConsentPhase.Aborted or PairingConsentPhase.ResultUnknown) return;
        phase = phase is PairingConsentPhase.Committing or PairingConsentPhase.AwaitingApplied
            ? PairingConsentPhase.ResultUnknown : PairingConsentPhase.Aborted;
        localConfirmed = remoteConfirmed = false;
    }
}
