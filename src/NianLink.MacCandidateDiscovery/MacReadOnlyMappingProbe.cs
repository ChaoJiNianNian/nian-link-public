using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using DesktopToolbox.DeskLink;
#if MAC_WIFI_TESTING
using NianLink.MacCandidateDiscovery;
namespace MacMappingTestsOnly;
#else
namespace NianLink.MacCandidateDiscovery;
#endif

internal sealed record ApprovedMapping(string Id, int Index, IPAddress Address, IPAddress Mask);
internal sealed record MappingSample(IReadOnlyList<MacNetworkRecord> Before, IReadOnlyList<MacWifiEvidence> Wifi,
    IReadOnlyList<MacNetworkRecord> After, bool ApprovedStillAvailable);
internal sealed record MappingObservation(string BsdName, int InterfaceIndex,
    MacCandidateNetworkBinding.Snapshot Selected, DiscoveryNetworkBinding? SharedBinding)
{
    internal bool HasLiveNetworkOrigin => false;
}

// One-shot read-only diagnostic. Results cannot authorize network activity.
internal sealed class MacReadOnlyMappingProbe
{
    private static readonly SemaphoreSlim SystemSamplingSlot = new(1, 1);
    private readonly object gate = new();
    private readonly TaskCompletionSource<bool> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool revoked, started;
    private long generation;
    internal bool IsRevoked { get { lock (gate) return revoked; } }

    internal void Invalidate()
    {
        lock (gate) { revoked = true; generation++; }
        stopped.TrySetResult(true);
    }

    internal Task<MappingObservation> ReadCurrentAsync(VerifiedLanBinding approved, TimeSpan timeout, CancellationToken cancellation) =>
        ReadCurrentCoreAsync(approved, timeout, cancellation, pinned: false);

    internal Task<MappingObservation> ReadCurrentPinnedAsync(VerifiedLanBinding approved, TimeSpan timeout, CancellationToken cancellation) =>
        ReadCurrentCoreAsync(approved, timeout, cancellation, pinned: true);

    private Task<MappingObservation> ReadCurrentCoreAsync(VerifiedLanBinding approved, TimeSpan timeout, CancellationToken cancellation, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS is required.");
        return ReadCoreAsync(token => CoordinateSystemSamplingAsync(async () =>
        {
            ThrowIfStopped();
            var binding = DiscoveryNetworkBinding.CreateForCurrentNetwork(approved);
            var expected = new ApprovedMapping(binding.InterfaceId, binding.InterfaceIndex, binding.Address, binding.SubnetMask);
            var before = ReadNetworks();
            ThrowIfStopped();
            var names = await (pinned ? WifiEvidenceReader.ReadPinnedAsync() : WifiEvidenceReader.ReadAsync()).ConfigureAwait(false);
            ThrowIfStopped();
            var wifi = names.Select(name => { ThrowIfStopped(); return new MacWifiEvidence(name, ReadIndex(name)); }).ToArray();
            ThrowIfStopped();
            var after = ReadNetworks();
            ThrowIfStopped();
            return (expected, new MappingSample(before, wifi, after, binding.IsStillAvailable() && approved.IsStillAvailable()), (DiscoveryNetworkBinding?)binding);
        }, WifiEvidenceReader.WaitForNativeIdleAsync, token), timeout, cancellation);
    }

    private static async Task<T> CoordinateSystemSamplingAsync<T>(Func<Task<T>> sample, Func<Task> waitUntilNativeIdle,
        CancellationToken cancellation)
    {
        await SystemSamplingSlot.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            return await sample().ConfigureAwait(false);
        }
        finally
        {
            // Cancellation cannot release the slot while native code still owns its pinned buffer/library.
            try { await waitUntilNativeIdle().ConfigureAwait(false); }
            finally { SystemSamplingSlot.Release(); }
        }
    }

    private void ThrowIfStopped()
    {
        lock (gate) if (revoked) throw new InvalidOperationException("Mapping probe was stopped.");
    }

#if MAC_WIFI_TESTING
    // Injection is compiled only into the isolated test assembly, never the candidate assembly.
    internal Task<MappingObservation> ReadForTestAsync(ApprovedMapping approved, Func<Task<MappingSample>> sample,
        TimeSpan timeout, CancellationToken cancellation)
    {
        var frozen = new ApprovedMapping(approved.Id, approved.Index, new IPAddress(approved.Address.GetAddressBytes()),
            new IPAddress(approved.Mask.GetAddressBytes()));
        return ReadCoreAsync(async _ => (frozen, await sample().ConfigureAwait(false), null), timeout, cancellation);
    }

    internal Task<MappingObservation> ReadCoordinatedForTestAsync(ApprovedMapping approved,
        Func<CancellationToken, Task<MappingSample>> sample, Func<Task> waitUntilNativeIdle,
        TimeSpan timeout, CancellationToken cancellation)
    {
        var frozen = new ApprovedMapping(approved.Id, approved.Index, new IPAddress(approved.Address.GetAddressBytes()),
            new IPAddress(approved.Mask.GetAddressBytes()));
        return ReadCoreAsync(token => CoordinateSystemSamplingAsync(async () =>
            (frozen, await sample(token).ConfigureAwait(false), (DiscoveryNetworkBinding?)null), waitUntilNativeIdle, token), timeout, cancellation);
    }
#endif

    private async Task<MappingObservation> ReadCoreAsync(Func<CancellationToken, Task<(ApprovedMapping Approved, MappingSample Sample, DiscoveryNetworkBinding? Shared)>> sample,
        TimeSpan timeout, CancellationToken cancellation)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        long epoch;
        lock (gate)
        {
            if (revoked || started) throw new InvalidOperationException("Mapping probe is no longer available.");
            started = true; epoch = generation;
        }
        using var registration = cancellation.Register(Invalidate);
        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        workerCancellation.CancelAfter(timeout);
        var start = Stopwatch.GetTimestamp();
        var worker = Task.Run(() => sample(workerCancellation.Token));
        try
        {
            if (await Task.WhenAny(worker, stopped.Task, Task.Delay(timeout)).ConfigureAwait(false) != worker)
                throw new InvalidOperationException("Mapping probe was stopped or timed out.");
            var (approved, observed, shared) = await worker.ConfigureAwait(false);
            var result = Validate(approved, observed) with { SharedBinding = shared };
            lock (gate)
            {
                if (revoked || generation != epoch || cancellation.IsCancellationRequested || Stopwatch.GetElapsedTime(start) >= timeout)
                    throw new InvalidOperationException("Mapping probe expired.");
                return result;
            }
        }
        catch
        {
            Invalidate();
            _ = worker.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        finally { workerCancellation.Cancel(); }
    }

    private sealed record NetworkValue(string Id, string Name, int Index, NetworkInterfaceType Type,
        OperationalStatus Status, uint Address, uint Mask);

    private static NetworkValue[] Canonical(IReadOnlyList<MacNetworkRecord> records)
    {
        if (records is null || records.Count is < 1 or > 1024) throw new InvalidOperationException("Invalid network snapshot.");
        return records.Select(n => n is null ? throw new InvalidOperationException("Invalid network record.") :
            new NetworkValue(n.Id, n.Name, n.Index, n.Type, n.Status,
                MacCandidateNetworkBinding.ToUInt(n.Address), MacCandidateNetworkBinding.ToUInt(n.Mask)))
            .OrderBy(n => n.Id, StringComparer.Ordinal).ThenBy(n => n.Index).ThenBy(n => n.Address).ToArray();
    }

    private static MappingObservation Validate(ApprovedMapping approved, MappingSample sample)
    {
        if (sample is null || !sample.ApprovedStillAvailable || !Canonical(sample.Before).SequenceEqual(Canonical(sample.After)))
            throw new InvalidOperationException("Network changed during sampling.");
        var selected = MacCandidateNetworkBinding.Select(MacCandidateNetworkBinding.ToUInt(approved.Address), sample.Before, sample.Wifi);
        var after = MacCandidateNetworkBinding.Select(MacCandidateNetworkBinding.ToUInt(approved.Address), sample.After, sample.Wifi);
        if (selected != after || selected.Id != approved.Id || selected.Index != approved.Index ||
            selected.Mask != MacCandidateNetworkBinding.ToUInt(approved.Mask))
            throw new InvalidOperationException("System mapping does not match the approved network.");
        return new(selected.BsdName, selected.Index, selected, null);
    }

    private static MacNetworkRecord[] ReadNetworks()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces();
        if (adapters.Length is < 1 or > 1024) throw new InvalidOperationException("Network interfaces unavailable.");
        var result = new List<MacNetworkRecord>();
        foreach (var adapter in adapters)
        {
            var properties = adapter.GetIPProperties();
            var addresses = properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToArray();
            if (addresses.Length == 0) continue;
            var index = properties.GetIPv4Properties()?.Index ?? throw new InvalidOperationException("Interface index unavailable.");
            foreach (var address in addresses)
            {
                if (result.Count >= 1024 || address.IPv4Mask is null) throw new InvalidOperationException("Network snapshot unavailable.");
                result.Add(new(adapter.Id, adapter.Name, index, adapter.NetworkInterfaceType, adapter.OperationalStatus,
                    new IPAddress(address.Address.GetAddressBytes()), new IPAddress(address.IPv4Mask.GetAddressBytes())));
            }
        }
        return result.ToArray();
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "if_nametoindex")]
    private static extern uint NativeIfNameToIndex(byte[] name);

    private static int ReadIndex(string name)
    {
        // Names came from the strict ASCII bridge parser, with no embedded NUL.
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        var index = NativeIfNameToIndex(bytes);
        if (index == 0 || index > int.MaxValue) throw new InvalidOperationException("Interface index unavailable.");
        return (int)index;
    }
}
