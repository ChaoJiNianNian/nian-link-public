using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DesktopToolbox.DeskLink;

public enum RecoveryAuthorizationFault { None, Role, Type, Version, RecoveryId, RequestId, HostChallenge, PeerChallenge, Expiry, Identity, Offer, Stage, Slot, DataPortBase, Host, ControlPort, KeyboardPort, DataPort, Digest, Missing, Extra, CaseCollision, Duplicate, InvalidUtf8, Oversize, Truncated }

internal sealed class RecoveryActivationControls
{
    private readonly TaskCompletionSource<bool> hostDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> peerDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int bound;
    internal TaskCompletionSource HostAwaitingConfirmation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource PeerAwaitingConfirmation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource HostConfirmationSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal RecoveryActivationSession? HostSessionForTest { get; set; }
    internal RecoveryActivationSession? PeerSessionForTest { get; set; }
    internal OfflineRecoverySnapshot? HostSnapshotForTest { get; set; }
    internal OfflineRecoverySnapshot? PeerSnapshotForTest { get; set; }
    internal object? PeerPermitForTest { get; set; }
    internal bool ThrowOnCancellationTest { get; set; }
    internal bool ThrowAfterHostStreamCloseTest { get; set; }
    internal bool ThrowAfterPeerStreamCloseTest { get; set; }
    internal bool ForceHostClosureUnverifiedTest { get; set; }
    internal bool ForcePeerClosureUnverifiedTest { get; set; }
    internal TaskCompletionSource? HostEndSkipped { get; set; }
    internal TaskCompletionSource? PeerEndSkipped { get; set; }
    internal ControlledLivePublishBarrier? BeforeHostAuthorization { get; set; }
    internal ControlledLivePublishBarrier? AfterHostFinalCheck { get; set; }
    internal ControlledLivePublishBarrier? BeforePeerActivation { get; set; }
    internal ControlledLivePublishBarrier? BeforePeerAuthorizationRead { get; set; }
    internal RecoveryAuthorizationFault AuthorizationFault { get; set; }
    internal bool CaptureAuthorization { get; set; }
    internal byte[]? CapturedAuthorization { get; set; }
    internal byte[]? ReplayAuthorization { get; set; }
    internal bool ReplayInjected { get; set; }
    internal TaskCompletionSource? AuthorizationWritten { get; set; }
    internal TaskCompletionSource? AuthorizationPartialWritten { get; set; }
    internal TaskCompletionSource<string>? PeerAuthorizationRejected { get; set; }
    internal void Bind() { if (Interlocked.Exchange(ref bound, 1) != 0) throw new InvalidOperationException("Recovery controls cannot be reused."); }
    internal void ConfirmHost() { if (!HostAwaitingConfirmation.Task.IsCompleted || !hostDecision.TrySetResult(true)) throw new InvalidOperationException("Host confirmation is unavailable."); }
    internal void RejectHost() { if (!HostAwaitingConfirmation.Task.IsCompleted || !hostDecision.TrySetResult(false)) throw new InvalidOperationException("Host confirmation is unavailable."); }
    internal void ConfirmPeer() { if (!PeerAwaitingConfirmation.Task.IsCompleted || !peerDecision.TrySetResult(true)) throw new InvalidOperationException("Peer confirmation is unavailable."); }
    internal void RejectPeer() { if (!PeerAwaitingConfirmation.Task.IsCompleted || !peerDecision.TrySetResult(false)) throw new InvalidOperationException("Peer confirmation is unavailable."); }
    internal Task<bool> HostDecision(CancellationToken ct) => hostDecision.Task.WaitAsync(ct);
    internal Task<bool> PeerDecision(CancellationToken ct) => peerDecision.Task.WaitAsync(ct);
}

// Private state for one actually authenticated test TLS stream; this is not a product authorization API.
internal sealed class RecoveryActivationSession
{
    private readonly object gate = new();
    private readonly SslStream stream;
    private readonly OfflineRecoverySnapshot ownSnapshot;
    private readonly Func<DateTimeOffset> clock;
    private readonly CancellationTokenSource lifetime;
    private readonly bool host;
    private DateTimeOffset last;
    private bool stopped, parametersSet, localConfirmed, remoteConfirmed, receivedAuthorization, peerClaimed;
    private CancellationTokenSource? stopFaultTest;
    private object? peerPermit;
    private PeerInvitationStore? peerStore;
    internal Guid RecoveryId { get; private set; }
    internal Guid RequestId { get; private set; }
    internal DateTimeOffset ExpiresAtUtc { get; private set; }
    internal string HostChallenge { get; private set; } = "";
    internal string PeerChallenge { get; private set; } = "";
    internal OfflineRecoveryBinding Binding => ownSnapshot.Binding;
    internal Task<System.Text.Json.JsonDocument> ReadFrameAsync(CancellationToken ct) => RecoveryActivationProtocol.ReadAsync(stream, ct);

    internal RecoveryActivationSession(SslStream stream, OfflineRecoverySnapshot snapshot, bool host, Func<DateTimeOffset> clock, CancellationTokenSource lifetime)
    {
        this.stream = stream; ownSnapshot = snapshot; this.host = host; this.clock = clock; this.lifetime = lifetime; last = clock();
        var identity = snapshot.Binding.Identity;
        if (snapshot.IsExpired || !stream.IsAuthenticated || !stream.IsEncrypted || !stream.IsMutuallyAuthenticated ||
            stream.SslProtocol is not (SslProtocols.Tls12 or SslProtocols.Tls13) ||
            !Matches(host ? identity.HostFingerprint : identity.PeerFingerprint, stream.LocalCertificate) ||
            !Matches(host ? identity.PeerFingerprint : identity.HostFingerprint, stream.RemoteCertificate))
            throw new InvalidOperationException("Recovery TLS credentials do not match the protected snapshot.");
    }
    private static bool Matches(string pin, X509Certificate? certificate) => certificate is not null &&
        DeskLinkFingerprint.Matches(pin, Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())));
    internal void SetParameters(Guid recovery, Guid request, string hostChallenge, DateTimeOffset expires, CancellationToken ct)
    {
        lock (gate)
        {
            ct.ThrowIfCancellationRequested(); var now = clock();
            if (stopped || parametersSet || recovery == Guid.Empty || request == Guid.Empty || now < last || expires <= now || expires > now.AddSeconds(30) ||
                expires > Binding.Identity.ExpiresAtUtc || expires > Binding.Offer.ExpiresAtUtc)
                throw new InvalidOperationException("Invalid fresh recovery session.");
            RecoveryId = recovery; RequestId = request; HostChallenge = hostChallenge; ExpiresAtUtc = expires; parametersSet = true; last = now;
        }
    }
    internal void SetPeerChallenge(string value, CancellationToken ct)
    {
        lock (gate) { CheckCore(ct); if (PeerChallenge.Length != 0 || value == HostChallenge) throw new InvalidOperationException("Invalid recovery challenge."); PeerChallenge = value; }
    }
    internal DateTimeOffset CheckTime(CancellationToken ct) { lock (gate) return CheckCore(ct); }
    private DateTimeOffset CheckCore(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var now = clock();
        if (stopped || !parametersSet || now < last || now >= ExpiresAtUtc) throw new InvalidOperationException("Fresh recovery session expired or stopped.");
        last = now; return now;
    }
    internal async Task AwaitLocalConfirmationAsync(RecoveryActivationControls controls, CancellationToken ct)
    {
        CheckTime(ct);
        if (host) controls.HostAwaitingConfirmation.TrySetResult(); else controls.PeerAwaitingConfirmation.TrySetResult();
        var approved = host ? await controls.HostDecision(ct) : await controls.PeerDecision(ct);
        lock (gate)
        {
            CheckCore(ct);
            if (!approved || localConfirmed) throw new InvalidOperationException("Local recovery confirmation was rejected or reused.");
            localConfirmed = true;
        }
    }
    internal async Task ReceiveRemoteConfirmationAsync(CancellationToken ct)
    {
        using var frame = await RecoveryActivationProtocol.ReadAsync(stream, ct);
        RecoveryActivationProtocol.Validate(frame.RootElement, host ? "recovery-peer-confirm" : "recovery-host-confirm", host ? "peer" : "host", Binding,
            RecoveryId, RequestId, HostChallenge, PeerChallenge, ExpiresAtUtc);
        lock (gate) { CheckCore(ct); if (!localConfirmed || remoteConfirmed) throw new InvalidOperationException(); remoteConfirmed = true; }
    }
    internal void CheckHostConfirmed(OfflineRecoverySnapshot snapshot, CancellationToken ct)
    {
        lock (gate)
        {
            CheckCore(ct);
            if (!host || !localConfirmed || !remoteConfirmed || PeerChallenge.Length == 0 || !ReferenceEquals(snapshot, ownSnapshot))
                throw new InvalidOperationException("Host recovery confirmation is incomplete.");
        }
    }
    internal async Task<object> ReceivePeerAuthorizationAsync(PeerInvitationStore store, OfflineRecoverySnapshot snapshot, RecoveryActivationControls controls, CancellationToken ct)
    {
        if (controls.BeforePeerAuthorizationRead is not null) await controls.BeforePeerAuthorizationRead.PauseAsync();
        try
        {
            using var frame = await RecoveryActivationProtocol.ReadAsync(stream, ct);
            RecoveryActivationProtocol.Validate(frame.RootElement, "recovery-authorized", "host", Binding,
                RecoveryId, RequestId, HostChallenge, PeerChallenge, ExpiresAtUtc);
        }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or EndOfStreamException or FormatException or InvalidOperationException)
        {
            controls.PeerAuthorizationRejected?.TrySetResult(error is EndOfStreamException ? "eof" : "invalid"); throw;
        }
        lock (gate)
        {
            CheckCore(ct);
            if (host || !localConfirmed || !remoteConfirmed || receivedAuthorization || !ReferenceEquals(snapshot, ownSnapshot))
                throw new InvalidOperationException("Peer recovery confirmation is incomplete.");
            receivedAuthorization = true; peerStore = store; return peerPermit = new object();
        }
    }
    internal void ClaimPeerActivation(PeerInvitationStore store, OfflineRecoverySnapshot snapshot, object permit, CancellationToken ct)
    {
        lock (gate)
        {
            CheckCore(ct);
            if (host || !receivedAuthorization || peerClaimed || !ReferenceEquals(snapshot, ownSnapshot) || !ReferenceEquals(store, peerStore) || !ReferenceEquals(permit, peerPermit))
                throw new InvalidOperationException("Peer recovery permit is unavailable.");
            peerClaimed = true;
        }
    }
    internal void CheckPeerActivation(PeerInvitationStore store, OfflineRecoverySnapshot snapshot, object permit, CancellationToken ct)
    {
        lock (gate)
        {
            CheckCore(ct);
            if (host || !receivedAuthorization || !peerClaimed || !ReferenceEquals(snapshot, ownSnapshot) || !ReferenceEquals(store, peerStore) || !ReferenceEquals(permit, peerPermit))
                throw new InvalidOperationException("Peer recovery permit is no longer available.");
        }
    }
    internal void PublishPeerActivation(PeerInvitationStore store, OfflineRecoverySnapshot snapshot, object permit, string temporary, string target, CancellationToken ct)
    {
        store.ValidateRecoveryTestPaths(temporary, target);
        lock (gate)
        {
            CheckCore(ct);
            if (host || !receivedAuthorization || !peerClaimed || !ReferenceEquals(snapshot, ownSnapshot) || !ReferenceEquals(store, peerStore) || !ReferenceEquals(permit, peerPermit))
                throw new InvalidOperationException("Peer recovery permit is no longer available.");
            store.AfterRecoveryFinalCheck?.PauseInsideGate();
            File.Move(temporary, target, false);
            peerPermit = null;
        }
    }

    internal async Task WriteOrdinaryFrameAsync(string type, string role, string peerChallenge, CancellationToken ct)
    {
        lock (gate)
        {
            CheckCore(ct);
            var allowed = host
                ? role == "host" && (type == "recovery-init" || type == "recovery-host-confirm" && localConfirmed)
                : role == "peer" && (type == "recovery-hello" || type == "recovery-peer-confirm" && localConfirmed && remoteConfirmed);
            if (!allowed) throw new InvalidOperationException("Recovery frame cannot be sent by this path.");
        }
        await WriteFrameCoreAsync(type, role, peerChallenge, ct);
    }

    internal async Task SendAuthorizedFromVaultAsync(MultiDeviceCredentialVault vault, OfflineRecoverySnapshot snapshot, object permit,
        RecoveryActivationControls controls, CancellationToken ct)
    {
        await vault.ClaimActivationSendTestAsync(this, snapshot, permit, ct);
        CheckHostConfirmed(snapshot, ct);
        if (controls.AfterHostFinalCheck is not null) await controls.AfterHostFinalCheck.PauseAsync();
        await WriteFrameCoreAsync("recovery-authorized", "host", PeerChallenge, ct, controls);
    }

    private async Task WriteFrameCoreAsync(string type, string role, string peerChallenge, CancellationToken ct, RecoveryActivationControls? controls = null)
    {
        CheckTime(ct); var bytes = RecoveryActivationProtocol.Encode(this, type, role, peerChallenge);
        try
        {
            if (controls?.CaptureAuthorization == true) controls.CapturedAuthorization = bytes.ToArray();
            if (controls?.ReplayAuthorization is not null) { CryptographicOperations.ZeroMemory(bytes); bytes = controls.ReplayAuthorization.ToArray(); controls.ReplayInjected = true; }
            if (controls is not null && controls.AuthorizationFault != RecoveryAuthorizationFault.None)
            {
                var altered = RecoveryActivationFaults.Mutate(bytes, controls.AuthorizationFault);
                CryptographicOperations.ZeroMemory(bytes); bytes = altered;
            }
            if (controls?.AuthorizationFault == RecoveryAuthorizationFault.Truncated)
            {
                await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), ct); await stream.FlushAsync(ct);
                controls.AuthorizationPartialWritten?.TrySetResult(); await stream.DisposeAsync();
                if (controls.PeerAuthorizationRejected is not null) await controls.PeerAuthorizationRejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
                throw new IOException("Synthetic truncated recovery authorization.");
            }
            if (controls?.AuthorizationFault != RecoveryAuthorizationFault.Oversize && bytes.Length + 1 > 8192) throw new InvalidDataException("Recovery frame too large.");
            await stream.WriteAsync(bytes, ct); await stream.WriteAsync(new byte[] { (byte)'\n' }, ct); await stream.FlushAsync(ct);
            controls?.AuthorizationWritten?.TrySetResult();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal DateTimeOffset CheckInitialTime(CancellationToken ct)
    {
        lock (gate)
        {
            ct.ThrowIfCancellationRequested(); var now = clock();
            if (stopped || now < last) throw new InvalidOperationException("Recovery session clock moved backwards.");
            last = now; return now;
        }
    }
    internal void CheckOwner(bool expectedHost, OfflineRecoverySnapshot snapshot, CancellationToken ct)
    {
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            if (stopped || host != expectedHost || !ReferenceEquals(snapshot, ownSnapshot)) throw new InvalidOperationException("Recovery session does not belong to this store.");
        }
    }
    internal async Task CloseTransportTestAsync()
    {
        Stop(); await stream.DisposeAsync();
    }
    internal void InstallThrowingStopCallbackTest()
    {
        lock (gate)
        {
            if (stopped || stopFaultTest is not null) throw new InvalidOperationException("Stop callback test is unavailable.");
            stopFaultTest = new CancellationTokenSource();
            stopFaultTest.Token.Register(static () => throw new IOException("Synthetic explicit stop callback failure."));
        }
    }
    internal void Stop()
    {
        CancellationTokenSource? fault;
        lock (gate) { if (stopped) return; stopped = true; fault = stopFaultTest; stopFaultTest = null; }
        Exception? failure = null;
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { } catch (Exception error) { failure = error; }
        try { fault?.Cancel(); } catch (Exception error) { failure ??= error; }
        finally { fault?.Dispose(); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
