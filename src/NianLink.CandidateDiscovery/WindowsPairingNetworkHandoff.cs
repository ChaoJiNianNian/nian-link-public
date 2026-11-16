using DesktopToolbox.DeskLink;

namespace NianLink.CandidateDiscovery;

/// <summary>Independent observation after an explicitly accepted request.</summary>
internal sealed class WindowsPairingNetworkHandoff : PairingNetworkHandoff
{
    private readonly DiscoveryNetworkBinding binding;
    private readonly DiscoveryReviewLease lease;
    private readonly CancellationTokenSource revoked = new();
    private readonly Task monitor;
    private int disposed;

    private WindowsPairingNetworkHandoff(DiscoveryNetworkBinding binding, DiscoveryReviewLease lease)
    { this.binding = binding; this.lease = lease; monitor = MonitorAsync(); }

    internal static async Task<PairingNetworkHandoff> CreateAsync(DiscoveryNetworkBinding binding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Start can query OS interfaces; run off the caller/UI thread.
        var lease = await Task.Run(() => DiscoveryReviewLease.Start(binding), CancellationToken.None).ConfigureAwait(false);
        try { ct.ThrowIfCancellationRequested(); return new WindowsPairingNetworkHandoff(binding, lease); }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal override DiscoveryNetworkBinding Binding => binding;
    internal override CancellationToken RevocationToken => revoked.Token;

    internal override async Task<bool> RevalidateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (revoked.IsCancellationRequested || !lease.IsFresh) return false;
        var available = await Task.Run(binding.IsHardwareAvailable, CancellationToken.None).WaitAsync(ct).ConfigureAwait(false);
        if (!available || !lease.IsFresh || revoked.IsCancellationRequested)
        { revoked.Cancel(); return false; }
        return true;
    }

    private async Task MonitorAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), revoked.Token).ConfigureAwait(false);
                if (!lease.IsFresh) { revoked.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (revoked.IsCancellationRequested) { }
        catch { revoked.Cancel(); }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        revoked.Cancel();
        try { await monitor.ConfigureAwait(false); }
        finally { try { await lease.DisposeAsync().ConfigureAwait(false); } finally { revoked.Dispose(); } }
    }
}
