#if NIAN_FENCE_RESEARCH
using System.Security.Cryptography;
using DesktopToolbox.DeskLink.Coordination;

namespace DesktopToolbox.DeskLink;

// Synthetic test controls. Test barriers never execute under a vault lock.
internal sealed class EmergencyResearchControls
{
    internal bool HoldAfterRegistration, HoldAfterPermit;
    internal TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseRegistration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Permitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleasePermit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// No process, native handle, callback or external device can be supplied to it.
internal sealed class EmergencyResearchKeyboard(string host, string[] peers) : IMultiPeerKeyboard
{
    private readonly object sync = new();
    private string active = peers[0];
    private long revision = 1;
    private int switchCalls;
    internal int SwitchCalls => Volatile.Read(ref switchCalls);
    internal void SetSyntheticFocus(string target)
    {
        if (target != host && !peers.Contains(target, StringComparer.Ordinal)) throw new ArgumentException("Unknown synthetic target.");
        lock (sync) { active = target; revision++; }
    }
    public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
    public Task<DeskLinkState> StopAsync(CancellationToken ct) => GetStateAsync(ct);
    public Task<DeskLinkState> GetStateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
            NativeControlAvailable = true, ActiveComputer = active, Revision = revision, ConnectedComputers = peers });
    }
    public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long expectedRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            Interlocked.Increment(ref switchCalls);
            if (target != host || expected != active || expectedRevision != revision)
                return Task.FromResult(new DeskLinkSwitchResult { Error = "stale" });
            active = host; revision++;
            return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = host });
        }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed partial class MultiPeerControlServer
{
    // Created and consumed only inside vault-gate callbacks. It is never sent
    // over TLS, and a request cannot create or reuse another request's lease.
    private sealed class EmergencyResearchLease(TrustedDeviceIdentity identity)
    {
        internal TrustedDeviceIdentity Identity { get; } = identity;
        private bool used;
        internal bool TryPermit(TrustedDeviceIdentity current)
        {
            if (used || current != Identity) return false;
            used = true;
            return true;
        }
    }
    private MultiPeerPreviewManager? emergencyResearchManager;
    private EmergencyResearchKeyboard? emergencyKeyboard;
    private EmergencyResearchControls? emergencyControls;
    internal EmergencyResearchKeyboard EmergencyResearchKeyboard => emergencyKeyboard ?? throw new InvalidOperationException();
    internal EmergencyResearchControls EmergencyResearchControls => emergencyControls ?? throw new InvalidOperationException();

    internal static MultiPeerControlServer CreateForEmergencyResearch(string hostName, MultiDeviceCredentialVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var peers = vault.GetTrustedDevices().Select(device => device.Name).ToArray();
        if (peers.Length == 0) throw new InvalidOperationException("A synthetic research peer is required.");
        if (peers.Any(peer => string.Equals(peer, hostName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A research peer cannot use the host name.", nameof(hostName));
        // Reuse the strict loopback-only, isolated-vault factory. It accepts no
        // real keyboard and uses no persisted application certificate.
        var server = CreateForFenceResearch(hostName, vault);
        server.emergencyKeyboard = new EmergencyResearchKeyboard(hostName, peers);
        server.emergencyResearchManager = new MultiPeerPreviewManager(hostName, peers, server.emergencyKeyboard);
        server.emergencyControls = new EmergencyResearchControls();
        return server;
    }

    private async Task<Response> DispatchEmergencyResearchAsync(Request request, CancellationToken ct)
    {
        if (request.Version != 2 || request.Action is not ("emergency" or "emergency-probe" or "emergency-bound") || request.Target != hostName ||
            researchVault is null || emergencyResearchManager is null || emergencyControls is null ||
            string.IsNullOrEmpty(request.Peer) || request.Token is not { Length: 44 })
            return new(request.Version, request.RequestId, false, "unsupported or unauthorized");
        byte[] token;
        try { token = Convert.FromBase64String(request.Token); }
        catch (FormatException) { return new(2, request.RequestId, false, "unauthorized"); }
        try
        {
            // Match the canonical credential representation required by the
            // other research protocol. Alternative Base64 padding bits must
            // not be admitted even when they decode to the same byte array.
            if (token.Length != 32 || !string.Equals(Convert.ToBase64String(token), request.Token, StringComparison.Ordinal))
                return new(2, request.RequestId, false, "unauthorized");
            EmergencyResearchLease? lease = null;
            var registration = await researchVault.TryCommitUniqueResearchAuthorizedAsync(request.Peer, token,
                identity => lease = new EmergencyResearchLease(identity), ct);
            if (registration != ResearchGrantAuthorization.Committed || lease is null)
                return new(2, request.RequestId, false, "unauthorized");
            if (request.Action != "emergency-probe")
            {
                emergencyControls.Registered.TrySetResult();
                if (emergencyControls.HoldAfterRegistration) await emergencyControls.ReleaseRegistration.Task.WaitAsync(ct);
            }

            // A second vault-gate check is the admission point. After this point
            // revocation cannot prove the manager/native action was not sent;
            // it stays unconfirmed, even if its response arrives successfully.
            var admitted = false;
            var authorization = await researchVault.TryCommitUniqueResearchAuthorizedAsync(request.Peer, token,
                identity => admitted = lease.TryPermit(identity), ct);
            if (authorization != ResearchGrantAuthorization.Committed || !admitted)
                return new(2, request.RequestId, false, "unauthorized");
            if (request.Action == "emergency-probe")
            {
                try
                {
                    var revision = await emergencyResearchManager.ObserveEmergencyRevisionAsync(lease.Identity.Name, ct);
                    return new(2, request.RequestId, true, Incarnation: researchFence!.Incarnation, KeyboardRevision: revision);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
                { return new(2, request.RequestId, false, "unavailable"); }
            }
            if (request.Action == "emergency-bound" &&
                (request.ExpectedRevision is null or < 0 || !string.Equals(request.Incarnation, researchFence!.Incarnation, StringComparison.Ordinal)))
                return new(2, request.RequestId, false, "unavailable", FenceResult: "stale-or-unavailable");
            emergencyControls.Permitted.TrySetResult();
            if (emergencyControls.HoldAfterPermit) await emergencyControls.ReleasePermit.Task.WaitAsync(ct);
            try
            {
                var outcome = request.Action == "emergency-bound"
                    ? await emergencyResearchManager.TryEmergencyReturnAtRevisionAsync(lease.Identity.Name, request.ExpectedRevision!.Value, ct)
                    : await emergencyResearchManager.TryEmergencyReturnAsync(lease.Identity.Name, ct);
                return new(2, request.RequestId, false, "not confirmed", FenceResult:
                    outcome == EmergencyReturnOutcome.AlreadyHostUnconfirmed ? "already-host-unconfirmed" : "attempted-unconfirmed");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
            { return new(2, request.RequestId, false, "not confirmed", FenceResult: request.Action == "emergency-bound" ? "stale-or-unavailable" : "unconfirmed"); }
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }
}
#endif
