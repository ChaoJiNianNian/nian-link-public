using System.Text.Json;
#if MAC_WIFI_TESTING
using MacWifiAdapterTestsOnly;
#else
using NianLink.MacCandidateDiscovery;
#endif

namespace MacManagedWifiProbe;

internal static class ProbeProgram
{
    internal static string LibraryPath => WifiEvidenceReader.LibraryPath;

#if !MAC_WIFI_TESTING
    public static Task<int> Main(string[] args) => RunAsync(args, Console.Out);
#endif

    internal static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        if (args.Length != 2 || args[0] != "--run" || !ProbeDigest.IsValid(args[1]))
        {
            await output.WriteLineAsync("{\"version\":1,\"status\":\"not_started\"}");
            return 2;
        }
        try
        {
            if (!OperatingSystem.IsMacOS() || !await ProbeDigest.MatchesAsync(LibraryPath, args[1]))
                throw new InvalidOperationException();
            var names = await WifiEvidenceReader.ReadAsync();
            await output.WriteLineAsync(JsonSerializer.Serialize(new { version = 1, status = "ok", wifi_candidates = names }));
            return 0;
        }
        catch (Exception)
        {
            await output.WriteLineAsync("{\"version\":1,\"status\":\"unavailable\"}");
            return 1;
        }
    }

}
