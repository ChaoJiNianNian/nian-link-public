using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nian.Input.Control;

namespace Nian.Input.Mac;

public sealed record MacDesktopSourceGeometry(string SourceId, string Revision, double Left, double Top, double Width, double Height)
{
    public PreviewSourceGeometry ToPreviewGeometry(int imageWidth, int imageHeight) =>
        new(SourceId, Revision, Left, Top, Width, Height, imageWidth, imageHeight);
}

internal sealed record MacNativeDisplayInfo(uint Id, double Left, double Top, double Width, double Height, double Rotation);

/// <summary>Synchronous read-only CoreGraphics metadata; no helper process, UI monitor or input posting.</summary>
public static class MacDesktopSource
{
    public static MacDesktopSourceGeometry? ReadCurrentPreviewSource()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try { var snapshot = ReadNativeSnapshot(); return CreateGeometry(snapshot.Main, snapshot.Displays); }
        catch { return null; }
    }

    public static PreviewSourceGeometry? ReadPreviewGeometry(int imageWidth, int imageHeight)
    {
        var geometry = ReadCurrentPreviewSource()?.ToPreviewGeometry(imageWidth, imageHeight);
        return geometry?.IsValid == true ? geometry : null;
    }

    internal static MacDesktopSourceGeometry CreateGeometry(uint main, IReadOnlyList<MacNativeDisplayInfo> displays)
    {
        if (main == 0 || displays.Count is < 1 or > 64 || displays.Select(d => d.Id).Distinct().Count() != displays.Count ||
            displays.Any(d => d.Id == 0 || !Valid(d.Left) || !Valid(d.Top) || !Valid(d.Width) || !Valid(d.Height) || !Valid(d.Rotation) ||
                d.Width is < 1 or > 100_000 || d.Height is < 1 or > 100_000)) throw new InvalidOperationException("Native display metadata is invalid.");
        var primary = displays.SingleOrDefault(d => d.Id == main) ?? throw new InvalidOperationException("The primary display is not active.");
        var canonical = "primary:" + main.ToString(CultureInfo.InvariantCulture) + ";" + string.Join(";", displays.OrderBy(d => d.Id).Select(d =>
            d.Id.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(",", new[] { d.Left, d.Top, d.Width, d.Height, d.Rotation }.Select(value =>
                checked((long)Math.Round(value * 1_000_000, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture)))));
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new("mac-display-" + main.ToString(CultureInfo.InvariantCulture), revision, primary.Left, primary.Top, primary.Width, primary.Height);
    }
    private static bool Valid(double value) => double.IsFinite(value) && Math.Abs(value) <= 1_000_000;

    internal static IReadOnlyList<MacDesktopDisplay> ReadNativeDisplays()
    {
        var snapshot = ReadNativeSnapshot(); var geometry = CreateGeometry(snapshot.Main, snapshot.Displays);
        return snapshot.Displays.Select(d => new MacDesktopDisplay(d.Left, d.Top, d.Width, d.Height,
            "mac-display-" + d.Id.ToString(CultureInfo.InvariantCulture), geometry.Revision)).ToArray();
    }

    private static (uint Main, MacNativeDisplayInfo[] Displays) ReadNativeSnapshot()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var ids = new uint[64]; var main = QuartzDesktopNative.CGMainDisplayID();
        if (QuartzDesktopNative.CGGetActiveDisplayList(64, ids, out var count) != 0 || count is < 1 or > 64) throw new InvalidOperationException("Active display enumeration failed.");
        var active = ids.Take((int)count).Order().ToArray();
        var displays = active.Select(id =>
        {
            var bounds = QuartzDesktopNative.CGDisplayBounds(id);
            return new MacNativeDisplayInfo(id, bounds.Origin.X, bounds.Origin.Y, bounds.Size.Width, bounds.Size.Height, QuartzDesktopNative.CGDisplayRotation(id));
        }).ToArray();
        var after = new uint[64];
        if (main != QuartzDesktopNative.CGMainDisplayID() || QuartzDesktopNative.CGGetActiveDisplayList(64, after, out var afterCount) != 0 ||
            afterCount != count || !active.SequenceEqual(after.Take((int)afterCount).Order())) throw new InvalidOperationException("The display layout changed while reading its identity.");
        return (main, displays);
    }
}
