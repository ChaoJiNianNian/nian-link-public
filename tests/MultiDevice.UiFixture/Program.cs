using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NianLink.MultiDevice;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("An absolute output file is required.");
        var app = new Application();
        if (args.Length == 2 && args[1] == "network-status-busy-ui")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "network-busy-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var deadline = DateTime.UtcNow.AddSeconds(15); var started = false;
            timer.Tick += async (_, _) =>
            {
                if (started) return;
                if (DateTime.UtcNow > deadline) { File.WriteAllText(args[0], "busy network status timed out"); Environment.Exit(1); }
                if (!((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).IsEnabled) return;
                started = true;
                DesktopToolbox.DeskLink.MultiDeviceCredentialVault? vault = null;
                DesktopToolbox.DeskLink.MultiPeerRuntime? runtime = null;
                try
                {
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    vault = await DesktopToolbox.DeskLink.MultiDeviceCredentialVault.OpenAsync(Path.Combine(profile, "synthetic-vault"), new NianLink.Windows.WindowsCredentialProtector());
                    runtime = new DesktopToolbox.DeskLink.MultiPeerRuntime(vault, Path.Combine(profile, "synthetic-runtime"), "host",
                        System.Net.IPAddress.Loopback, 0, 0, _ => throw new InvalidOperationException("keyboard must not start"));
                    typeof(MainWindow).GetField("host", flags)!.SetValue(candidate, runtime);
                    typeof(MainWindow).GetField("busy", flags)!.SetValue(candidate, true);
                    var statusField = typeof(DesktopToolbox.DeskLink.MultiPeerRuntime).GetField("networkLossStatus", flags)!;
                    statusField.SetValue(runtime, (int)DesktopToolbox.DeskLink.HostNetworkLossStatus.Stopping);
                    var refresh = typeof(MainWindow).GetMethod("RefreshStatusAsync", flags)!;
                    await ((Task)refresh.Invoke(candidate, null)!).WaitAsync(TimeSpan.FromSeconds(3));
                    var notice = (System.Windows.Controls.TextBlock)candidate.FindName("HostNetworkNotice");
                    if (notice.Visibility != Visibility.Visible || !notice.Text.Contains("正在停止")) throw new InvalidOperationException("busy window lost stopping notice");
                    statusField.SetValue(runtime, (int)DesktopToolbox.DeskLink.HostNetworkLossStatus.AwaitingNetworkApproval);
                    await ((Task)refresh.Invoke(candidate, null)!).WaitAsync(TimeSpan.FromSeconds(3));
                    if (!notice.Text.Contains("关闭并重新打开测试版") || !notice.Text.Contains("确认网络")) throw new InvalidOperationException("reapproval instructions do not match actual flow");
                    statusField.SetValue(runtime, (int)DesktopToolbox.DeskLink.HostNetworkLossStatus.CleanupUnconfirmed);
                    typeof(MainWindow).GetField("statusRefreshing", flags)!.SetValue(candidate, true);
                    await ((Task)refresh.Invoke(candidate, null)!).WaitAsync(TimeSpan.FromSeconds(3));
                    if (!notice.Text.Contains("未确认")) throw new InvalidOperationException("refreshing window lost unconfirmed notice");
                    File.WriteAllText(args[0], "network notice persists while busy and status refresh is in progress");
                }
                catch (Exception error) { File.WriteAllText(args[0], error.ToString()); Environment.ExitCode = 1; }
                finally
                {
                    typeof(MainWindow).GetField("host", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(candidate, null);
                    if (runtime is not null) await runtime.DisposeAsync();
                    if (vault is not null) await vault.DisposeAsync();
                    Environment.Exit(Environment.ExitCode);
                }
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "network-status-ui")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "network-status-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var deadline = DateTime.UtcNow.AddSeconds(15);
            timer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow > deadline) { File.WriteAllText(args[0], "network status timed out"); Environment.Exit(1); }
                if (!((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).IsEnabled) return;
                try
                {
                    var notice = candidate.FindName("HostNetworkNotice") as System.Windows.Controls.TextBlock ?? throw new InvalidOperationException("persistent network notice missing");
                    var present = typeof(MainWindow).GetMethod("PresentHostNetworkStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("network presenter missing");
                    foreach (var item in new[]
                    {
                        (DesktopToolbox.DeskLink.HostNetworkLossStatus.Stopping, "正在停止"),
                        (DesktopToolbox.DeskLink.HostNetworkLossStatus.AwaitingNetworkApproval, "确认网络"),
                        (DesktopToolbox.DeskLink.HostNetworkLossStatus.CleanupUnconfirmed, "未确认")
                    })
                    {
                        present.Invoke(candidate, new object[] { item.Item1 });
                        if (notice.Visibility != Visibility.Visible || !notice.Text.Contains(item.Item2, StringComparison.Ordinal))
                            throw new InvalidOperationException("network notice is incorrect for " + item.Item1);
                        if (notice.Text.Contains("已安全归还", StringComparison.Ordinal)) throw new InvalidOperationException("unconfirmed return reported as safe");
                    }
                    present.Invoke(candidate, new object[] { DesktopToolbox.DeskLink.HostNetworkLossStatus.None });
                    if (notice.Visibility != Visibility.Collapsed) throw new InvalidOperationException("notice did not clear for absent status");
                    File.WriteAllText(args[0], "network loss states displayed without claiming confirmed return"); Environment.Exit(0);
                }
                catch (Exception error) { File.WriteAllText(args[0], error.ToString()); Environment.Exit(1); }
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "blue-detached")
        {
            var type = typeof(MainWindow).Assembly.GetType("DesktopToolbox.App.DeskLink.DetachedPreviewWindow")!;
            var previewWindow = (Window)Activator.CreateInstance(type, nonPublic: true)!;
            var method = type.GetMethod("SetClearBlueAppearance") ?? throw new InvalidOperationException("candidate preview theme missing");
            method.Invoke(previewWindow, null);
            var ok = previewWindow.Background is SolidColorBrush brush && brush.Color == Color.FromRgb(246, 249, 255);
            var miniType = typeof(MainWindow).Assembly.GetType("DesktopToolbox.App.DeskLink.MiniCollaborationWindow")!;
            var applyMini = miniType.GetMethod("ApplyDetachedAppearance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("mini preview appearance selector missing");
            applyMini.Invoke(null, new object[] { previewWindow, true, false, false });
            ok = ok && previewWindow.Background is SolidColorBrush blue && blue.Color == Color.FromRgb(246, 249, 255);
            applyMini.Invoke(null, new object[] { previewWindow, false, false, false });
            ok = ok && previewWindow.Background is SolidColorBrush legacy && legacy.Color == Color.FromRgb(234, 240, 248);
            File.WriteAllText(args[0], ok ? "candidate detached preview uses clear-blue palette" : "detached preview palette incorrect");
            Environment.Exit(ok ? 0 : 1);
        }
        if (args.Length == 2 && args[1] == "blue-confirmations")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "blue-confirm-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; var started = false;
            timer.Tick += (_, _) =>
            {
                if (started || !((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).IsEnabled) return;
                started = true;
                try
                {
                    var ask = typeof(MainWindow).GetMethod("AskBlueConfirmation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("blue confirmation missing");
                    var message = "核对完整指纹：\n" + new string('A', 64);
                    foreach (var action in new[] { "cancel", "accept", "close", "timeout" })
                    {
                        var inspected = false;
                        var inspect = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                        inspect.Tick += (_, _) =>
                        {
                            var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "确认测试");
                            if (dialog is null) return;
                            var yes = FindButton(dialog, "一致，确认"); var no = FindButton(dialog, "取消");
                            var texts = CollectText(dialog);
                            inspected = yes is not null && no is not null && no.IsCancel && !yes.IsDefault && texts.Contains(message) &&
                                ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(dialog), no);
                            inspect.Stop();
                            if (action == "cancel") no?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                            else if (action == "accept") yes?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                            else if (action == "close") dialog.Close();
                        };
                        inspect.Start(); using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                        var accepted = (bool)ask.Invoke(candidate, new object[] { "确认测试", message, "一致，确认", cancel.Token })!;
                        inspect.Stop();
                        if (!inspected || accepted != (action == "accept")) throw new InvalidOperationException("confirmation behavior mismatch: " + action);
                    }
                    File.WriteAllText(args[0], "fingerprint intact; cancel, confirm, close and timeout behave safely"); Environment.Exit(0);
                }
                catch (Exception error) { File.WriteAllText(args[0], error.ToString()); Environment.Exit(1); }
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "blue-target-labels")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "target-label-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; var deadline = DateTime.UtcNow.AddSeconds(15);
            timer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow > deadline) { File.WriteAllText(args[0], "target labels timed out"); Environment.Exit(1); }
                if (!((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).IsEnabled) return;
                try
                {
                    var preview = candidate.FindName("HostPreviewTarget") as System.Windows.Controls.TextBlock ?? throw new InvalidOperationException("preview target missing");
                    var files = candidate.FindName("HostFilesTarget") as System.Windows.Controls.TextBlock ?? throw new InvalidOperationException("file target missing");
                    var list = (System.Windows.Controls.ListBox)candidate.FindName("HostDevices");
                    var devices = new[] { new DesktopToolbox.DeskLink.TrustedDeviceIdentity("A", new string('A', 64)), new DesktopToolbox.DeskLink.TrustedDeviceIdentity("B", new string('B', 64)) };
                    list.ItemsSource = devices;
                    if (!preview.Text.Contains("未选择") || !files.Text.Contains("未选择")) throw new InvalidOperationException("empty target is not explicit");
                    list.SelectedItem = devices[0]; if (!preview.Text.Contains("A") || !files.Text.Contains("A")) throw new InvalidOperationException("first target missing");
                    list.SelectedItem = devices[1]; if (!preview.Text.Contains("B") || !files.Text.Contains("B")) throw new InvalidOperationException("changed target missing");
                    typeof(MainWindow).GetMethod("ApplyHostKeyboardState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(candidate, new object[] { new DesktopToolbox.DeskLink.DeskLinkState { ProcessState = DesktopToolbox.DeskLink.DeskLinkProcessState.Running, ActiveComputer = "A" } });
                    if (!preview.Text.Contains("B") || !files.Text.Contains("B")) throw new InvalidOperationException("status refresh changed target");
                    list.SelectedItem = null; if (!preview.Text.Contains("未选择") || !files.Text.Contains("未选择")) throw new InvalidOperationException("cleared target missing");
                    File.WriteAllText(args[0], "both pages track selection and show no selection"); Environment.Exit(0);
                }
                catch (Exception error) { File.WriteAllText(args[0], error.ToString()); Environment.Exit(1); }
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "blue-preview-controls")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "preview-controls-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var deadline = DateTime.UtcNow.AddSeconds(15); var stage = 0;
            timer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow > deadline) { File.WriteAllText(args[0], "preview controls timed out"); Environment.Exit(1); }
                var tabs = (System.Windows.Controls.TabControl)candidate.FindName("ModeTabs"); if (!tabs.IsEnabled) return;
                var page = (FrameworkElement)candidate.FindName("HostPreviewCard");
                if (stage == 0)
                {
                    typeof(MainWindow).GetMethod("ShowSettingsPage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(candidate, new object[] { "HostPreviewCard" });
                    stage = 1; return;
                }
                if (stage == 1)
                {
                    var refresh = FindButton(page, "刷新画面"); var operate = FindButton(page, "开始操作");
                    if (refresh is null || operate is null) { File.WriteAllText(args[0], "preview actions missing"); Environment.Exit(1); return; }
                    stage = 2; refresh.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); return;
                }
                if (stage == 2)
                {
                    var text = ((System.Windows.Controls.TextBlock)candidate.FindName("Feedback")).Text;
                    if (!text.Contains("先选择设备", StringComparison.Ordinal)) { File.WriteAllText(args[0], "refresh did not validate prerequisites: " + text); Environment.Exit(1); return; }
                    stage = 3; FindButton(page, "开始操作")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); return;
                }
                var result = ((System.Windows.Controls.TextBlock)candidate.FindName("Feedback")).Text;
                var ok = result.Contains("先启动主控共享", StringComparison.Ordinal);
                File.WriteAllText(args[0], ok ? "preview actions invoke real prerequisite checks" : "operation did not validate prerequisites: " + result);
                Environment.Exit(ok ? 0 : 1);
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "blue-preview-guard")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "preview-guard-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var started = false;
            timer.Tick += async (_, _) =>
            {
                if (started) return;
                if (DateTime.UtcNow > deadline) { File.WriteAllText(args[0], "preview guard timed out"); Environment.Exit(1); }
                if (!((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).IsEnabled) return;
                started = true;
                try
                {
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var stateMethod = typeof(MainWindow).GetMethod("ApplyHostKeyboardState", flags) ?? throw new InvalidOperationException("keyboard state method missing");
                    var devices = new[] { new DesktopToolbox.DeskLink.TrustedDeviceIdentity("A", new string('A', 64)), new DesktopToolbox.DeskLink.TrustedDeviceIdentity("B", new string('B', 64)) };
                    var list = (System.Windows.Controls.ListBox)candidate.FindName("HostDevices"); list.ItemsSource = devices; list.SelectedItem = devices[1];
                    stateMethod.Invoke(candidate, new object[] { new DesktopToolbox.DeskLink.DeskLinkState { ProcessState = DesktopToolbox.DeskLink.DeskLinkProcessState.Running, ActiveComputer = "A", ConnectedComputers = new[] { "A", "B" } } });
                    if (!ReferenceEquals(list.SelectedItem, devices[1])) throw new InvalidOperationException("keyboard state replaced the user's selected device");
                    var apply = typeof(MainWindow).GetMethod("ApplyHostPreviewFrameAsync", flags) ?? throw new InvalidOperationException("preview frame gate missing");
                    var viewed = typeof(MainWindow).GetField("hostViewedPeer", flags)!;
                    var enabled = typeof(MainWindow).GetField("hostViewEnabled", flags)!;
                    var epoch = typeof(MainWindow).GetField("previewEpoch", flags)!;
                    var label = (System.Windows.Controls.TextBlock)candidate.FindName("HostPreviewStatus");
                    list.SelectedItem = devices[0]; viewed.SetValue(candidate, "A"); enabled.SetValue(candidate, true);
                    var pending = new TaskCompletionSource<DesktopToolbox.DeskLink.Collaboration.PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var capturedEpoch = (long)epoch.GetValue(candidate)!;
                    Func<bool> runtime = () => true;
                    Func<Task<DesktopToolbox.DeskLink.Collaboration.PreviewSourceStatus?>> source = () => Task.FromResult<DesktopToolbox.DeskLink.Collaboration.PreviewSourceStatus?>(null);
                    var operation = (Task<bool>)apply.Invoke(candidate, new object[] { "A", capturedEpoch, pending.Task, runtime, source })!;
                    list.SelectedItem = devices[1]; var afterSelection = label.Text;
                    pending.SetResult(DesktopToolbox.DeskLink.Collaboration.PreviewFrame.Off("offline"));
                    if (await operation || label.Text != afterSelection) throw new InvalidOperationException("late A frame was applied after selection B");
                    list.SelectedItem = devices[0]; viewed.SetValue(candidate, "A"); enabled.SetValue(candidate, true);
                    capturedEpoch = (long)epoch.GetValue(candidate)!;
                    pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    operation = (Task<bool>)apply.Invoke(candidate, new object[] { "A", capturedEpoch, pending.Task, runtime, source })!;
                    enabled.SetValue(candidate, false); label.Text = "已关闭";
                    pending.SetResult(DesktopToolbox.DeskLink.Collaboration.PreviewFrame.Off("offline"));
                    if (await operation || label.Text != "已关闭") throw new InvalidOperationException("late frame was applied after view closed");
                    enabled.SetValue(candidate, true); viewed.SetValue(candidate, "A"); capturedEpoch = (long)epoch.GetValue(candidate)!;
                    pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    operation = (Task<bool>)apply.Invoke(candidate, new object[] { "A", capturedEpoch, pending.Task, runtime, source })!;
                    list.SelectedItem = devices[1]; list.SelectedItem = devices[0]; viewed.SetValue(candidate, "A");
                    var afterReturn = label.Text;
                    pending.SetResult(DesktopToolbox.DeskLink.Collaboration.PreviewFrame.Off("offline"));
                    if (await operation || label.Text != afterReturn) throw new InvalidOperationException("late frame survived selection A to B to A");
                    capturedEpoch = (long)epoch.GetValue(candidate)!;
                    pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    operation = (Task<bool>)apply.Invoke(candidate, new object[] { "A", capturedEpoch, pending.Task, runtime, source })!;
                    pending.SetResult(DesktopToolbox.DeskLink.Collaboration.PreviewFrame.Off("offline"));
                    if (!await operation || label.Text != "对方未连接") throw new InvalidOperationException("current frame was not applied");
                    File.WriteAllText(args[0], "selection preserved and late frames rejected after selection or disable"); Environment.Exit(0);
                }
                catch (Exception error) { File.WriteAllText(args[0], error.ToString()); Environment.Exit(1); }
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "blue-pages")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "blue-pages-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var stage = 0;
            timer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow > deadline) { File.WriteAllText(args[0], "blue pages timed out"); Environment.Exit(1); }
                var tabs = (System.Windows.Controls.TabControl)candidate.FindName("ModeTabs");
                if (!tabs.IsEnabled) return;
                var host = (FrameworkElement)candidate.FindName("HostSections");
                var peer = (FrameworkElement)candidate.FindName("PeerSections");
                if (stage == 0)
                {
                    var keyboard = FindButton(host, "键鼠");
                    if (keyboard is null) { File.WriteAllText(args[0], "host keyboard page missing"); Environment.Exit(1); return; }
                    stage = 1; keyboard.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); return;
                }
                if (stage == 1)
                {
                    if (candidate.FindName("HostKeyboardCard") is not FrameworkElement page || page.Visibility != Visibility.Visible || candidate.FindName("HostClipboard") is null)
                    { File.WriteAllText(args[0], "host keyboard controls missing"); Environment.Exit(1); return; }
                    tabs.SelectedIndex = 1; stage = 2; return;
                }
                if (stage == 2)
                {
                    var devices = FindButton(peer, "设备");
                    if (devices is null) { File.WriteAllText(args[0], "peer device page missing"); Environment.Exit(1); return; }
                    stage = 3; devices.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); return;
                }
                if (stage == 3)
                {
                    if (candidate.FindName("PeerSummaryCard") is not FrameworkElement page || page.Visibility != Visibility.Visible)
                    { File.WriteAllText(args[0], "peer device page not visible"); Environment.Exit(1); return; }
                    if (((FrameworkElement)candidate.FindName("PeerSummaryStartButton")).Visibility != Visibility.Visible ||
                        ((FrameworkElement)candidate.FindName("PeerSummaryStopButton")).Visibility != Visibility.Collapsed ||
                        ((FrameworkElement)candidate.FindName("PeerSummaryReturnButton")).Visibility != Visibility.Collapsed ||
                        ((FrameworkElement)candidate.FindName("PeerSummaryConfirmButton")).Visibility != Visibility.Collapsed)
                    { File.WriteAllText(args[0], "peer disconnected controls do not match actual state"); Environment.Exit(1); return; }
                    var settings = FindButton(peer, "设置与权限");
                    if (settings is null) { File.WriteAllText(args[0], "peer settings page missing"); Environment.Exit(1); return; }
                    stage = 4; settings.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); return;
                }
                var ok = candidate.FindName("PeerSettingsCard") is FrameworkElement settingsPage && settingsPage.Visibility == Visibility.Visible;
                File.WriteAllText(args[0], ok ? "host keyboard, peer devices and settings pages correct" : "peer settings not visible");
                Environment.Exit(ok ? 0 : 1);
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "blue-device-cards")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "blue-cards-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var devices = new[] {
                new DesktopToolbox.DeskLink.TrustedDeviceIdentity("Desk-A", new string('A', 64), "windows", 1),
                new DesktopToolbox.DeskLink.TrustedDeviceIdentity("Mac-B", new string('B', 64), "mac", 2)
            };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var stage = 0;
            timer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow > deadline) { File.WriteAllText(args[0], "device card test timed out"); Environment.Exit(1); }
                var tabs = (System.Windows.Controls.TabControl)candidate.FindName("ModeTabs");
                if (!tabs.IsEnabled) return;
                var list = (System.Windows.Controls.ListBox)candidate.FindName("HostDevices");
                if (stage == 0)
                {
                    list.ItemsSource = devices;
                    typeof(MainWindow).GetMethod("ShowSettingsPage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(candidate, new object[] { "HostDevicesCard" });
                    stage = 1; return;
                }
                if (stage == 1)
                {
                    candidate.UpdateLayout();
                    var item = list.ItemContainerGenerator.ContainerFromItem(devices[1]);
                    var firstItem = list.ItemContainerGenerator.ContainerFromItem(devices[0]);
                    var view = item is null ? null : FindButton(item, "查看画面");
                    var toggle = item is null ? null : FindButton(item, "切换到这台电脑");
                    var firstToggle = firstItem is null ? null : FindButton(firstItem, "切换到这台电脑");
                    if (view is null || toggle is null || firstToggle is null || toggle.IsEnabled)
                    { File.WriteAllText(args[0], "per-device actions missing or offline switch enabled"); Environment.Exit(1); return; }
                    candidate.SetValue(MainWindow.HostSharedRunningProperty, true);
                    candidate.HostKeyboardSnapshot = new DesktopToolbox.DeskLink.DeskLinkState
                    { ProcessState = DesktopToolbox.DeskLink.DeskLinkProcessState.Running, ConnectedComputers = new[] { "Mac-B" } };
                    candidate.UpdateLayout();
                    if (!toggle.IsEnabled || firstToggle.IsEnabled)
                    { File.WriteAllText(args[0], "switch availability did not follow individual device state"); Environment.Exit(1); return; }
                    candidate.HostKeyboardSnapshot = null;
                    candidate.UpdateLayout();
                    if (toggle.IsEnabled) { File.WriteAllText(args[0], "unknown device state allowed switching"); Environment.Exit(1); return; }
                    stage = 2;
                    view.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    return;
                }
                var ok = ReferenceEquals(list.SelectedItem, devices[1]) &&
                    ((FrameworkElement)candidate.FindName("HostPreviewCard")).Visibility == Visibility.Visible;
                File.WriteAllText(args[0], ok ? "device card selected the intended peer and opened preview" : "device card targeted wrong peer or page");
                Environment.Exit(ok ? 0 : 1);
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "blue-shell")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "blue-shell-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var stage = 0;
            timer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow > deadline) { File.WriteAllText(args[0], "blue shell timed out"); Environment.Exit(1); }
                var tabs = candidate.FindName("ModeTabs") as System.Windows.Controls.TabControl;
                if (tabs is null || !tabs.IsEnabled) return;
                var hostNav = candidate.FindName("HostSections") as System.Windows.Controls.WrapPanel;
                var peerNav = candidate.FindName("PeerSections") as System.Windows.Controls.WrapPanel;
                var hostRole = candidate.FindName("HostRoleButton") as System.Windows.Controls.Button;
                var peerRole = candidate.FindName("PeerRoleButton") as System.Windows.Controls.Button;
                if (hostNav is null || peerNav is null || hostRole is null || peerRole is null ||
                    hostNav.Orientation != System.Windows.Controls.Orientation.Vertical ||
                    peerNav.Orientation != System.Windows.Controls.Orientation.Vertical ||
                    System.Windows.Controls.DockPanel.GetDock((UIElement)VisualTreeHelper.GetParent(hostNav)) != System.Windows.Controls.Dock.Left ||
                    System.Windows.Controls.DockPanel.GetDock((UIElement)VisualTreeHelper.GetParent(peerNav)) != System.Windows.Controls.Dock.Left)
                { File.WriteAllText(args[0], "blue shell role controls or left navigation missing"); Environment.Exit(1); return; }
                if (stage == 0)
                {
                    if (((SolidColorBrush)hostRole.Background).Color != Colors.White || ((SolidColorBrush)peerRole.Background).Color == Colors.White)
                    { File.WriteAllText(args[0], "host role selection not visible"); Environment.Exit(1); return; }
                    stage = 1;
                    peerRole.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    return;
                }
                if (stage == 1)
                {
                    if (tabs.SelectedIndex != 1 || ((SolidColorBrush)peerRole.Background).Color != Colors.White || ((SolidColorBrush)hostRole.Background).Color == Colors.White)
                    { File.WriteAllText(args[0], "peer role switch or selection indicator failed"); Environment.Exit(1); return; }
                    stage = 2;
                    hostRole.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    return;
                }
                var ok = tabs.SelectedIndex == 0 && hostRole.IsEnabled && peerRole.IsEnabled && ((SolidColorBrush)hostRole.Background).Color == Colors.White;
                tabs.IsEnabled = false;
                ok = ok && !hostRole.IsEnabled && !peerRole.IsEnabled;
                tabs.IsEnabled = true;
                File.WriteAllText(args[0], ok ? "left navigation and both role switches correct" : "host role switch failed");
                Environment.Exit(ok ? 0 : 1);
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "workflow")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "workflow-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var deadline = DateTime.UtcNow.AddSeconds(20);
            var started = false;
            timer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow > deadline) { File.WriteAllText(args[0], "workflow timed out"); Environment.Exit(1); }
                if (!((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).IsEnabled) return;
                bool Visible(string name) => ((FrameworkElement)candidate.FindName(name)).Visibility == Visibility.Visible;
                var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var pairing = typeof(MainWindow).GetField("pairing", fields)!;
                if (!started)
                {
                    if (!Visible("HostPairingPrepare") || Visible("HostPairingConfirm") || !Visible("PeerPairingImport"))
                    { File.WriteAllText(args[0], "invalid initial step"); Environment.Exit(1); return; }
                    started = true;
                    (FindButton(candidate, "准备主控") ?? throw new InvalidOperationException("prepare missing")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    return;
                }
                if (pairing.GetValue(candidate) is null) return;
                if (!Visible("HostPairingImport") || Visible("HostPairingConfirm"))
                { File.WriteAllText(args[0], "invalid import step"); Environment.Exit(1); return; }
                var offerField = typeof(MainWindow).GetField("offer", fields)!;
                offerField.SetValue(candidate, new DesktopToolbox.DeskLink.MultiPeerPairingPreview("id", "Test-Mac", "mac", new string('A', 64), DateTimeOffset.UtcNow.AddMinutes(5)));
                var refresh = typeof(MainWindow).GetMethod("RefreshPairingGuide", fields)!;
                refresh.Invoke(candidate, null);
                if (!Visible("HostPairingConfirm") || Visible("HostPairingImport"))
                { File.WriteAllText(args[0], "invalid review step"); Environment.Exit(1); return; }
                offerField.SetValue(candidate, null);
                typeof(MainWindow).GetField("unsavedInvitation", fields)!.SetValue(candidate, new DesktopToolbox.DeskLink.MultiPeerInvitation
                { Host = "127.0.0.1", HostName = "Host", PeerName = "Peer", CertificateSha256 = new string('A', 64), Token = "test" });
                refresh.Invoke(candidate, null);
                if (!Visible("HostPairingSave") || Visible("HostPairingConfirm"))
                { File.WriteAllText(args[0], "unsaved invitation not recoverable"); Environment.Exit(1); return; }
                typeof(MainWindow).GetField("unsavedInvitation", fields)!.SetValue(candidate, null);
                typeof(MainWindow).GetField("pairingFileSaved", fields)!.SetValue(candidate, true);
                typeof(MainWindow).GetField("hostPreview", fields)!.SetValue(candidate, new DesktopToolbox.DeskLink.MultiPeerHostPreview("id", "Host", "127.0.0.1", new string('B', 64)));
                refresh.Invoke(candidate, null);
                var ok = Visible("HostPairingDone") && Visible("PeerPairingConfirm") && !Visible("PeerPairingImport");
                File.WriteAllText(args[0], ok ? "wizard transitions correct" : "invalid completion step");
                Environment.Exit(ok ? 0 : 1);
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "presentation")
        {
            var converter = new DeviceConnectionConverter();
            var switchAvailable = new DeviceSwitchAvailableConverter();
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var connected = new DesktopToolbox.DeskLink.DeskLinkState { ProcessState = DesktopToolbox.DeskLink.DeskLinkProcessState.Running, NativeControlAvailable = true, ConnectedComputers = new[] { "Mac-A" } };
            var unavailableEmpty = connected with { NativeControlAvailable = false, ConnectedComputers = Array.Empty<string>() };
            var unavailableStale = connected with { NativeControlAvailable = false };
            var stopped = new DesktopToolbox.DeskLink.DeskLinkState { ProcessState = DesktopToolbox.DeskLink.DeskLinkProcessState.Stopped };
            var starting = unavailableStale with { ProcessState = DesktopToolbox.DeskLink.DeskLinkProcessState.Starting };
            var stopping = unavailableStale with { ProcessState = DesktopToolbox.DeskLink.DeskLinkProcessState.Stopping };
            var faulted = unavailableStale with { ProcessState = DesktopToolbox.DeskLink.DeskLinkProcessState.Faulted };
            var ok = Equals(converter.Convert(new object[] { "Mac-A", starting }, typeof(string), null!, culture), "连接状态更新中") &&
                     Equals(converter.Convert(new object[] { "Mac-A", stopping }, typeof(string), null!, culture), "连接状态更新中") &&
                     Equals(converter.Convert(new object[] { "Mac-A", faulted }, typeof(string), null!, culture), "状态未知") &&
                     Equals(switchAvailable.Convert(new object[] { "Mac-A", starting, true }, typeof(bool), null!, culture), false) &&
                     Equals(switchAvailable.Convert(new object[] { "Mac-A", stopping, true }, typeof(bool), null!, culture), false) &&
                     Equals(switchAvailable.Convert(new object[] { "Mac-A", faulted, true }, typeof(bool), null!, culture), false) && Equals(converter.Convert(new object[] { "Mac-A", unavailableEmpty }, typeof(string), null!, culture), "状态未知") &&
                     Equals(converter.Convert(new object[] { "Mac-A", unavailableStale }, typeof(string), null!, culture), "状态未知") &&
                     Equals(switchAvailable.Convert(new object[] { "Mac-A", unavailableStale, true }, typeof(bool), null!, culture), false) &&
                     Equals(converter.Convert(new object[] { "Mac-A", connected }, typeof(string), null!, culture), "● 已连接") &&
                     Equals(converter.Convert(new object[] { "Mac-B", connected }, typeof(string), null!, culture), "未连接") &&
                     Equals(converter.Convert(new object[] { "Mac-A", stopped }, typeof(string), null!, culture), "未连接") &&
                     Equals(converter.Convert(new object[] { "Mac-A", DependencyProperty.UnsetValue }, typeof(string), null!, culture), "状态未知") &&
                     Equals(switchAvailable.Convert(new object[] { "Mac-A", connected, true }, typeof(bool), null!, culture), true) &&
                     Equals(switchAvailable.Convert(new object[] { "Mac-B", connected, true }, typeof(bool), null!, culture), false) &&
                     Equals(switchAvailable.Convert(new object[] { "Mac-A", stopped, true }, typeof(bool), null!, culture), false) &&
                     Equals(switchAvailable.Convert(new object[] { "Mac-A", connected, false }, typeof(bool), null!, culture), false) &&
                     Equals(switchAvailable.Convert(new object[] { "Mac-A", DependencyProperty.UnsetValue, true }, typeof(bool), null!, culture), false) &&
                     UserErrorMessage.For(new System.Net.Sockets.SocketException(10065)).Contains("网络") &&
                     UserErrorMessage.For(new InvalidOperationException("请先选择设备。")).Contains("选择设备") &&
                     !UserErrorMessage.For(new Exception("sensitive technical details")).Contains("sensitive");
            File.WriteAllText(args[0], ok ? "device and error presentation correct" : "presentation incorrect");
            Environment.Exit(ok ? 0 : 1);
        }
        if (args.Length == 2 && args[1] == "presentation-main")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "unused-presentation-profile");
            if (Directory.Exists(profile) || File.Exists(profile)) throw new InvalidOperationException("Synthetic profile path already exists.");
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe")) { ShowInTaskbar=false };
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var runtime = (DesktopToolbox.DeskLink.MultiPeerRuntime)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DesktopToolbox.DeskLink.MultiPeerRuntime));
            typeof(DesktopToolbox.DeskLink.MultiPeerRuntime).GetField("publishedDevices",flags)!.SetValue(runtime,
                new[] { new DesktopToolbox.DeskLink.MultiPeerDeviceEndpoint("Mac-A","mac",0) });
            var devices = (System.Windows.Controls.ListBox)candidate.FindName("HostDevices");
            var identity = new DesktopToolbox.DeskLink.TrustedDeviceIdentity("Mac-A",new string('A',64));
            devices.Items.Add(identity); devices.SelectedItem=identity;
            typeof(MainWindow).GetField("host",flags)!.SetValue(candidate,runtime);
            var button=(System.Windows.Controls.Button)candidate.FindName("HostSwitchButton");
            var returnButton=(System.Windows.Controls.Button)candidate.FindName("HostReturnButton");
            var refresh=typeof(MainWindow).GetMethod("RefreshDeviceControls",flags)!;
            var running = new DesktopToolbox.DeskLink.DeskLinkState {
                ProcessState = DesktopToolbox.DeskLink.DeskLinkProcessState.Running,
                NativeControlAvailable = false, ConnectedComputers = new[] {"Mac-A"} };
            candidate.HostKeyboardSnapshot=running; refresh.Invoke(candidate,null);
            if (button.IsEnabled || !returnButton.IsEnabled) throw new InvalidOperationException("Unavailable evidence must disable switch while retaining return.");
            candidate.HostKeyboardSnapshot=running with {ConnectedComputers=Array.Empty<string>()}; refresh.Invoke(candidate,null);
            if (button.IsEnabled || !returnButton.IsEnabled) throw new InvalidOperationException("Empty unavailable evidence must retain safety controls.");
            candidate.HostKeyboardSnapshot=running with {NativeControlAvailable=true}; refresh.Invoke(candidate,null);
            if (!button.IsEnabled) throw new InvalidOperationException("Freshly confirmed device must remain switchable.");
            foreach(var process in new[] {DesktopToolbox.DeskLink.DeskLinkProcessState.Stopped, DesktopToolbox.DeskLink.DeskLinkProcessState.Starting, DesktopToolbox.DeskLink.DeskLinkProcessState.Stopping, DesktopToolbox.DeskLink.DeskLinkProcessState.Faulted})
            {
                candidate.HostKeyboardSnapshot=running with {ProcessState=process,NativeControlAvailable=true};
                refresh.Invoke(candidate,null); if(button.IsEnabled)throw new InvalidOperationException("Non-running process must not allow switching.");
            }
            if (Directory.Exists(profile) || File.Exists(profile)) throw new InvalidOperationException("Fixture unexpectedly created a profile.");
            File.WriteAllText(args[0],"real main window selected controls correct"); Environment.Exit(0);
        }
        if (args.Length == 2 && args[1] == "controls")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "controls-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                if (!((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).IsEnabled) return;
                var start = candidate.FindName("HostStartButton") as System.Windows.Controls.Button;
                var stop = candidate.FindName("HostStopButton") as System.Windows.Controls.Button;
                var selected = candidate.FindName("HostSelectedActions") as FrameworkElement;
                var empty = candidate.FindName("HostEmptyState") as FrameworkElement;
                var ok = start is not null && stop is not null && selected is not null && empty is not null &&
                    start.Visibility == Visibility.Visible && stop.Visibility == Visibility.Collapsed &&
                    selected.Visibility == Visibility.Collapsed && empty.Visibility == Visibility.Visible;
                File.WriteAllText(args[0], ok ? "empty state and actions correct" : "empty state or actions incorrect");
                Environment.Exit(ok ? 0 : 1);
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "pages")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "pages-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var deadline = DateTime.UtcNow.AddSeconds(8);
            var clicked = false;
            timer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow > deadline) { File.WriteAllText(args[0], "pages timed out"); Environment.Exit(1); }
                if (!((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).IsEnabled) return;
                if (!clicked)
                {
                    var button = FindButton(candidate, "文件");
                    if (button is null) { File.WriteAllText(args[0], "file navigation missing"); Environment.Exit(1); return; }
                    clicked = true; button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); return;
                }
                var files = (FrameworkElement)candidate.FindName("HostFilesCard");
                var connection = (FrameworkElement)candidate.FindName("HostConnectionCard");
                var ok = files.Visibility == Visibility.Visible && connection.Visibility == Visibility.Collapsed;
                File.WriteAllText(args[0], ok ? "only selected page visible" : "multiple sections still visible");
                Environment.Exit(ok ? 0 : 1);
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "navigation")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "navigation-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var deadline = DateTime.UtcNow.AddSeconds(8);
            var stage = 0;
            timer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow > deadline) { File.WriteAllText(args[0], "navigation timed out"); Environment.Exit(1); }
                if (!((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).IsEnabled) return;
                if (stage == 0)
                {
                    stage = 1;
                    (FindButton(candidate, "设备") ?? throw new InvalidOperationException("device navigation missing")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    return;
                }
                if (stage == 1)
                {
                    if (((FrameworkElement)candidate.FindName("HostDevicesCard")).Visibility != Visibility.Visible || ((FrameworkElement)candidate.FindName("HostConnectionCard")).Visibility != Visibility.Collapsed)
                    { File.WriteAllText(args[0], "host navigation failed"); Environment.Exit(1); return; }
                    ((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).SelectedIndex = 1;
                    stage = 2; return;
                }
                if (stage == 2)
                {
                    var peer = (FrameworkElement)candidate.FindName("PeerSections");
                    (FindButton(peer, "文件") ?? throw new InvalidOperationException("peer file navigation missing")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    stage = 3; return;
                }
                var ok = ((FrameworkElement)candidate.FindName("PeerFilesCard")).Visibility == Visibility.Visible &&
                         ((FrameworkElement)candidate.FindName("PeerConnectionCard")).Visibility == Visibility.Collapsed;
                File.WriteAllText(args[0], ok ? "host and peer navigation correct" : "peer navigation failed");
                Environment.Exit(ok ? 0 : 1);
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "aliases")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "alias-profile-" + Guid.NewGuid().ToString("N"));
            var key = "peer:" + new string('A', 64);
            var known = new Dictionary<string, string> { ["local"] = "PC-AAAAAAAAAAAA", [key] = "PC-BBBBBBBBBBBB" };
            var store = DeviceAliasStore.OpenAsync(profile).GetAwaiter().GetResult();
            var other = DeviceAliasStore.OpenAsync(profile).GetAwaiter().GetResult();
            store.RenameAsync("local", "台式机", known).GetAwaiter().GetResult();
            var rejected = false;
            try { other.RenameAsync(key, "台式机", known).GetAwaiter().GetResult(); }
            catch (ArgumentException) { rejected = true; }
            other.RenameAsync(key, "Mac 工作室", known).GetAwaiter().GetResult();
            var reopened = DeviceAliasStore.OpenAsync(profile).GetAwaiter().GetResult();
            var ok = rejected && reopened.Names.Resolve("local", known["local"]) == "台式机" &&
                reopened.Names.Resolve(key, known[key]) == "Mac 工作室" && known[key] == "PC-BBBBBBBBBBBB";
            File.WriteAllText(args[0], ok ? "saved and reopened" : "wrong aliases");
            Environment.Exit(ok ? 0 : 1);
        }
        if (args.Length == 2 && args[1] == "alias-list")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "alias-list-profile-" + Guid.NewGuid().ToString("N"));
            var fingerprint = new string('A', 64);
            var vault = DesktopToolbox.DeskLink.MultiDeviceCredentialVault.OpenAsync(
                Path.Combine(profile, "host", "trust"), new NianLink.Windows.WindowsCredentialProtector()).GetAwaiter().GetResult();
            vault.AddConfirmedWithSlotAsync("PC-BBBBBBBBBBBB", fingerprint, "mac").GetAwaiter().GetResult();
            vault.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var aliases = DeviceAliasStore.OpenAsync(profile).GetAwaiter().GetResult();
            aliases.RenameAsync("peer:" + fingerprint, "工作 Mac", new Dictionary<string, string>
            { ["local"] = "PC-AAAAAAAAAAAA", ["peer:" + fingerprint] = "PC-BBBBBBBBBBBB" }).GetAwaiter().GetResult();
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var deadline = DateTime.UtcNow.AddSeconds(12);
            var started = false;
            timer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow > deadline) { File.WriteAllText(args[0], "timeout: " + ((System.Windows.Controls.TextBlock)candidate.FindName("Feedback")).Text); Environment.Exit(1); }
                if (!((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).IsEnabled) return;
                if (!started)
                {
                    started = true;
                    var prepare = FindButton(candidate, "准备主控") ?? throw new InvalidOperationException("Prepare button missing");
                    prepare.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    return;
                }
                var list = (System.Windows.Controls.ListBox)candidate.FindName("HostDevices");
                if (list.Items.Count != 1) return;
                var device = (DesktopToolbox.DeskLink.TrustedDeviceIdentity)list.Items[0];
                list.SelectedItem = device;
                var converter = (DeviceLabelConverter)candidate.Resources["DeviceLabelConverter"];
                var label = converter.Convert(device.Name, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture);
                var ok = device.Name == "PC-BBBBBBBBBBBB" && Equals(label, "工作 Mac") && ReferenceEquals(list.SelectedItem, device) &&
                    ((FrameworkElement)candidate.FindName("HostSelectedActions")).Visibility == Visibility.Visible &&
                    !((System.Windows.Controls.Button)candidate.FindName("HostSwitchButton")).IsEnabled;
                File.WriteAllText(args[0], ok ? "friendly label with stable identity" : "wrong list identity");
                Environment.Exit(ok ? 0 : 1);
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "onboarding")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "onboarding-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                if (!((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).IsEnabled) return;
                var prepare = (System.Windows.Controls.Button)candidate.FindName("HostPairingPrepare");
                var confirmation = (System.Windows.Controls.Button)candidate.FindName("HostPairingConfirm");
                var guide = (System.Windows.Controls.TextBlock)candidate.FindName("HostPairingStep");
                var ok = prepare.Visibility == Visibility.Visible && confirmation.Visibility == Visibility.Collapsed && guide.Text.Contains("准备主控");
                File.WriteAllText(args[0], ok ? "prepare before pairing" : "onboarding step incorrect");
                Environment.Exit(ok ? 0 : 1);
            };
            timer.Start(); app.Run(candidate); return;
        }
        if (args.Length == 2 && args[1] == "close")
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "close-profile-" + Guid.NewGuid().ToString("N"));
            var candidate = new MainWindow(profile, Path.Combine(profile, "not-running.exe"))
            { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            var ready = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            candidate.Closed += (_, _) => { ready.Stop(); timeout.Stop(); File.WriteAllText(args[0], "closed"); app.Shutdown(); };
            ready.Tick += (_, _) =>
            {
                if (!((System.Windows.Controls.TabControl)candidate.FindName("ModeTabs")).IsEnabled) return;
                ready.Stop(); candidate.Close();
            };
            timeout.Tick += (_, _) =>
            {
                var feedback = ((System.Windows.Controls.TextBlock)candidate.FindName("Feedback")).Text;
                File.WriteAllText(args[0], "failed: " + feedback);
                Environment.Exit(1);
            };
            ready.Start(); timeout.Start(); app.Run(candidate); return;
        }
        var window = new MainWindow(Path.Combine(Path.GetTempPath(), "nian-link-render-only"), Path.Combine(Path.GetTempPath(), "not-running.exe"));
        if (args.Length == 2 && args[1] == "showcase-devices")
        {
            window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000; window.Top = -10000; window.Show();
            var devices = (System.Windows.Controls.ListBox)window.FindName("HostDevices");
            var mac = new DesktopToolbox.DeskLink.TrustedDeviceIdentity("工作 Mac", new string('A', 64), "mac", 1);
            var pc = new DesktopToolbox.DeskLink.TrustedDeviceIdentity("笔记本", new string('B', 64), "windows", 2);
            devices.ItemsSource = new[] { mac, pc };
            window.HostKeyboardSnapshot = new DesktopToolbox.DeskLink.DeskLinkState { ProcessState = DesktopToolbox.DeskLink.DeskLinkProcessState.Stopped };
            devices.SelectedItem = mac;
            var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(MainWindow).GetMethod("RefreshDeviceControls", fields)!.Invoke(window, null);
            typeof(MainWindow).GetMethod("ShowSettingsPage", fields)!.Invoke(window, new object[] { "HostDevicesCard" });
            ((System.Windows.Controls.TabControl)window.FindName("ModeTabs")).IsEnabled = true;
            var visual = (FrameworkElement)window.Content;
            visual.UpdateLayout();
            var preview = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            preview.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(preview));
            using var output = new FileStream(args[0], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            png.Save(output); app.Shutdown(); return;
        }
        if (args.Length == 2 && args[1] == "showcase")
        {
            ((System.Windows.Controls.TextBlock)window.FindName("HostDisplayName")).Text = "台式机";
            ((System.Windows.Controls.Button)window.FindName("MacExportButton")).IsEnabled = true;
            ((System.Windows.Controls.TextBlock)window.FindName("MacExportStatus")).Text = "适用于 Apple 芯片 Mac";
            var address = (System.Windows.Controls.ComboBox)window.FindName("HostAddress");
            address.ItemsSource = new[] { "已连接局域网" }; address.SelectedIndex = 0;
        }
        if (args.Length == 2 && args[1] == "client") ((System.Windows.Controls.TabControl)window.FindName("ModeTabs")).SelectedIndex = 1;
        if (args.Length == 2 && args[1] is "blue-screen" or "blue-screen-narrow" or "blue-screen-wide")
            typeof(MainWindow).GetMethod("ShowSettingsPage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, new object[] { "HostPreviewCard" });
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        content.Resources.MergedDictionaries.Add(window.Resources);
        var narrow = args.Length == 2 && args[1] is "narrow" or "narrow-hidpi" or "blue-screen-narrow";
        var width = args.Length == 2 && args[1] == "blue-screen-wide" ? 980 : narrow ? 620 : 760;
        var height = narrow ? 580 : 740;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        if (args.Length == 2 && args[1] == "display")
        {
            ((System.Windows.Controls.ScrollViewer)window.FindName("HostScroll")).ScrollToVerticalOffset(700);
            content.UpdateLayout();
        }
        var scale = args.Length == 2 && args[1] == "narrow-hidpi" ? 1.5 : 1.0;
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = new FileStream(args[0], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(file);
        app.Shutdown();
    }

    private static System.Windows.Controls.Button? FindButton(DependencyObject node, string label)
    {
        if (node is System.Windows.Controls.Button button && Equals(button.Content, label)) return button;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var found = FindButton(VisualTreeHelper.GetChild(node, i), label);
            if (found is not null) return found;
        }
        return null;
    }

    private static List<string> CollectText(DependencyObject node)
    {
        var values = new List<string>();
        if (node is System.Windows.Controls.TextBlock text) values.Add(text.Text);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) values.AddRange(CollectText(VisualTreeHelper.GetChild(node, i)));
        return values;
    }
}
