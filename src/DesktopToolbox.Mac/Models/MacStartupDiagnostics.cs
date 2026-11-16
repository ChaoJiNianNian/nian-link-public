namespace DesktopToolbox.Mac.Models;

/// <summary>Backend facts for the startup report; native input has no external-core diagnostic.</summary>
public static class MacStartupDiagnostics
{
    public static Dictionary<string, object?> Create(MacLinkSnapshot state, string runtimeService, Func<string, bool> fileExists)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["engineKind"] = state.Backend switch { MacLinkBackend.Native => "native", MacLinkBackend.Deskflow => "deskflow", _ => "unknown" },
            ["runtimeService"] = runtimeService
        };
        if (state.Backend == MacLinkBackend.Deskflow)
        {
            fields["EnginePath"] = state.Settings.EnginePath;
            fields["officialCorePresent"] = fileExists(state.Settings.EnginePath);
        }
        return fields;
    }
}
