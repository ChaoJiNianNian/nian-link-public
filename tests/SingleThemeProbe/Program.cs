using System.Reflection;
using System.Windows;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        _ = new Application();
        var assembly = Assembly.Load("NianLink");
        var previewType = assembly.GetType("DesktopToolbox.App.DeskLink.DetachedPreviewWindow")!;
        var miniType = assembly.GetType("DesktopToolbox.App.DeskLink.MiniCollaborationWindow")!;
        if (previewType.GetMethod("SetClearBlueAppearance") is not null) throw new InvalidOperationException("multi-device theme leaked into legacy assembly");
        var preview = (Window)Activator.CreateInstance(previewType, nonPublic: true)!;
        var method = miniType.GetMethod("ApplyDetachedAppearance", BindingFlags.Static | BindingFlags.NonPublic)!;
        method.Invoke(null, new object[] { preview, true, false, false });
        if (preview.Background is not SolidColorBrush brush || brush.Color != Color.FromRgb(234, 240, 248))
            throw new InvalidOperationException("legacy preview theme changed");
        Console.WriteLine("legacy assembly retains legacy preview palette even for multi-device flag");
        return 0;
    }
}
