using Avalonia;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac;

internal static class Program
{
    internal static MacStartupOptions StartupOptions { get; private set; } = new(null, false);

    [STAThread]
    public static int Main(string[] args)
    {
#if !DISCOVERY_CANDIDATE
        if (args.FirstOrDefault() == "--display-bridge")
            return MacDisplayBridgeCommand.RunAsync(args[1..]).GetAwaiter().GetResult();
#endif
        try
        {
            StartupOptions = MacStartupOptions.Parse(args);
#if DISCOVERY_CANDIDATE
            StartupOptions = StartupOptions.ResolveDiscoveryTestProfile(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
#elif MULTI_DEVICE_CANDIDATE
            StartupOptions = StartupOptions.ResolveMultiDeviceCandidateProfile(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
#elif NATIVE_INPUT_CANDIDATE
            StartupOptions = StartupOptions.ResolveNativeCandidateProfile(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
#elif PREVIEW_CONTROL_CANDIDATE
            StartupOptions = StartupOptions.ResolvePreviewCandidateProfile(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
#endif
            StartupOptions.ValidateStartupCheckIsolation();
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
