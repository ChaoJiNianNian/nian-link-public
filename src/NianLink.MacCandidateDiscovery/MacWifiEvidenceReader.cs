using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;

#if MAC_WIFI_TESTING
namespace MacWifiAdapterTestsOnly;
#else
namespace NianLink.MacCandidateDiscovery;
#endif

// Evidence only: neither a live binding nor a peer or physical-device attestation.
internal static class WifiEvidenceReader
{
    private const int Capacity = 16384;
    private const int MaxNames = 128;
    private const string PinMetadataKey = "NianWifiEvidencePinnedSha256";
    private static readonly object FlightGate = new();
    private static TaskCompletionSource<bool>? inFlight;
    internal static string LibraryPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "libNianWifiEvidence.dylib"));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeCall(IntPtr buffer, int capacity, out int written);

    internal static Task<string[]> ReadAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS is required.");
        return ReadCoreAsync(InvokeNative, TimeSpan.FromSeconds(2));
    }

    internal static Task<string[]> ReadPinnedAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS is required.");
        return ReadCoreAsync(buffer =>
        {
            if (!MatchesPinnedBridge(LibraryPath)) throw new InvalidOperationException("Pinned bridge unavailable.");
            return InvokeNative(buffer);
        }, TimeSpan.FromSeconds(2));
    }

    // Remains incomplete until the native worker actually returns, even if its caller timed out.
    internal static Task WaitForNativeIdleAsync()
    { lock (FlightGate) return inFlight?.Task ?? Task.CompletedTask; }

#if MAC_WIFI_TESTING
    // This injection point is compiled only into the isolated test assembly.
    internal static Task<string[]> ReadForTestAsync(Func<byte[], (int Status, int Written)> call, TimeSpan timeout) =>
        ReadCoreAsync(call, timeout);
    internal static bool MatchesPinnedBridgeForTest(string path) => MatchesPinnedBridge(path);
#endif

    private static bool MatchesPinnedBridge(string path)
    {
        try
        {
            var pin = SelectPin(typeof(WifiEvidenceReader).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>());
            if (pin is null) return false;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 67108864) return false;
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), Convert.FromHexString(pin));
        }
        catch (Exception) { return false; }
    }

    internal static string? SelectPin(IEnumerable<AssemblyMetadataAttribute> attributes)
    {
        var pins = attributes.Where(attribute => attribute.Key == PinMetadataKey).Take(2).ToArray();
        if (pins.Length != 1 || pins[0].Value is not { Length: 64 } pin ||
            pin.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) return null;
        return pin;
    }

    private static async Task<string[]> ReadCoreAsync(Func<byte[], (int Status, int Written)> call, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        TaskCompletionSource<bool> completed;
        lock (FlightGate)
        {
            if (inFlight is not null) throw new InvalidOperationException("A native evidence call is already in progress.");
            inFlight = completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Task<string[]> worker;
        try
        {
            worker = Task.Run(() =>
            {
                try
                {
                    var buffer = new byte[Capacity];
                    var (status, written) = call(buffer);
                    if (status != 0 || written <= 0 || written > buffer.Length)
                        throw new InvalidOperationException("Native Wi-Fi evidence was unavailable.");
                    return Parse(buffer.AsMemory(0, written));
                }
                catch (Exception e) when (e is not InvalidOperationException)
                { throw new InvalidOperationException("Native Wi-Fi evidence was unavailable.", e); }
                finally
                {
                    lock (FlightGate) { inFlight = null; completed.TrySetResult(true); }
                }
            });
        }
        catch { lock (FlightGate) { inFlight = null; completed.TrySetResult(true); } throw; }
        if (await Task.WhenAny(worker, Task.Delay(timeout)).ConfigureAwait(false) != worker)
        {
            // Observe a late fault without consuming or publishing a late result.
            _ = worker.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new TimeoutException("Native Wi-Fi evidence timed out.");
        }
        return await worker.ConfigureAwait(false);
    }

    private static (int Status, int Written) InvokeNative(byte[] buffer)
    {
        // A fixed location avoids ordinary library search paths. It is not a signature or integrity check.
        var library = NativeLibrary.Load(LibraryPath);
        try
        {
            var entry = NativeLibrary.GetExport(library, "nian_wifi_evidence_v1");
            var call = Marshal.GetDelegateForFunctionPointer<NativeCall>(entry);
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try { var status = call(pin.AddrOfPinnedObject(), buffer.Length, out var written); return (status, written); }
            finally { pin.Free(); }
        }
        finally { NativeLibrary.Free(library); }
    }

    private static string[] Parse(ReadOnlyMemory<byte> utf8)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 4 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
            var version = false; var candidates = false;
            var names = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.NameEquals("version") && !version)
                {
                    version = true;
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var v) || v != 1)
                        throw new InvalidOperationException();
                }
                else if (p.NameEquals("wifi_candidates") && !candidates)
                {
                    candidates = true;
                    if (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() is < 1 or > MaxNames)
                        throw new InvalidOperationException();
                    foreach (var item in p.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) throw new InvalidOperationException();
                        var name = item.GetString();
                        if (name is not { Length: > 0 and <= 63 } ||
                            !name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.') ||
                            !seen.Add(name)) throw new InvalidOperationException();
                        names.Add(name);
                    }
                }
                else throw new InvalidOperationException();
            }
            if (!version || !candidates) throw new InvalidOperationException();
            return names.ToArray();
        }
        catch (JsonException e) { throw new InvalidOperationException("Invalid native Wi-Fi evidence.", e); }
    }
}
