using System.Net;
using DesktopToolbox.DeskLink;

namespace NianLink.CandidateDiscovery;

/// <summary>Discovery startup failed and at least one owned resource could not be confirmed closed.</summary>
public sealed class DiscoveryStartupCleanupException : InvalidOperationException
{
    public DiscoveryStartupCleanupException(Exception startup, Exception cleanup)
        : base("Discovery startup cleanup failed.", new AggregateException(startup, cleanup)) { }
}

internal static class DiscoveryStartupCleanup
{
    internal static Exception? TryDispose(params IDisposable?[] resources)
    {
        Exception? first = null;
        foreach (var resource in resources)
            try { resource?.Dispose(); } catch (Exception error) { first ??= error; }
        return first;
    }
}

internal sealed record CandidateDiscoveryDatagram(byte[] Data, IPEndPoint Source, IPAddress Destination, int InterfaceIndex);

internal interface ICandidateDiscoveryTransport : IDisposable
{
    Task SendQueryAsync(DiscoveryScanSession scan, CancellationToken ct);
    // Implementations prepare/validate outside the gate; only the final send is started inside it.
#if ISOLATED_SOCKET_CONFIGURATION_TEST
    // Existing synthetic transports may use the basic implementation; new
    // lifecycle tests override this to model blocking preparation explicitly.
    Task SendQueryGuardedAsync(DiscoveryScanSession scan, CancellationToken ct, Func<Func<Task>, Task> start) =>
        start(() => SendQueryAsync(scan, ct));
#else
    Task SendQueryGuardedAsync(DiscoveryScanSession scan, CancellationToken ct, Func<Func<Task>, Task> start);
#endif
    Task ReplyToReceivedQueryAsync(DiscoveryResponderSession responder, CandidateDiscoveryDatagram packet, CancellationToken ct);
    Task<CandidateDiscoveryDatagram> ReceiveAsync(CancellationToken ct);
}

/// <summary>Untrusted identity selected from one scan; the caller owns the network observation.</summary>
public sealed record CandidateDiscoveryChoice(Guid Id, string Name, string Platform);

#if FILELESS_PAIRING_DIAGNOSTICS
/// <summary>Local, unauthenticated aggregate observations, not proof of identity or packet delivery.</summary>
public readonly record struct PairingDiagnosticSnapshot(int Observed, int Rejected, int Accepted);
#endif

public sealed class CandidateReviewSelection
{
    private readonly byte[] sourceAddress;
    private int securePairingClaimed;
    internal CandidateReviewSelection(Guid generation, PeerIdentityOffer offer, IPAddress source, DateTimeOffset expiresAtUtc, DiscoveryReviewLease lease)
    {
        Generation = generation; Offer = offer; sourceAddress = source.GetAddressBytes();
        ExpiresAtUtc = expiresAtUtc; Lease = lease;
    }
    public Guid Generation { get; }
    public PeerIdentityOffer Offer { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public DiscoveryReviewLease Lease { get; }
    // The address is an untrusted route to the selected announcement, not proof of identity.
    internal IPAddress SourceAddress => new(sourceAddress);
    internal bool TryClaimForSecurePairing() => Interlocked.CompareExchange(ref securePairingClaimed, 1, 0) == 0;
}

/// <summary>A single explicit discovery session; candidate results are unverified.</summary>
public sealed class CandidateDiscoveryRun : IAsyncDisposable
{
    private static readonly TimeSpan ScanQueryInterval = TimeSpan.FromSeconds(2);
    private readonly DiscoveryScanSession? scan;
    private readonly DiscoveryResponderSession? responder;
    private readonly ICandidateDiscoveryTransport transport;
    private readonly Func<bool> active;
    private readonly DiscoveryNetworkBinding? handoffBinding;
    private readonly PairingNoticeInbox? pairingInbox;
    private readonly object closeGate = new();
#if FILELESS_PAIRING_DIAGNOSTICS
    private readonly object pairingDiagnosticGate = new();
    private int diagnosticObserved, diagnosticRejected, diagnosticAccepted;
    public PairingDiagnosticSnapshot GetPairingDiagnostics()
    {
        lock (pairingDiagnosticGate) return new(diagnosticObserved, diagnosticRejected, diagnosticAccepted);
    }
    private void RecordPairingDiagnostic(bool accepted)
    {
        lock (pairingDiagnosticGate)
        {
            if (diagnosticObserved < 999) diagnosticObserved++;
            if (accepted) { if (diagnosticAccepted < 999) diagnosticAccepted++; }
            else if (diagnosticRejected < 999) diagnosticRejected++;
        }
    }
#endif
    private readonly Dictionary<Guid, DiscoveryCandidate> choiceIds = new();
    private readonly long startedAt = TimeProvider.System.GetTimestamp();
    private readonly TimeSpan duration;
    private bool handoffCancelled, networkFailed;
    public Guid Generation { get; } = Guid.NewGuid();
#if ISOLATED_SOCKET_CONFIGURATION_TEST
    private Func<DiscoveryReviewLease>? testLeaseFactory;
    private Func<DateTimeOffset>? testClock;
#endif
    private readonly CancellationTokenSource stopped;
    private readonly TaskCompletionSource stopFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int closed;
    private Exception? cleanupError;
    public Task Completion { get; }
    public Exception? Error { get; private set; }

    private CandidateDiscoveryRun(DiscoveryScanSession? scan, DiscoveryResponderSession? responder,
        ICandidateDiscoveryTransport transport, Func<bool> active, TimeSpan duration, DiscoveryNetworkBinding? handoffBinding = null, CancellationToken cancellation = default,
        DiscoveryNetworkBinding? responderBinding = null, PeerIdentityOffer? responderOffer = null
#if ISOLATED_SOCKET_CONFIGURATION_TEST
        , bool disableTestDeadline = false
#endif
        )
    {
        cancellation.ThrowIfCancellationRequested();
        this.scan = scan; this.responder = responder; this.transport = transport; this.active = active;
        if (handoffBinding is not null && (scan is null || !scan.IsBoundTo(handoffBinding)))
            throw new InvalidOperationException("Scan and handoff binding differ.");
        this.handoffBinding = handoffBinding; this.duration = duration;
        if ((scan is null) == (responder is null) || duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30))
            throw new ArgumentException("Invalid candidate discovery lifetime.");
        stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (responderBinding is not null || responderOffer is not null)
        {
            if (responder is null || responderBinding is null || responderOffer is null || !responder.IsBoundTo(responderBinding))
            { stopped.Dispose(); throw new InvalidOperationException("Pairing responder binding is invalid."); }
            try { pairingInbox = new PairingNoticeInbox(responderBinding, responderOffer, duration, stopped.Token,
                acquireNetwork: token => WindowsPairingNetworkHandoff.CreateAsync(responderBinding, token)); }
            catch { stopped.Dispose(); throw; }
        }
#if ISOLATED_SOCKET_CONFIGURATION_TEST
        if (!disableTestDeadline)
#endif
            stopped.CancelAfter(duration);
        Completion = Task.Run(RunAsync);
    }

    public static CandidateDiscoveryRun StartScan(VerifiedLanBinding network, TimeSpan duration, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var sessions = CandidateDiscoverySessions.Open(network);
        var binding = sessions.Binding;
        if (!binding.IsHardwareAvailable()) throw new InvalidOperationException("Approved hardware interface is unavailable.");
        var scan = sessions.StartScan(duration);
        WindowsDiscoveryDatagramSocket? socket = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            socket = WindowsDiscoveryDatagramSocket.Open(binding, responder: false);
            cancellation.ThrowIfCancellationRequested();
            return new(scan, null, socket, binding.IsHardwareAvailable, duration, binding, cancellation);
        }
        catch (Exception error)
        {
            var cleanup = DiscoveryStartupCleanup.TryDispose(socket, scan);
            if (cleanup is not null) throw new DiscoveryStartupCleanupException(error, cleanup);
            throw;
        }
    }

    public static CandidateDiscoveryRun StartResponder(VerifiedLanBinding network, PeerIdentityOffer offer, TimeSpan duration, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30)) throw new ArgumentException("Invalid candidate discovery lifetime.");
        var sessions = CandidateDiscoverySessions.Open(network);
        var binding = sessions.Binding;
        if (!binding.IsHardwareAvailable()) throw new InvalidOperationException("Approved hardware interface is unavailable.");
        var responder = sessions.StartResponder(offer);
        WindowsDiscoveryDatagramSocket? socket = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            socket = WindowsDiscoveryDatagramSocket.Open(binding, responder: true);
            cancellation.ThrowIfCancellationRequested();
            return new(null, responder, socket, binding.IsHardwareAvailable, duration, cancellation: cancellation,
                responderBinding: binding, responderOffer: offer);
        }
        catch (Exception error)
        {
            var cleanup = DiscoveryStartupCleanup.TryDispose(socket, responder);
            if (cleanup is not null) throw new DiscoveryStartupCleanupException(error, cleanup);
            throw;
        }
    }

#if ISOLATED_SOCKET_CONFIGURATION_TEST
    internal static CandidateDiscoveryRun ForScanTest(DiscoveryScanSession scan, ICandidateDiscoveryTransport transport,
        Func<bool> active, TimeSpan duration, bool disableTestDeadline = false) => new(scan, null, transport, active, duration, disableTestDeadline: disableTestDeadline);
    internal static CandidateDiscoveryRun ForScanWithCancellationTest(DiscoveryScanSession scan, ICandidateDiscoveryTransport transport, CancellationToken cancellation) =>
        new(scan, null, transport, () => true, TimeSpan.FromSeconds(10), cancellation: cancellation);
    internal static CandidateDiscoveryRun ForScanHandoffTest(DiscoveryScanSession scan, DiscoveryNetworkBinding binding,
        ICandidateDiscoveryTransport transport, Func<DiscoveryReviewLease> leaseFactory, Func<DateTimeOffset> clock, TimeSpan? runDuration = null, Func<bool>? testActive = null)
    {
        var run = new CandidateDiscoveryRun(scan, null, transport, testActive ?? (() => true), runDuration ?? TimeSpan.FromSeconds(10), binding);
        run.testLeaseFactory = leaseFactory; run.testClock = clock; return run;
    }
    internal static CandidateDiscoveryRun ForResponderTest(DiscoveryResponderSession responder, ICandidateDiscoveryTransport transport,
        Func<bool> active, TimeSpan duration) => new(null, responder, transport, active, duration);
    internal static CandidateDiscoveryRun ForResponderWithPairingTest(DiscoveryResponderSession responder, DiscoveryNetworkBinding binding,
        PeerIdentityOffer offer, ICandidateDiscoveryTransport transport, Func<bool> active, TimeSpan duration)
        => new(null, responder, transport, active, duration, responderBinding: binding, responderOffer: offer);
#endif

    /// <summary>An unverified request from the current responder run, or null.</summary>
    public PairingIncomingRequest? GetPendingPairingRequest() => Volatile.Read(ref closed) == 0 ? pairingInbox?.GetPending() : null;
    public bool RejectPairingRequest(Guid id) => Volatile.Read(ref closed) == 0 && pairingInbox?.Reject(id) == true;

    public IReadOnlyList<DiscoveryCandidate> GetCandidates() => Volatile.Read(ref closed) == 0 && scan is not null ? scan.GetCandidates() : [];

    private DateTimeOffset HandoffNow()
    {
#if ISOLATED_SOCKET_CONFIGURATION_TEST
        if (testClock is not null) return testClock();
#endif
        return DateTimeOffset.UtcNow;
    }
    private DiscoveryReviewLease StartHandoffLease()
    {
#if ISOLATED_SOCKET_CONFIGURATION_TEST
        if (testLeaseFactory is not null) return testLeaseFactory();
#endif
        return DiscoveryReviewLease.Start(handoffBinding!);
    }

    private bool WithinDeadline() => TimeProvider.System.GetElapsedTime(startedAt) is var elapsed &&
        elapsed >= TimeSpan.Zero && elapsed < duration;

    private static bool SameCandidate(DiscoveryCandidate left, DiscoveryCandidate right) =>
        left.Offer == right.Offer && left.ExpiresAtUtc == right.ExpiresAtUtc && left.Source.Equals(right.Source);

    private bool CheckActive()
    {
        var available = active();
        if (!available) lock (closeGate) networkFailed = true;
        return available;
    }

    /// <summary>Untrusted display choices scoped to this running scan; IDs are not device identities.</summary>
    public IReadOnlyList<CandidateDiscoveryChoice> GetCandidateChoices()
    {
        if (scan is null || handoffBinding is null || Volatile.Read(ref closed) != 0) return [];
        var snapshot = scan.GetCandidates();
        lock (closeGate)
        {
            if (closed != 0 || networkFailed || stopped.IsCancellationRequested || !WithinDeadline()) return [];
            foreach (var id in choiceIds.Where(x => !snapshot.Any(candidate => SameCandidate(x.Value, candidate))).Select(x => x.Key).ToArray())
                choiceIds.Remove(id);
            var result = new List<CandidateDiscoveryChoice>();
            foreach (var candidate in snapshot)
            {
                var found = choiceIds.FirstOrDefault(x => SameCandidate(x.Value, candidate));
                var id = found.Key;
                if (id == Guid.Empty) { id = Guid.NewGuid(); choiceIds.Add(id, candidate with { Source = new IPAddress(candidate.Source.GetAddressBytes()) }); }
                result.Add(new(id, candidate.Offer.Name, candidate.Offer.Platform));
            }
            return result;
        }
    }

    /// <summary>Claim one current untrusted candidate, stop and drain its scan, and transfer ownership of its network lease.</summary>
    public async Task<CandidateReviewSelection> SelectCandidateAsync(Guid generation, Guid choiceId, CancellationToken ct = default)
    {
        DiscoveryCandidate snapshot;
        lock (closeGate)
        {
            if (generation != Generation || scan is null || handoffBinding is null || closed != 0 ||
                networkFailed || stopped.IsCancellationRequested || !WithinDeadline() || ct.IsCancellationRequested || !choiceIds.TryGetValue(choiceId, out snapshot!))
                throw new InvalidOperationException("Discovery candidate is no longer available.");
        }
        var candidate = scan.GetCandidates().SingleOrDefault(x => SameCandidate(x, snapshot));
        var remaining = scan.Remaining();
        var selectedAt = HandoffNow();
        var stamp = TimeProvider.System.GetTimestamp();
        lock (closeGate)
        {
            if (closed != 0 || networkFailed || stopped.IsCancellationRequested || !WithinDeadline() || ct.IsCancellationRequested || Error is not null ||
                candidate is null || candidate.ExpiresAtUtc <= selectedAt || candidate.Offer.ExpiresAtUtc <= selectedAt || remaining <= TimeSpan.Zero)
                throw new InvalidOperationException("Discovery candidate is no longer available.");
            Volatile.Write(ref closed, 1);
            choiceIds.Clear();
        }
        StopCore();
        DiscoveryReviewLease? lease = null;
        try
        {
            try { lease = await Task.Run(StartHandoffLease).ConfigureAwait(false); }
            finally { await Completion.ConfigureAwait(false); }
            var elapsed = TimeProvider.System.GetElapsedTime(stamp);
            var now = HandoffNow();
            lock (closeGate)
            {
                if (handoffCancelled || networkFailed || ct.IsCancellationRequested || cleanupError is not null || Error is not null ||
                    !WithinDeadline() || now < selectedAt || now >= candidate.ExpiresAtUtc || now >= candidate.Offer.ExpiresAtUtc ||
                    elapsed < TimeSpan.Zero || elapsed >= remaining || !lease.IsBoundTo(handoffBinding) || !lease.IsFresh)
                    throw new InvalidOperationException("Discovery selection expired or cleanup failed.", cleanupError ?? Error);
                var result = new CandidateReviewSelection(Generation, candidate.Offer, candidate.Source, candidate.ExpiresAtUtc, lease);
                lease = null;
                return result;
            }
        }
        finally { if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); }
    }

    private async Task RunAsync()
    {
        try
        {
            stopped.Token.ThrowIfCancellationRequested();
            if (!CheckActive()) return;
            stopped.Token.ThrowIfCancellationRequested();
            long lastQuery = 0;
            if (scan is not null)
            {
                if (!WithinDeadline() || scan.Remaining() <= TimeSpan.Zero || !await SendScanQueryAsync()) return;
                lastQuery = TimeProvider.System.GetTimestamp();
            }
            while (!stopped.IsCancellationRequested)
            {
                if (!CheckActive()) break;
                if (scan is not null)
                {
                    if (!WithinDeadline() || scan.Remaining() <= TimeSpan.Zero) break;
                    if (TimeProvider.System.GetElapsedTime(lastQuery) >= ScanQueryInterval)
                    {
                        if (!await SendScanQueryAsync()) break;
                        lastQuery = TimeProvider.System.GetTimestamp();
                    }
                }
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                tick.CancelAfter(TimeSpan.FromSeconds(1));
                CandidateDiscoveryDatagram packet;
                try { packet = await transport.ReceiveAsync(tick.Token); }
                catch (OperationCanceledException) when (!stopped.IsCancellationRequested) { continue; }
                if (!CheckActive() || stopped.IsCancellationRequested) break;
                var source = new IPEndPoint(new IPAddress(packet.Source.Address.GetAddressBytes()), packet.Source.Port);
                if (scan is not null) scan.Observe(packet.Data, source, packet.Destination, packet.InterfaceIndex);
                else
                {
                    if (pairingInbox is not null && !DiscoveryProtocol.MulticastGroup.Equals(packet.Destination))
                    {
#if FILELESS_PAIRING_DIAGNOSTICS
                        var accepted = pairingInbox.Observe(packet.Data, source, packet.Destination, packet.InterfaceIndex);
                        RecordPairingDiagnostic(accepted);
#else
                        pairingInbox.Observe(packet.Data, source, packet.Destination, packet.InterfaceIndex);
#endif
                    }
                    else if (CheckActive() && !stopped.IsCancellationRequested)
                        await transport.ReplyToReceivedQueryAsync(responder!, packet, stopped.Token);
                }
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (Volatile.Read(ref closed) != 0) { }
        catch (Exception error) { Error = error; }
        finally { Stop(); await stopFinished.Task.ConfigureAwait(false); stopped.Dispose(); }
    }

    // Hardware enumeration, query preparation and socket completion waits must
    // not hold the close gate. Only starting the prepared send is gated.
    // DisposeAsync/selection still drain this task before reporting completion.
    private async Task<bool> SendScanQueryAsync()
    {
        if (!WithinDeadline() || scan!.Remaining() <= TimeSpan.Zero || !CheckActive()) return false;
        try
        {
            stopped.Token.ThrowIfCancellationRequested();
            await transport.SendQueryGuardedAsync(scan, stopped.Token, StartGuardedQueryAsync).ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref closed) != 0) { return false; }
        catch (InvalidOperationException) when (Volatile.Read(ref closed) != 0) { return false; }
    }

    private Task StartGuardedQueryAsync(Func<Task> send)
    {
        lock (closeGate)
        {
            if (closed != 0 || stopped.IsCancellationRequested || !WithinDeadline()) return Task.CompletedTask;
            return send();
        }
    }

    private void Stop()
    {
        lock (closeGate)
        {
            if (closed != 0) return;
            Volatile.Write(ref closed, 1);
        }
        StopCore();
    }
    private void StopCore()
    {
        try
        {
            try { scan?.Dispose(); } catch (Exception error) { RecordCleanupFailure(error); }
            try { responder?.Dispose(); } catch (Exception error) { RecordCleanupFailure(error); }
            try { stopped.Cancel(); } catch (Exception error) { RecordCleanupFailure(error); }
            try { pairingInbox?.Dispose(); } catch (Exception error) { RecordCleanupFailure(error); }
            try { transport.Dispose(); } catch (Exception error) { RecordCleanupFailure(error); }
        }
        finally { stopFinished.TrySetResult(); }
    }
    private void RecordCleanupFailure(Exception error) { cleanupError ??= error; Error ??= error; }
    public async ValueTask DisposeAsync()
    {
        lock (closeGate) handoffCancelled = true;
        Stop(); await Completion.ConfigureAwait(false);
        if (cleanupError is not null) throw new InvalidOperationException("Discovery cleanup did not complete normally.", cleanupError);
    }
}
