using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using NianLink.MultiDevice;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("isolated profile required");
        _ = new Application();
        var candidate = new MainWindow(args[0], Path.Combine(args[0], "no-engine.exe"));
        var logo = candidate.FindName("BrandLogo") as Image ?? throw new InvalidOperationException("brand image missing");
        if (logo.Source is not BitmapSource bitmap || bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1)
            throw new InvalidOperationException("candidate brand resource did not decode");
        Console.WriteLine($"candidate application assembly brand decoded: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
        return 0;
    }
}
