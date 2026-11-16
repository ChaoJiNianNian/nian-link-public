using DesktopToolbox.DeskLink;
using NianLink.MacCandidateDiscovery;

namespace NianLink.MacResponderCandidate;

/// <summary>New native network observation independent of the discovery socket lifetime.</summary>
internal sealed class MacPairingNetworkHandoff : PairingNetworkHandoff
{
    private readonly MacCandidateNetworkBinding network;
    private readonly DiscoveryNetworkBinding binding;
    private readonly CancellationTokenSource revoked;
    private readonly NetworkCheckSequencer checks = new();
    private readonly Task monitor;
    private int disposed;

    private MacPairingNetworkHandoff(MacCandidateNetworkBinding network)
    {
        this.network = network; binding = network.SharedBinding;
        revoked = CancellationTokenSource.CreateLinkedTokenSource(network.RevocationToken);
        monitor = MonitorAsync();
    }

    internal static async Task<PairingNetworkHandoff> CreateAsync(VerifiedLanBinding approved,
        DiscoveryNetworkBinding original, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!original.IsStillAvailable()) throw new InvalidOperationException("Original network is unavailable.");
        var network = await MacCandidateNetworkBinding.CreateForCurrentNetworkAsync(approved, ct).ConfigureAwait(false);
        try
        {
            var next = network.SharedBinding;
            if (!next.HasLiveNetworkOrigin || !original.HasLiveNetworkOrigin || next.InterfaceId != original.InterfaceId ||
                next.InterfaceIndex != original.InterfaceIndex || !next.Address.Equals(original.Address) || !next.SubnetMask.Equals(original.SubnetMask) ||
                !original.IsStillAvailable()) throw new InvalidOperationException("Approved network changed during pairing handoff.");
            ct.ThrowIfCancellationRequested();
            return new MacPairingNetworkHandoff(network);
        }
        catch { network.Dispose(); throw; }
    }

    internal override DiscoveryNetworkBinding Binding => binding;
    internal override CancellationToken RevocationToken => revoked.Token;

    internal override async Task<bool> RevalidateAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, revoked.Token);
        return await checks.RunAsync(async cancellation =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (revoked.IsCancellationRequested) return false;
            var available = await network.IsStillAvailableAsync(cancellation).WaitAsync(cancellation).ConfigureAwait(false);
            var single = available && await Task.Run(() => SingleIPv4Policy.ReadAndMatch(network.InterfaceId, network.InterfaceIndex, network.Address),
                CancellationToken.None).WaitAsync(cancellation).ConfigureAwait(false);
            if (!available || !single || revoked.IsCancellationRequested) { revoked.Cancel(); return false; }
            return true;
        }, linked.Token).ConfigureAwait(false);
    }

    private async Task MonitorAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), revoked.Token).ConfigureAwait(false);
                if (!await RevalidateAsync(revoked.Token).ConfigureAwait(false)) return;
            }
        }
        catch (OperationCanceledException) when (revoked.IsCancellationRequested) { }
        catch { revoked.Cancel(); }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        revoked.Cancel();
        try { await monitor.ConfigureAwait(false); await checks.StopAndDrainAsync().ConfigureAwait(false); }
        finally { network.Dispose(); revoked.Dispose(); }
    }
}
