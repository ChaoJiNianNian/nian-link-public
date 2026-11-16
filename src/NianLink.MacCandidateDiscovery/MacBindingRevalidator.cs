using System.Diagnostics;
using DesktopToolbox.DeskLink;
#if MAC_WIFI_TESTING
using NianLink.MacCandidateDiscovery;
namespace MacBindingTestsOnly;
#else
namespace NianLink.MacCandidateDiscovery;
#endif

// Internal lifecycle only; it cannot create or promote a live binding.
internal sealed class MacBindingRevalidator
{
    private readonly object gate = new();
    private readonly MacCandidateNetworkBinding.Snapshot baseline;
    private readonly Func<CancellationToken, Task<MacCandidateNetworkBinding.Snapshot>> sample;
    private readonly TimeSpan timeout;
    private readonly CancellationTokenSource revokedToken = new();
    private readonly TaskCompletionSource<bool> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool revoked, checking;

    private MacBindingRevalidator(MacCandidateNetworkBinding.Snapshot baseline,
        Func<CancellationToken, Task<MacCandidateNetworkBinding.Snapshot>> sample, TimeSpan timeout)
    { this.baseline = baseline; this.sample = sample; this.timeout = timeout; }

    internal static MacBindingRevalidator ForSystem(MacCandidateNetworkBinding.Snapshot baseline,
        VerifiedLanBinding approved, DiscoveryNetworkBinding shared)
        => new(baseline, async cancellation =>
        {
            if (!shared.HasLiveNetworkOrigin || !shared.IsStillAvailable()) throw new InvalidOperationException();
            var observation = await new MacReadOnlyMappingProbe().ReadCurrentPinnedAsync(approved, TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
            if (!shared.IsStillAvailable() || observation.Selected.Id != shared.InterfaceId ||
                observation.Selected.Index != shared.InterfaceIndex || observation.Selected.Address != MacCandidateNetworkBinding.ToUInt(shared.Address) ||
                observation.Selected.Mask != MacCandidateNetworkBinding.ToUInt(shared.SubnetMask)) throw new InvalidOperationException();
            return observation.Selected;
        }, TimeSpan.FromSeconds(5));

#if MAC_WIFI_TESTING
    // Test assembly only; an injected sample cannot create any binding.
    internal static MacBindingRevalidator ForTest(MacCandidateNetworkBinding.Snapshot baseline,
        Func<CancellationToken, Task<MacCandidateNetworkBinding.Snapshot>> sample, TimeSpan timeout) => new(baseline, sample, timeout);
#endif

    internal CancellationToken RevocationToken => revokedToken.Token;
    internal void Revoke()
    {
        lock (gate) { if (revoked) return; revoked = true; }
        stopped.TrySetResult(true);
        try { revokedToken.Cancel(); } catch (AggregateException) { }
    }

    internal async Task<bool> CheckAsync(CancellationToken cancellation)
    {
        bool overlap;
        lock (gate)
        {
            if (revoked) return false;
            overlap = checking;
            if (!overlap) checking = true;
        }
        if (overlap) { Revoke(); return false; }
        using var registration = cancellation.Register(Revoke);
        Task<MacCandidateNetworkBinding.Snapshot>? worker = null;
        var success = false;
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (cancellation.IsCancellationRequested || RevocationToken.IsCancellationRequested) return false;
            worker = Task.Run(() => sample(cancellation));
            if (await Task.WhenAny(worker, stopped.Task, Task.Delay(timeout)).ConfigureAwait(false) != worker)
                return false;
            var value = await worker.ConfigureAwait(false);
            lock (gate)
                if (!revoked && !cancellation.IsCancellationRequested && Stopwatch.GetElapsedTime(started) < timeout && value == baseline)
                    return success = true;
            return false;
        }
        catch (Exception) { return false; }
        finally
        {
            // A successful check stays usable; every unsuccessful one permanently revokes below.
            if (!success) Revoke();
            lock (gate) checking = false;
            if (worker is not null && !worker.IsCompleted)
                _ = worker.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
