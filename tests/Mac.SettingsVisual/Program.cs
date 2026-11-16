using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.Views;
using DesktopToolbox.Mac.ViewModels;

internal static class Program
{
    [STAThread] private static void Main() => AppBuilder.Configure<FixtureApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);

    private sealed class FixtureApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://NianDeskLink/")) { Source = new Uri("avares://NianDeskLink/Themes/DeskLinkTheme.axaml") });
        }
        public override void OnFrameworkInitializationCompleted()
        {
            base.OnFrameworkInitializationCompleted();
            Dispatcher.UIThread.Post(async () =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
                var fake = new FakeLink();
                var window = new MainWindow(fake, new FakeWireless()) { Width = 520, Height = 600, ShowInTaskbar = false, RequestedThemeVariant = ThemeVariant.Dark };
                try
                {
                    window.Show(); await Task.Delay(1000); window.UpdateLayout();
                    var names = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()).Where(x => x is not null).ToHashSet();
                    foreach (var expected in new[] { "开始共享", "停止共享", "导入 Windows 配对文件…", "打开辅助功能设置", "打开屏幕录制设置", "退出软件" })
                        if (!names.Contains(expected)) throw new InvalidOperationException("Missing settings action: " + expected);
                    var dir = SyntheticWorkDirectory.Create("mac-settings-visual-").Path;
                    var path = Path.Combine(dir, "settings.png");
                    var settingsWidth = window.Bounds.Width; var settingsHeight = window.Bounds.Height;
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                    bitmap.Render(window); bitmap.Save(path, PngBitmapEncoderOptions.Default);
                    var model = (MacLinkViewModel)window.DataContext!;
                    await model.PreviewPairingAsync("synthetic fixture");
                    await Task.Delay(300); window.UpdateLayout();
                    var field = typeof(MainWindow).GetField("pairingCard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    if (field.GetValue(window) is not Border { IsVisible: true })
                        throw new InvalidOperationException("The Windows fingerprint confirmation card did not become visible.");
                    if (!model.HasPairingPreview || !model.PairingDescription.Contains(new string('A', 64), StringComparison.Ordinal))
                        throw new InvalidOperationException("The synthetic fingerprint preview was not preserved.");
                    var confirm = (Button)typeof(MainWindow).GetField("acceptPairing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
                    var location = confirm.TranslatePoint(new Point(0, 0), window);
                    if (location is null || location.Value.Y < 0 || location.Value.Y + confirm.Bounds.Height > window.Bounds.Height)
                        throw new InvalidOperationException("Pairing confirmation is not visible after importing at the minimum settings size.");
                    using var preview = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                    preview.Render(window); preview.Save(Path.Combine(dir, "pairing.png"), PngBitmapEncoderOptions.Default);
                    model.RejectPairing(); fake.Connected = true; await model.RefreshAsync();
                    await Task.Delay(150); window.UpdateLayout();
                    var compactField = typeof(MainWindow).GetField("compactMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    if (compactField.GetValue(window) is not true || Math.Abs(window.Width - 380) > 1 || Math.Abs(window.Height - 208) > 1)
                        throw new InvalidOperationException("Connected compact layout was not restored.");
                    if (window.Foreground?.ToString() != "#fff2f2f5")
                        throw new InvalidOperationException("Compact window did not restore the original foreground palette.");
                    var toggle = (Button)typeof(MainWindow).GetField("settingsToggle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
                    toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(150); window.UpdateLayout();
                    if (compactField.GetValue(window) is not false || Math.Abs(window.Width - 640) > 1)
                        throw new InvalidOperationException("Opening settings did not restore the full layout.");
                    if (window.Foreground?.ToString() != "#ff243a59")
                        throw new InvalidOperationException("Expanded settings window did not restore the light foreground palette.");
                    toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(150); window.UpdateLayout();
                    if (compactField.GetValue(window) is not true || Math.Abs(window.Width - 380) > 1 || Math.Abs(window.Height - 208) > 1)
                        throw new InvalidOperationException("Returning from settings did not restore the compact layout.");
                    var foregrounds = window.GetVisualDescendants().OfType<TextBlock>()
                        .Where(x => x.Text is "共享状态" or "共享已停止" or "年年互联" or "切到 Windows")
                        .Select(x => new { x.Text, foreground = x.Foreground?.ToString(), x.IsVisible, bounds = x.Bounds.ToString() }).ToArray();
                    Console.WriteLine(JsonSerializer.Serialize(new { passed = true, synthetic = true, output = path, settingsWidth, settingsHeight, compactReturned = true, foregrounds }));
                    window.Close(); lifetime.Shutdown(0);
                }
                catch (Exception error) { Console.Error.WriteLine(error); window.Close(); lifetime.Shutdown(1); }
            });
        }
    }
    private sealed class FakeWireless : IMacWirelessModeService
    {
        public Task<MacWirelessSnapshot> RefreshAsync(CancellationToken token = default) => Task.FromResult(new MacWirelessSnapshot(MacAwdlState.Unavailable, "暂不可用", ""));
        public Task<MacWirelessSnapshot> SetSmoothModeAsync(bool enabled, CancellationToken token = default) => RefreshAsync(token);
    }
    private sealed class FakeLink : IMacLinkService
    {
        internal bool Connected { get; set; }
        public event Action<MacLinkSnapshot>? Changed { add { } remove { } }
        public Task<MacLinkSnapshot> RefreshAsync(CancellationToken token = default) => Task.FromResult(Connected
            ? new MacLinkSnapshot { HasApprovedPairing = true, IsRunning = true, IsConnected = true, ControlConnected = true, ActiveComputer = "My-Mac" }
            : new MacLinkSnapshot());
        public Task StartAsync(MacConnectionSettings settings, CancellationToken token = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task ConfirmPeerAsync(string fingerprint, bool accept, CancellationToken token = default) => Task.CompletedTask;
        public Task<PairingPreview> PreviewPairingAsync(string json, CancellationToken token = default) => Task.FromResult(new PairingPreview("fixture", "192.0.2.101", 24801, "Fixture-Win", "Fixture-Mac", new string('A', 64), "测试指纹"));
        public Task AcceptPairingAsync(string previewId, CancellationToken token = default) => Task.CompletedTask;
        public Task<MacSwitchOutcome> SwitchAsync(string target, CancellationToken token = default) => Task.FromResult(new MacSwitchOutcome(false, "", false, "disabled", ""));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
