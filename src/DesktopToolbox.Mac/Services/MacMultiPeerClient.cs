using System.IO;
using System.Security.Cryptography;
using DesktopToolbox.DeskLink;
#if MULTI_DEVICE_CANDIDATE || NIAN_PREVIEW_TEST_HOOKS
using NianLink.MultiDevice;
#endif
using DesktopToolbox.DeskLink.Collaboration;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Services;

/// <summary>Isolated multi-device Mac client; the caller owns explicit fingerprint confirmation.</summary>
public sealed class MacMultiPeerClient : IAsyncDisposable
{
#if NIAN_PREVIEW_TEST_HOOKS
    internal Action<CollaborationClient>? TestAfterCandidateDataCreated;
#endif
    private readonly string root, engine;
    private readonly MacMultiPeerProtector protector;
    private readonly PeerInvitationStore store;
    private MultiPeerClientOnboarding onboarding;
    private readonly DeskflowSessionDependencies? dependencies;
    private readonly SemaphoreSlim gate = new(1, 1);
    // This gate protects only local authorization state. Never run I/O or
    // invoke observers while holding it.
    private readonly object collaborationGate = new();
    private long collaborationGeneration;
    private bool collaborationSuspended = true;
    private bool stopRequested;
    private int stopRequests;
    internal sealed class CollaborationEpoch
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Operations;
        internal bool Closed;
        internal bool CancellationStarted;
        internal bool CancellationFinished;
    }
    private CollaborationEpoch collaborationEpoch = new();
    private MultiPeerClientLink? keyboard;
#if MULTI_DEVICE_CANDIDATE || NIAN_PREVIEW_TEST_HOOKS
    // Lives with the candidate owner, not with a preview window or a connection.
    private readonly PeerControlOwner previewOwner = new();
    // A failed Start can leave an unbound keyboard solely for later cleanup.
    private MultiPeerClientLink? guardedKeyboard;
    public bool PreviewControlUnconfirmed => previewOwner.IsUncertain;
    public bool PreviewControlUnresolved => previewOwner.HasUnresolvedOperation;

    public void BeginControlStop()
    {
        lock (collaborationGate)
            if (guardedKeyboard is { } current) previewOwner.BeginStopping(current);
    }

    private GuardedPeerControl GuardedControl(MultiPeerClientLink current, MultiPeerInvitation invitation) =>
        new(previewOwner, current, current.SwitchDetailedAsync, invitation.HostName, invitation.PeerName);
#endif
    private CollaborationClient? data;
    private DeskLinkPairingBundle? collaborationPairing;
    private bool disposed;
    public string DeviceName { get; }

    private MacMultiPeerClient(string root, string engine, string name, MacMultiPeerProtector protector,
        PeerInvitationStore store, MultiPeerInvitation? saved, DeskflowSessionDependencies? dependencies)
    {
        this.root = root; this.engine = engine; DeviceName = name; this.protector = protector;
        this.store = store; onboarding = new(store, name); this.dependencies = dependencies;
        collaborationPairing = ToCollaborationPairing(saved);
    }

    public static async Task<MacMultiPeerClient> OpenAsync(string root, string engine, IIdentityVault vault,
        DeskflowSessionDependencies? dependencies = null, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(engine)) throw new ArgumentException("Absolute candidate paths are required.");
        root = Path.GetFullPath(root); engine = Path.GetFullPath(engine);
        var name = await LocalDeviceName.OpenAsync(Path.Combine(root, "identity"), ct);
        var trust = Path.Combine(root, "trust");
        var protector = await MacMultiPeerProtector.OpenAsync(trust, vault, ct);
        PeerInvitationStore? store = null;
        try
        {
            store = await PeerInvitationStore.OpenAsync(trust, protector, ct);
            MultiPeerInvitation? saved;
#if FILELESS_PAIRING_CANDIDATE
            // Keep the owner/store available to its recovery UI, without exposing
            // any old invitation or starting a connection on uncertain evidence.
            try { saved = store.Get(); }
            catch (PairingRecoveryRequiredException) { saved = null; }
#else
            saved = store.Get();
#endif
            if (saved is not null && !string.Equals(saved.PeerName, name, StringComparison.Ordinal))
                throw new InvalidDataException("Saved pairing belongs to a different device; it was retained.");
            return new(root, engine, name, protector, store, saved, dependencies);
        }
        catch { if (store is not null) await store.DisposeAsync(); protector.Dispose(); throw; }
    }

    public async Task<PeerIdentityOffer> CreateIdentityAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (keyboard is not null) throw new InvalidOperationException("Stop the current connection first.");
            return await PeerIdentityOffer.CreateAsync(Path.Combine(root, "keyboard"), DeviceName, "mac", now, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<MultiPeerHostPreview> ReviewInvitationAsync(string json, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (keyboard is not null) throw new InvalidOperationException("Stop the current connection first.");
#if FILELESS_PAIRING_CANDIDATE
            await RequireSettledPairingInsideAsync(ct);
#endif
            return onboarding.Review(json);
        }
        finally { gate.Release(); }
    }

    public async Task ConfirmInvitationAsync(string id, string fingerprint, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (keyboard is not null) throw new InvalidOperationException("Stop the current connection first.");
#if FILELESS_PAIRING_CANDIDATE
            await RequireSettledPairingInsideAsync(ct);
#endif
            await onboarding.ConfirmAsync(id, fingerprint, ct);
            collaborationPairing = ToCollaborationPairing(store.Get());
        }
        finally { gate.Release(); }
    }

#if FILELESS_PAIRING_CANDIDATE
    private async Task RequireSettledPairingInsideAsync(CancellationToken ct)
    {
        if ((await store.GetPairingRecoveryStatusAsync(ct)).Phase != PairingRecoveryPhase.None)
            throw new InvalidOperationException("存在待处理的配对，请先查看未完成的配对。");
    }

    public async Task<bool> AcceptFilelessPairingAsync(SecurePairingPeer session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (keyboard is not null) throw new InvalidOperationException("请先停止当前连接。");
            // Invalidate any file invitation reviewed before this transaction.
            onboarding = new(store, DeviceName);
            var completed = await session.AcceptPairingOperationAsync(store, ct);
            if (completed)
                collaborationPairing = ToCollaborationPairing(store.Get() ?? throw new InvalidOperationException("配对资料暂不可用。"));
            return completed;
        }
        finally { gate.Release(); }
    }

    public async Task<PairingRecoveryStatus> GetPairingRecoveryStatusAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await store.GetPairingRecoveryStatusAsync(ct); }
        finally { gate.Release(); }
    }

    public async Task<bool> AbandonUnsentPairingAsync(PairingRecoveryStatus reviewed, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await store.AbandonUnsentPairingAsync(reviewed, ct); }
        finally { gate.Release(); }
    }
#endif

    public async Task<DeskLinkState> StartAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (keyboard is not null) throw new InvalidOperationException("Connection already started.");
            long generation;
            CancellationToken cancellation;
            lock (collaborationGate)
            {
                if (stopRequested) throw new InvalidOperationException("连接停止尚未完成，请先重新停止连接。");
                collaborationSuspended = true;
                generation = ++collaborationGeneration;
                collaborationEpoch = new();
                cancellation = collaborationEpoch.Cancellation.Token;
            }
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellation);
#if FILELESS_PAIRING_CANDIDATE
            await RequireSettledPairingInsideAsync(startup.Token);
#endif
            var invitation = store.Get() ?? throw new InvalidOperationException("Approve the host pairing first.");
            var candidate = new MultiPeerClientLink(Path.Combine(root, "keyboard"), engine, invitation, true, dependencies);
            CollaborationClient? candidateData = null;
            try
            {
                var state = await candidate.StartAsync(startup.Token);
                startup.Token.ThrowIfCancellationRequested();
                candidateData = MultiPeerCollaborationClient.Open(invitation, true);
                var candidatePairing = ToCollaborationPairing(invitation);
#if NIAN_PREVIEW_TEST_HOOKS
                TestAfterCandidateDataCreated?.Invoke(candidateData);
#endif
                lock (collaborationGate)
                {
                    if (stopRequested || generation != collaborationGeneration || startup.IsCancellationRequested)
                        throw new OperationCanceledException("连接启动时已请求停止。", startup.Token);
#if MULTI_DEVICE_CANDIDATE || NIAN_PREVIEW_TEST_HOOKS
                    previewOwner.Bind(candidate);
                    guardedKeyboard = candidate;
#endif
                    data = candidateData; keyboard = candidate;
                    collaborationPairing = candidatePairing;
                    collaborationSuspended = false;
                }
                return state;
            }
            catch (Exception startError)
            {
                var errors = new List<Exception> { startError };
                if (candidateData is not null)
                {
                    try { await candidateData.DisposeAsync(); }
                    catch (Exception error) { errors.Add(error); data = candidateData; }
                }
                try { await candidate.DisposeAsync(); }
                catch (Exception error)
                {
                    errors.Add(error); keyboard = candidate;
                }
                if (errors.Count > 1)
                {
                    // Keep failed cleanup targets owned for a later explicit Stop.
                    lock (collaborationGate) { stopRequested = true; collaborationSuspended = true; collaborationGeneration++; }
                    throw new AggregateException("连接启动失败，清理尚未完成，请重新停止连接。", errors);
                }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<DeskLinkState> GetStateAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(disposed, this); return keyboard is null ? new DeskLinkState() : await keyboard.GetStateAsync(ct); }
        finally { gate.Release(); }
    }

    public async Task<IDeskflowPreviewControl> GetPreviewControlAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var current = keyboard ?? throw new InvalidOperationException("Start the connection first.");
#if MULTI_DEVICE_CANDIDATE || NIAN_PREVIEW_TEST_HOOKS
            var invitation = store.Get() ?? throw new InvalidOperationException("Approve pairing first.");
            return GuardedControl(current, invitation);
#else
            return current;
#endif
        }
        finally { gate.Release(); }
    }

    public async Task<DeskLinkPairingBundle?> GetApprovedCollaborationPairingAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(disposed, this); return keyboard is null ? null : collaborationPairing; }
        finally { gate.Release(); }
    }

    public sealed class CollaborationLease
    {
        private readonly MacMultiPeerClient owner;
        private readonly long generation;
        private readonly CollaborationEpoch epoch;
        internal CollaborationLease(MacMultiPeerClient owner, long generation, DeskLinkPairingBundle pairing, CollaborationEpoch epoch)
        { this.owner = owner; this.generation = generation; Pairing = pairing; this.epoch = epoch; }
        internal DeskLinkPairingBundle Pairing { get; }
        internal CancellationToken Cancellation => epoch.Cancellation.Token;
        public bool IsActive { get { lock (owner.collaborationGate) return owner.IsLeaseActive(generation); } }
        internal bool TryCommit(Action commit)
        {
            lock (owner.collaborationGate)
            {
                if (!owner.IsLeaseActive(generation)) return false;
                commit();
                return true;
            }
        }
        internal bool TryEnter(out CollaborationOperation? operation)
        {
            lock (owner.collaborationGate)
            {
                if (!owner.IsLeaseActive(generation) || epoch.Closed) { operation = null; return false; }
                epoch.Operations++;
                operation = new(owner, epoch);
                return true;
            }
        }
        internal Task Suspend() => owner.SuspendCollaboration(generation, epoch);
    }

    public sealed class CollaborationOperation : IDisposable
    {
        private MacMultiPeerClient? owner;
        private readonly CollaborationEpoch epoch;
        internal CollaborationOperation(MacMultiPeerClient owner, CollaborationEpoch epoch) { this.owner = owner; this.epoch = epoch; }
        public CancellationToken Token => epoch.Cancellation.Token;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null) return;
            lock (current.collaborationGate)
            {
                epoch.Operations--;
                if (epoch.Closed && epoch.CancellationFinished && epoch.Operations == 0) epoch.Drained.TrySetResult();
            }
        }
    }

    private bool IsLeaseActive(long generation) => !stopRequested && !collaborationSuspended && collaborationGeneration == generation;

    public async Task<CollaborationLease?> GetCollaborationLeaseAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (collaborationGate)
                return keyboard is null || collaborationPairing is null || stopRequested || collaborationSuspended
                    ? null : new(this, collaborationGeneration, collaborationPairing, collaborationEpoch);
        }
        finally { gate.Release(); }
    }

    private Task SuspendCollaboration(long generation, CollaborationEpoch epoch)
    {
        var cancel = false;
        lock (collaborationGate)
        {
            if (IsLeaseActive(generation))
            {
                collaborationSuspended = true;
                collaborationGeneration++;
                cancel = CloseEpoch(epoch);
            }
        }
        // Cancellation callbacks may run arbitrary code; never run them under the gate.
        if (cancel) CancelEpoch(epoch);
        return epoch.Drained.Task;
    }

    private static bool CloseEpoch(CollaborationEpoch epoch)
    {
        epoch.Closed = true;
        if (epoch.CancellationStarted) return false;
        epoch.CancellationStarted = true;
        return true;
    }

    private void CancelEpoch(CollaborationEpoch epoch)
    {
        try { epoch.Cancellation.Cancel(); } catch (AggregateException) { }
        finally
        {
            lock (collaborationGate)
            {
                epoch.CancellationFinished = true;
                if (epoch.Operations == 0) epoch.Drained.TrySetResult();
            }
        }
    }

    private Task RequestStop()
    {
        CollaborationEpoch epoch;
        bool cancel;
        lock (collaborationGate)
        {
#if MULTI_DEVICE_CANDIDATE || NIAN_PREVIEW_TEST_HOOKS
            if (guardedKeyboard is { } current) previewOwner.BeginStopping(current);
#endif
            stopRequested = true;
            stopRequests++;
            collaborationSuspended = true;
            collaborationGeneration++;
            epoch = collaborationEpoch;
            cancel = CloseEpoch(epoch);
        }
        if (cancel) CancelEpoch(epoch);
        return epoch.Drained.Task;
    }

    public async Task<string> GetCollaborationScopeAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (keyboard is null || store.Get() is not { } invitation) throw new InvalidOperationException("Start the approved connection first.");
            var token = Convert.FromBase64String(invitation.Token);
            try { return Convert.ToHexString(SHA256.HashData(token)); }
            finally { CryptographicOperations.ZeroMemory(token); }
        }
        finally { gate.Release(); }
    }

    private static DeskLinkPairingBundle? ToCollaborationPairing(MultiPeerInvitation? invitation)
    {
        if (invitation is null) return null;
        return new DeskLinkPairingBundle { Host = invitation.Host, Port = invitation.Port, DataPort = invitation.DataPort,
            WindowsName = invitation.HostName, MacName = invitation.PeerName,
            CertificateSha256 = invitation.CertificateSha256, Token = invitation.Token };
    }

    public async Task ConfirmKeyboardAsync(string fingerprint, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(disposed, this); await (keyboard ?? throw new InvalidOperationException("Start the connection first.")).ConfirmKeyboardPeerAsync(fingerprint, ct); }
        finally { gate.Release(); }
    }

    public async Task<bool> SwitchAsync(bool toThisMac, CancellationToken ct = default) => (await SwitchDetailedAsync(toThisMac, ct)).InputTransferred;

    public async Task<DeskLinkSwitchResult> SwitchDetailedAsync(bool toThisMac, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var invitation = store.Get() ?? throw new InvalidOperationException("Approve pairing first.");
            var current = keyboard ?? throw new InvalidOperationException("Start the connection first.");
#if MULTI_DEVICE_CANDIDATE || NIAN_PREVIEW_TEST_HOOKS
            return await GuardedControl(current, invitation).SwitchDetailedAsync(toThisMac ? invitation.PeerName : invitation.HostName, ct);
#else
            return await current.SwitchDetailedAsync(toThisMac ? invitation.PeerName : invitation.HostName, ct);
#endif
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var drained = RequestStop();
        var stopped = false;
        try
        {
            await drained.WaitAsync(ct);
            await gate.WaitAsync(ct);
            try { await StopInsideAsync(); stopped = true; }
            finally { gate.Release(); }
        }
        finally
        {
            lock (collaborationGate)
            {
                stopRequests--;
                // A cancelled/failed stop remains fail-closed until a later
                // successful explicit stop completes cleanup.
                if (stopped && stopRequests == 0) stopRequested = false;
            }
        }
    }

    private async Task StopInsideAsync()
    {
        if (keyboard is not null)
        {
#if MULTI_DEVICE_CANDIDATE || NIAN_PREVIEW_TEST_HOOKS
            lock (collaborationGate)
            {
                if (ReferenceEquals(guardedKeyboard, keyboard))
                { previewOwner.Retire(keyboard); guardedKeyboard = null; }
            }
#endif
            await keyboard.StopAsync(CancellationToken.None); await keyboard.DisposeAsync();
            lock (collaborationGate) keyboard = null;
        }
        if (data is not null) { await data.DisposeAsync(); data = null; }
    }

    public async ValueTask DisposeAsync()
    {
        var drained = RequestStop();
        await drained;
        await gate.WaitAsync();
        try { if (!disposed) { await StopInsideAsync(); await store.DisposeAsync(); protector.Dispose(); disposed = true; } }
        finally { gate.Release(); }
    }
}
