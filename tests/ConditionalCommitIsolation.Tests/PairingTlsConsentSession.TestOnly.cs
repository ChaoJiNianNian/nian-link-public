using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopToolbox.DeskLink;

// Compiled only into the isolated test assembly, never the product DLL.
internal sealed partial class PairingTlsConsentSession
{
    private IsolatedCapability? isolatedCapability;
    private bool isolatedActionClaimed;
    private bool peerPrepareReserved;
    private object? peerPrepareTicket;
    private MultiPeerInvitation? peerPrepareCandidate;
    private Guid peerPrepareStage;
    private int peerPrepareSlot, peerPrepareDataPortBase;
    internal ControlledLivePublishBarrier? BeforeLiveMove { get; set; }
    internal ControlledLivePublishBarrier? AfterLiveMoveCheck { get; set; }

    [JsonConverter(typeof(RejectCapabilityJson))]
    private sealed class IsolatedCapability
    {
        public override string ToString() => "[isolated capability]";
    }

    private sealed class RejectCapabilityJson : JsonConverter<IsolatedCapability>
    {
        public override IsolatedCapability? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, IsolatedCapability value, JsonSerializerOptions options) => throw new NotSupportedException();
    }

    internal object IssueIsolatedTestCapability()
    {
        lock (gate)
        {
            RequireActive();
            if (!helloComplete || !localConfirmation || !remoteConfirmation || remoteChallenge is null ||
                consent.Phase != PairingConsentPhase.ReadyToPrepare || isolatedCapability is not null)
                throw new InvalidOperationException("Isolated capability unavailable.");
            return isolatedCapability = new IsolatedCapability();
        }
    }

    internal void BeginLiveVaultTest(object capability, PairingSessionIdentity expectedIdentity, PeerIdentityOffer expectedOffer)
    {
        lock (gate)
        {
            RequireActive();
            if (role != "host" || isolatedCapability is null || !ReferenceEquals(capability, isolatedCapability) || isolatedActionClaimed ||
                consent.Phase != PairingConsentPhase.ReadyToPrepare || identity != expectedIdentity || offer != CanonicalizeOffer(expectedOffer))
                throw new InvalidOperationException("Live test capability does not match the session.");
            isolatedActionClaimed = true; activeOperations++;
        }
    }
    internal CancellationTokenSource CreateLiveVaultDeadline(CancellationToken ct) => CreateDeadline(ct);
    internal void CheckLiveVaultTest(object capability, CancellationToken ct)
    {
        lock (gate)
        {
            RequireActive(); ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(capability, isolatedCapability) || !isolatedActionClaimed || consent.Phase != PairingConsentPhase.ReadyToPrepare)
                throw new InvalidOperationException("Live test capability is no longer valid.");
        }
    }
    internal void PublishLiveVaultTest(object capability, MultiDeviceCredentialVault vault, string temporary, string target, CancellationToken ct)
    {
        vault.ValidateLiveTestPublishPaths(temporary, target);
        lock (gate)
        {
            RequireActive(); ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(capability, isolatedCapability) || !isolatedActionClaimed || consent.Phase != PairingConsentPhase.ReadyToPrepare)
                throw new InvalidOperationException("Live test capability is no longer valid.");
            BeforeLiveMove?.PauseInsideGate();
            RequireActive(); ct.ThrowIfCancellationRequested();
            AfterLiveMoveCheck?.PauseInsideGate();
            try { File.Move(temporary, target, true); }
            catch { vault.MarkLiveTestWriteUncertain(); throw; }
        }
    }
    internal void FinishLiveVaultTest() => FinishOperation();

    internal void BeginLivePeerStageTest(object capability, PairingSessionIdentity expectedIdentity, PeerIdentityOffer expectedOffer)
    {
        lock (gate)
        {
            RequireActive();
            if (role != "peer" || isolatedCapability is null || !ReferenceEquals(capability, isolatedCapability) || isolatedActionClaimed ||
                consent.Phase != PairingConsentPhase.ReadyToPrepare || identity != expectedIdentity || offer != CanonicalizeOffer(expectedOffer))
                throw new InvalidOperationException("Live test capability does not match the peer session.");
            isolatedActionClaimed = true; activeOperations++;
        }
    }

    internal void BeginReservedLivePeerStageTest(object capability, object ticket, PairingSessionIdentity expectedIdentity, PeerIdentityOffer expectedOffer,
        Guid stage, MultiPeerInvitation candidate, int slot, int dataPortBase)
    {
        lock (gate)
        {
            RequireActive();
            if (role != "peer" || !ReferenceEquals(capability, isolatedCapability) || !isolatedActionClaimed || !peerPrepareReserved ||
                peerPrepareTicket is null || !ReferenceEquals(ticket, peerPrepareTicket) || !ReferenceEquals(candidate, peerPrepareCandidate) ||
                stage != peerPrepareStage || slot != peerPrepareSlot || dataPortBase != peerPrepareDataPortBase ||
                consent.Phase != PairingConsentPhase.ReadyToPrepare || identity != expectedIdentity || offer != CanonicalizeOffer(expectedOffer))
                throw new InvalidOperationException("Reserved peer stage does not match the session.");
            peerPrepareReserved = false; peerPrepareTicket = null; peerPrepareCandidate = null; activeOperations++;
        }
    }

    internal void PublishLivePeerStageTest(object capability, PeerInvitationStore store, string temporary, string target, CancellationToken ct)
    {
        store.ValidateLivePeerTestPublishPaths(temporary, target);
        lock (gate)
        {
            RequireActive(); ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(capability, isolatedCapability) || !isolatedActionClaimed || consent.Phase != PairingConsentPhase.ReadyToPrepare)
                throw new InvalidOperationException("Live test capability is no longer valid.");
            BeforeLiveMove?.PauseInsideGate();
            RequireActive(); ct.ThrowIfCancellationRequested();
            try { File.Move(temporary, target, false); }
            catch { store.MarkLivePeerStageUncertain(); throw; }
        }
    }

    internal async Task RunIsolatedTestActionAsync(object capability, ControlledConsentTestReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        lock (gate)
        {
            RequireActive();
            if (isolatedCapability is null || !ReferenceEquals(capability, isolatedCapability) || isolatedActionClaimed || consent.Phase != PairingConsentPhase.ReadyToPrepare)
                throw new InvalidOperationException("Isolated capability unavailable.");
            isolatedActionClaimed = true; activeOperations++;
        }
        try { await receiver.RecordAsync(); }
        finally { FinishOperation(); }
    }

    internal async Task ObserveIsolatedTestAsync(TaskCompletionSource startedReading, CancellationToken cancellationToken)
    {
        lock (gate) { RequireActive(); activeOperations++; }
        try
        {
            using var deadline = CreateDeadline(cancellationToken);
            startedReading.TrySetResult();
            using var unexpected = await ReadAsync(deadline.Token);
            throw new InvalidDataException("Unexpected frame during isolated observation.");
        }
        catch { Abort(); throw; }
        finally { FinishOperation(); }
    }
}

// A fixed, side-effect-free receiver with controllable barriers, not a callback.
internal sealed class ControlledConsentTestReceiver(bool fail = false)
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int completed;
    internal Task Entered => entered.Task;
    internal int Completed => Volatile.Read(ref completed);
    internal void Release() => released.TrySetResult();
    internal async Task RecordAsync()
    {
        entered.TrySetResult();
        await released.Task;
        if (fail) throw new InvalidOperationException("Synthetic action failure.");
        Interlocked.Increment(ref completed);
    }
}

// Fixed barriers used only by tests; never compiled into the production project.
internal sealed class ControlledLivePublishBarrier
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Entered => entered.Task;
    internal void Release() => released.TrySetResult();
    internal async Task PauseAsync() { entered.TrySetResult(); await released.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    internal void PauseInsideGate() { entered.TrySetResult(); if (!released.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
}

// No release operation: cancellation/timeout always fails the READY path.
internal sealed class NeverReleaseReadyBarrier
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Entered => entered.Task;
    internal async Task PauseUntilCancelledAsync(CancellationToken ct)
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("READY barrier cannot complete normally.");
    }
}
