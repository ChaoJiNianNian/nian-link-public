using System.Text.Json;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services.Display;

namespace DesktopToolbox.Mac.Services;

internal sealed record MacDisplayBridgeReply(bool Ok, IReadOnlyList<MacDisplayDescriptor>? Displays = null, MacDisplayReadResult? Read = null, MacDisplaySetResult? Set = null, string? Error = null);

/// <summary>Restricted apphost child mode. It never initializes Avalonia, profiles, pairing or input sharing.</summary>
internal static class MacDisplayBridgeCommand
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, MaxDepth = 12 };
    public static async Task<int> RunAsync(string[] args)
    {
        MacDisplayBridgeReply reply;
        try
        {
            var options = MacDisplayBridgeOptions.Parse(args);
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Display bridge requires macOS.");
            using var control = new MacNativeDisplayControl();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var identity = new MacDisplayIdentity(options.VendorId, options.ProductId);
            reply = options.Action switch
            {
                "list" => new(true, Displays: await control.ListAsync(timeout.Token)),
                "read" => new(true, Read: await control.ReadInputAsync(identity, timeout.Token)),
                "set" => new(true, Set: await control.SetInputAsync(identity, options.Input, timeout.Token)),
                _ => throw new ArgumentException("Unknown display bridge action.")
            };
        }
        catch (Exception error) { reply = new(false, Error: error.Message); }
        var json = JsonSerializer.Serialize(reply, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 16384) { reply = new(false, Error: "Display bridge response exceeded 16 KiB."); json = JsonSerializer.Serialize(reply, Json); }
        await Console.Out.WriteLineAsync(json);
        return reply.Ok ? 0 : 2;
    }
}
