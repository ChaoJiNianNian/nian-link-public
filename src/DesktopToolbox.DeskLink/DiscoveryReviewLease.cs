namespace DesktopToolbox.DeskLink;

/// <summary>Short-lived observation of one locally approved network; it does not authenticate a peer.</summary>
public sealed class DiscoveryReviewLease : IAsyncDisposable
{
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(2);
    private readonly object gate = new();
    private readonly DiscoveryNetworkBinding binding;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource stopped = new();
    private Task monitor = Task.CompletedTask;
    private long verifiedAt;
    private bool revoked, claimed, completed;
    private int disposed;

    private DiscoveryReviewLease(DiscoveryNetworkBinding binding, TimeProvider clock, long observedAt)
    { this.binding = binding; this.clock = clock; verifiedAt = observedAt; }

    public static DiscoveryReviewLease Start(DiscoveryNetworkBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.HasLiveNetworkOrigin)
            throw new InvalidOperationException("Approved hardware network is unavailable.");
        var clock = TimeProvider.System;
        var started = clock.GetTimestamp();
        var lease = CreateAfterInitialObservation(binding, clock, started, binding.IsHardwareAvailable());
        lease.monitor = Task.Run(lease.MonitorAsync);
        return lease;
    }

    internal bool HasLiveNetworkOrigin => binding.HasLiveNetworkOrigin;
    internal DiscoveryNetworkBinding Binding => binding;
    public bool IsBoundTo(DiscoveryNetworkBinding other) => ReferenceEquals(binding, other);
    public bool IsFresh { get { lock (gate) return FreshLocked(); } }

    /// <summary>Returns false if publication was already claimed or this lease was already revoked.</summary>
    public bool TryRevoke()
    {
        lock (gate)
        {
            if (revoked || claimed || completed) return false;
            revoked = true; return true;
        }
    }

    // Returns false once publishing has started; callers must wait for that confirmation.
    internal bool RevokeUnlessPublicationClaimed()
    {
        lock (gate)
        {
            if (claimed || completed) return false;
            revoked = true;
            return true;
        }
    }

    internal void EnsureFresh()
    {
        lock (gate) if (!FreshLocked()) throw new InvalidOperationException("Discovery network observation is no longer valid.");
    }
    internal void ClaimPublish()
    {
        lock (gate)
        {
            if (!FreshLocked()) throw new InvalidOperationException("Discovery network observation is no longer valid.");
            claimed = true;
        }
    }
    internal void FinishPublish() { lock (gate) { completed = true; } }

    private bool FreshLocked()
    {
        if (revoked || claimed || completed) return false;
        var elapsed = clock.GetElapsedTime(verifiedAt, clock.GetTimestamp());
        if (elapsed < TimeSpan.Zero || elapsed >= Freshness) { revoked = true; return false; }
        return true;
    }
    private static DiscoveryReviewLease CreateAfterInitialObservation(DiscoveryNetworkBinding binding, TimeProvider clock, long started, bool available)
    {
        if (!available || !SampleIsTimely(clock, started))
            throw new InvalidOperationException("Approved hardware network is unavailable.");
        return new DiscoveryReviewLease(binding, clock, started);
    }
    private static bool SampleIsTimely(TimeProvider clock, long started)
    {
        var duration = clock.GetElapsedTime(started, clock.GetTimestamp());
        return duration >= TimeSpan.Zero && duration < Freshness;
    }
    private void Record(bool available, long started)
    {
        lock (gate)
        {
            if (revoked || claimed || completed) return;
            var now = clock.GetTimestamp();
            var elapsed = clock.GetElapsedTime(verifiedAt, now);
            var sampleDuration = clock.GetElapsedTime(started, now);
            var order = clock.GetElapsedTime(verifiedAt, started);
            if (!available || order < TimeSpan.Zero || sampleDuration < TimeSpan.Zero || sampleDuration >= Freshness ||
                elapsed < TimeSpan.Zero || elapsed >= Freshness) { revoked = true; return; }
            verifiedAt = started;
        }
    }
    private async Task MonitorAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), stopped.Token).ConfigureAwait(false);
                var started = clock.GetTimestamp();
                bool available;
                try { available = binding.IsHardwareAvailable(); }
                catch (Exception) { available = false; }
                Record(available, started);
                if (!IsFresh) return;
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        finally { TryRevoke(); }
    }
    /// <summary>Stops observation and tries to revoke; a claimed publication must be awaited through its confirmation task.</summary>
    public async ValueTask DisposeAsync()
    {
        TryRevoke();
        var first = Interlocked.Exchange(ref disposed, 1) == 0;
        if (first) stopped.Cancel();
        await monitor.ConfigureAwait(false);
        if (first) stopped.Dispose();
    }

#if ISOLATED_SOCKET_CONFIGURATION_TEST
    internal static DiscoveryReviewLease ForTest(DiscoveryNetworkBinding binding, TimeProvider clock) => new(binding, clock, clock.GetTimestamp());
    internal static DiscoveryReviewLease StartForTest(DiscoveryNetworkBinding binding, TimeProvider clock, Func<bool> query)
    {
        var started = clock.GetTimestamp();
        return CreateAfterInitialObservation(binding, clock, started, query());
    }
    internal void RecordForTest(bool available) => Record(available, clock.GetTimestamp());
    internal void RecordForTest(bool available, long started) => Record(available, started);
    internal void ClaimForTest() => ClaimPublish();
#endif
}
