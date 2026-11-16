using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using MacManagedWifiProbe;
#if MAC_WIFI_TESTING
using MacMappingTestsOnly;
using MacWifiAdapterTestsOnly;
#else
using NianLink.MacCandidateDiscovery;
#endif

namespace MacManagedMappingProbe;

internal static class MappingProbeProgram
{
#if !MAC_WIFI_TESTING
    public static Task<int> Main(string[] args) => RunAsync(args, Console.Out);
#endif

    internal static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        if (args.Length != 3 || args[0] != "--run" || !ProbeDigest.IsValid(args[1]) ||
            !IPAddress.TryParse(args[2], out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            await output.WriteLineAsync("{\"version\":1,\"status\":\"not_started\"}");
            return 2;
        }
        try
        {
            if (!OperatingSystem.IsMacOS() || !await ProbeDigest.MatchesAsync(WifiEvidenceReader.LibraryPath, args[1]))
                throw new InvalidOperationException();
            var approved = VerifiedLanBinding.VerifyCurrentNetwork(address, userApproved: true);
            var observation = await new MacReadOnlyMappingProbe().ReadCurrentAsync(approved, TimeSpan.FromSeconds(5), default);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { version = 1, status = "ok", bsd_name = observation.BsdName,
                interface_index = observation.InterfaceIndex }));
            return 0;
        }
        catch (Exception)
        {
            await output.WriteLineAsync("{\"version\":1,\"status\":\"unavailable\"}");
            return 1;
        }
    }
}
