using System.Reflection;
using Avalonia.Controls;
using DesktopToolbox.Mac.Views;

internal static partial class Program
{
    private sealed partial class FixtureApp
    {
        private static async Task VerifyExceptionSentinelsAsync()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var errors = new Exception[]
            {
                new IOException("SYNTHETIC_SENTINEL_IO"),
                new TimeoutException("SYNTHETIC_SENTINEL_TIMEOUT"),
                new OperationCanceledException("SYNTHETIC_SENTINEL_CANCELED"),
                new InvalidOperationException("SYNTHETIC_SENTINEL_RECOVERY"),
                new Exception("SYNTHETIC_SENTINEL_OTHER")
            };
            var failures = new List<string>();
            foreach (var error in errors)
            {
                var calls = 0;
                var window = new MacRemotePreviewReturnWindow("synthetic", _ =>
                {
                    calls++;
                    return Task.FromException(error);
                });
                window.Show();
                try
                {
                    var type = typeof(MacRemotePreviewReturnWindow);
                    var button = (Button)type.GetField("button", flags)!.GetValue(window)!;
                    await ((Task)type.GetMethod("ReturnAsync", flags)!.Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(3));
                    var content = button.Content as string ?? "";
                    var tip = ToolTip.GetTip(button) as string ?? "";
                    if (calls != 1 || !window.IsVisible || !button.IsEnabled ||
                        !content.Contains("未确认", StringComparison.Ordinal) ||
                        !tip.Contains("可能仍在处理", StringComparison.Ordinal) ||
                        content.Contains(error.Message, StringComparison.Ordinal) ||
                        tip.Contains(error.Message, StringComparison.Ordinal))
                        failures.Add(error.GetType().Name);
                }
                finally { if (window.IsVisible) window.Close(); }
            }
            if (failures.Count > 0) throw new InvalidOperationException("Unconfirmed entry missing for: " + string.Join(", ", failures));
        }
    }
}
