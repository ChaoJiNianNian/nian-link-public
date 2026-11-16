using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopToolbox.DeskLink;
using NianLink.MultiDevice;
using NianLink.CandidateDiscovery;
using System.Runtime.CompilerServices;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || !Path.IsPathFullyQualified(args[0]) ||
            args.Length == 2 && args[1] is not ("--invalid-promotion" or "--delivery-drain" or "--aborted-cleanup" or "--completed-awaiting-host" or "--pairing-status" or "--pairing-diagnostics" or "--no-pairing-diagnostics"))
            throw new ArgumentException("An absolute result path is required.");
        var output = args[0];
        var profile = Path.Combine(Path.GetDirectoryName(output)!, "fileless-ui-" + Guid.NewGuid().ToString("N"));
        var invalidPromotion = args.Length == 2 && args[1] == "--invalid-promotion";
        var deliveryDrain = args.Length == 2 && args[1] == "--delivery-drain";
        var abortedCleanup = args.Length == 2 && args[1] == "--aborted-cleanup";
        var completedAwaitingHost = args.Length == 2 && args[1] == "--completed-awaiting-host";
        var pairingStatus = args.Length == 2 && args[1] == "--pairing-status";
        var diagnosticsExpected = args.Length == 2 && args[1] == "--pairing-diagnostics";
        var diagnosticsAbsent = args.Length == 2 && args[1] == "--no-pairing-diagnostics";
        if (invalidPromotion)
        {
            var trust = Path.Combine(profile, "client", "trust");
            Directory.CreateDirectory(trust);
            File.WriteAllBytes(Path.Combine(trust, "peer-promotion.bin"), [1, 2, 3]);
        }
        var app = new Application();
        var window = new MainWindow(profile, Path.Combine(profile, "no-engine.exe"))
        { ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var phase = 0;
        timer.Tick += async (_, _) =>
        {
            try
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("UI fixture timed out.");
                if (!((TabControl)window.FindName("ModeTabs")).IsEnabled) return;
                if (diagnosticsExpected || diagnosticsAbsent)
                {
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var type = typeof(MainWindow);
                    var field = type.GetField("peerPairingDiagnostics", flags);
                    if (diagnosticsAbsent)
                    {
                        if (field is not null || typeof(CandidateDiscoveryRun).GetMethod("GetPairingDiagnostics") is not null)
                            throw new InvalidOperationException("Unexpected diagnostics in ordinary build.");
                        File.WriteAllText(output, "diagnostics absent");
                        Environment.Exit(0);
                    }
                    ((TabControl)window.FindName("ModeTabs")).SelectedIndex = 1;
                    typeof(MainWindow).GetMethod("ShowSettingsPage", flags)!.Invoke(window, ["PeerConnectionCard"]);
                    await Task.Delay(100);
                    var text = field?.GetValue(window) as TextBlock ?? throw new InvalidOperationException("Diagnostic text was not added.");
                    var status = (TextBlock)window.FindName("PeerDiscoveryStatus");
                    if (!ReferenceEquals(text.Parent, status.Parent) || !text.IsVisible || !text.Text.Contains("测试诊断", StringComparison.Ordinal))
                        throw new InvalidOperationException("Diagnostic text is not in the responder panel.");
                    var run = RuntimeHelpers.GetUninitializedObject(typeof(CandidateDiscoveryRun));
                    typeof(CandidateDiscoveryRun).GetField("pairingDiagnosticGate", flags)!.SetValue(run, new object());
                    typeof(CandidateDiscoveryRun).GetField("diagnosticObserved", flags)!.SetValue(run, 2);
                    typeof(CandidateDiscoveryRun).GetField("diagnosticRejected", flags)!.SetValue(run, 1);
                    typeof(CandidateDiscoveryRun).GetField("diagnosticAccepted", flags)!.SetValue(run, 1);
                    type.GetField("discoveryIsScan", flags)!.SetValue(window, false);
                    var finish = type.GetMethod("FinishPairingDiagnostics", flags)!;
                    var epoch = (long)type.GetField("discoveryUiEpoch", flags)!.GetValue(window)!;
                    finish.Invoke(window, [run, epoch]);
                    if (!text.Text.Contains("单播数据 2 条", StringComparison.Ordinal) || !text.Text.Contains("受理 1 条", StringComparison.Ordinal))
                        throw new InvalidOperationException("Final counters not shown.");
                    typeof(CandidateDiscoveryRun).GetField("diagnosticObserved", flags)!.SetValue(run, 3);
                    typeof(CandidateDiscoveryRun).GetField("diagnosticRejected", flags)!.SetValue(run, 2);
                    finish.Invoke(window, [run, epoch]);
                    if (!text.Text.Contains("单播数据 3 条", StringComparison.Ordinal)) throw new InvalidOperationException("Late counter not refreshed.");
                    text.Text = "new session";
                    finish.Invoke(window, [run, epoch - 1]);
                    if (text.Text != "new session") throw new InvalidOperationException("Old stop overwrote newer session.");
                    File.WriteAllText(output, "diagnostics displayed; final snapshot and epoch protected");
                    Environment.Exit(0);
                }
                if (pairingStatus)
                {
                    if (phase != 0) return;
                    phase = 3;
                    FindButton(window, "发现与配对")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var type = typeof(MainWindow);
                    foreach (var name in new[] { "host", "pairing", "changes" })
                    {
                        var field = type.GetField(name, flags) ?? throw new InvalidOperationException(name + " missing");
                        field.SetValue(window, RuntimeHelpers.GetUninitializedObject(field.FieldType));
                    }
                    var lifetime = type.GetField("discoveryLifetime", flags)!.GetValue(window)!;
                    var current = lifetime.GetType().GetField("current", flags)!;
                    var choices = (ListBox)window.FindName("DiscoveryChoices");
                    var choice = new CandidateDiscoveryChoice(Guid.NewGuid(), "test device", "windows");
                    choices.ItemsSource = new[] { choice };
                    choices.SelectedItem = choice;
                    type.GetField("discoveryIsScan", flags)!.SetValue(window, true);
                    var syntheticRun = RuntimeHelpers.GetUninitializedObject(typeof(CandidateDiscoveryRun));
                    typeof(CandidateDiscoveryRun).GetField("<Completion>k__BackingField", flags)!.SetValue(syntheticRun, Task.Delay(TimeSpan.FromMinutes(1)));
                    current.SetValue(lifetime, syntheticRun);
                    var startMethod = type.GetMethod("StartFilelessPairing_Click", flags)!;
                    startMethod.Invoke(window, [window, new RoutedEventArgs()]);
                    current.SetValue(lifetime, null);
                    await Task.Delay(100);
                    var status = ((TextBlock)window.FindName("HostDiscoveryStatus")).Text;
                    var feedbackText = ((TextBlock)window.FindName("Feedback")).Text;
                    if (status.Contains("正在请求", StringComparison.Ordinal) || !feedbackText.Contains("未完成", StringComparison.Ordinal))
                        throw new InvalidOperationException("Failed pairing left an active-looking status: " + status + "; " + feedbackText);
                    var finish = type.GetMethod("FinishFilelessPairingStatus", flags) ?? throw new InvalidOperationException("Status completion missing.");
                    var discoveryField = type.GetField("discoveryUiEpoch", flags)!;
                    var sessionField = type.GetField("filelessEpoch", flags)!;
                    var outcomeField = type.GetField("filelessDeliveryOutcome", flags)!;
                    var hostStatus = (TextBlock)window.FindName("HostDiscoveryStatus");
                    void Finish(bool hostSide, long discovery, long session, bool recoveryPending = false) =>
                        finish.Invoke(window, [hostSide, discovery, session, recoveryPending]);
                    void Outcome(string name) => outcomeField.SetValue(window, Enum.Parse(outcomeField.FieldType, name));
                    var discovery = (long)discoveryField.GetValue(window)!;
                    var session = (long)sessionField.GetValue(window)!;
                    hostStatus.Text = "双方已确认，正在完成配对…";
                    Outcome("Unknown");
                    Finish(true, discovery, session);
                    if (!hostStatus.Text.Contains("待确认", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unknown delivery was misreported as cancelled.");
                    hostStatus.Text = "正在请求对方确认…";
                    Outcome("None");
                    Finish(true, discovery, session, recoveryPending: true);
                    if (!hostStatus.Text.Contains("未完成的配对", StringComparison.Ordinal))
                        throw new InvalidOperationException("An unfinished earlier pairing was misreported as cancelled.");
                    hostStatus.Text = "正在请求对方确认…";
                    Outcome("None");
                    Finish(true, discovery - 1, session);
                    if (hostStatus.Text != "正在请求对方确认…")
                        throw new InvalidOperationException("Old discovery completion changed current status.");
                    Finish(true, discovery, session - 1);
                    if (hostStatus.Text != "正在请求对方确认…")
                        throw new InvalidOperationException("Old session completion changed current status.");
                    hostStatus.Text = "正在扫描，尚未发现设备。";
                    Finish(true, discovery, session);
                    if (hostStatus.Text != "正在扫描，尚未发现设备。")
                        throw new InvalidOperationException("Pairing cleanup changed current scan status.");
                    hostStatus.Text = "双方已确认，正在完成配对…";
                    ((TextBlock)window.FindName("Feedback")).Text = "设备已配对，共享已重启。";
                    Outcome("Completed");
                    Finish(true, discovery, session);
                    if (!hostStatus.Text.Contains("设备已配对", StringComparison.Ordinal))
                        throw new InvalidOperationException("Completed delivery was not preserved.");
                    var activeDelivery = type.GetField("filelessDeliveryFinished", flags)!;
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    activeDelivery.SetValue(window, completion);
                    Outcome("None");
                    type.GetMethod("CancelFilelessPairing_Click", flags)!.Invoke(window, [window, new RoutedEventArgs()]);
                    hostStatus.Text = "正在扫描，尚未发现设备。";
                    sessionField.SetValue(window, (long)sessionField.GetValue(window)! + 1);
                    activeDelivery.SetValue(window, null);
                    completion.SetResult();
                    await Task.Delay(100);
                    if (hostStatus.Text != "正在扫描，尚未发现设备。")
                        throw new InvalidOperationException("An older cancellation overwrote the new session status.");
                    type.GetMethod("CancelFilelessPairing_Click", flags)!.Invoke(window, [window, new RoutedEventArgs()]);
                    await Task.Delay(100);
                    if (!hostStatus.Text.Contains("本次配对尝试已停止", StringComparison.Ordinal) ||
                        !hostStatus.Text.Contains("未完成的配对", StringComparison.Ordinal))
                        throw new InvalidOperationException("Cancellation did not preserve the possibility of an earlier unfinished pairing.");
                    ((TabControl)window.FindName("ModeTabs")).SelectedIndex = 1;
                    await Task.Delay(100);
                    ((FrameworkElement)window.FindName("PeerConnectionCard")).Visibility = Visibility.Visible;
                    discovery = (long)discoveryField.GetValue(window)!;
                    session = (long)sessionField.GetValue(window)!;
                    var peerStatus = (TextBlock)window.FindName("PeerDiscoveryStatus");
                    peerStatus.Text = "正在建立安全连接…";
                    Outcome("None");
                    Finish(false, discovery, session);
                    if (!peerStatus.Text.Contains("未完成", StringComparison.Ordinal) || !peerStatus.Text.Contains("重新开启发现", StringComparison.Ordinal))
                        throw new InvalidOperationException("Peer cancellation left an active-looking status.");
                    peerStatus.Text = "双方已确认，正在完成配对…";
                    Outcome("Unknown");
                    Finish(false, discovery, session);
                    if (!peerStatus.Text.Contains("待确认", StringComparison.Ordinal))
                        throw new InvalidOperationException("Peer unknown delivery was misreported.");
                    File.WriteAllText(output, "failed pairing clears status; unknown, success, stale epochs and newer scan remain safe on host and peer");
                    Environment.Exit(0);
                }
                if (completedAwaitingHost)
                {
                    if (phase != 0) return;
                    phase = 3;
                    var method = typeof(MainWindow).GetMethod("RecoveryMessage", BindingFlags.Static | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("Recovery message missing.");
                    var message = (string)(method.Invoke(null, [PairingRecoveryPhase.CompletedAwaitingHost])
                        ?? throw new InvalidOperationException("Recovery message missing."));
                    if (!message.Contains("主控", StringComparison.Ordinal) || !message.Contains("核对", StringComparison.Ordinal) ||
                        message.Contains("损坏", StringComparison.Ordinal) || message.Contains("已配对", StringComparison.Ordinal))
                        throw new InvalidOperationException("Completed-awaiting-host did not guide authenticated recovery: " + message);
                    File.WriteAllText(output, "completed-awaiting-host guides renewed host authentication without claiming completion");
                    Environment.Exit(0);
                }
                if (abortedCleanup)
                {
                    if (phase != 0) return;
                    phase = 3;
                    var method = typeof(MainWindow).GetMethod("ConfirmAbortedCleanupAsync", BindingFlags.Static | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("Aborted cleanup confirmation missing.");
                    var calls = 0;
                    Func<Task<bool>> cleanup = () => { calls++; return Task.FromResult(true); };
                    async Task<string> Run(Func<bool> confirm, Func<Task<bool>> action) => await (Task<string>)
                        (method.Invoke(null, [confirm, action]) ?? throw new InvalidOperationException("Cleanup did not start."));
                    var declined = await Run(() => false, cleanup);
                    if (calls != 0 || !declined.Contains("保留", StringComparison.Ordinal))
                        throw new InvalidOperationException("Declining cleanup modified records.");
                    var acceptedCleanup = await Run(() => true, cleanup);
                    if (calls != 1 || !acceptedCleanup.Contains("取消", StringComparison.Ordinal) || acceptedCleanup.Contains("已配对", StringComparison.Ordinal))
                        throw new InvalidOperationException("Accepted aborted cleanup was misreported.");
                    var changed = await Run(() => true, () => Task.FromResult(false));
                    if (!changed.Contains("变化", StringComparison.Ordinal))
                        throw new InvalidOperationException("Changed evidence was not reported.");
                    File.WriteAllText(output, "aborted cleanup requires confirmation, preserves aborted outcome and reports changed evidence");
                    Environment.Exit(0);
                }
                if (deliveryDrain)
                {
                    if (phase != 0) return;
                    phase = 3;
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var active = typeof(MainWindow).GetField("filelessDeliveryFinished", flags)
                        ?? throw new InvalidOperationException("Delivery lifetime missing.");
                    active.SetValue(window, completion);
                    var ran = false;
                    var work = typeof(MainWindow).GetMethod("WorkAsync", flags)
                        ?? throw new InvalidOperationException("Work gate missing.");
                    await (Task)(work.Invoke(window, [new Func<Task>(() => { ran = true; return Task.CompletedTask; })])
                        ?? throw new InvalidOperationException("Work gate did not run."));
                    if (ran) throw new InvalidOperationException("Other work ran during a pending pairing transaction.");
                    var stop = typeof(MainWindow).GetMethod("StopFilelessPairingUiAsync", flags)
                        ?? throw new InvalidOperationException("Stop method missing.");
                    var stopped = (Task)(stop.Invoke(window, null) ?? throw new InvalidOperationException("Stop did not start."));
                    await Task.Delay(50);
                    if (stopped.IsCompleted) throw new InvalidOperationException("Stop returned before the pending delivery drained.");
                    active.SetValue(window, null);
                    completion.SetResult();
                    await stopped;
                    File.WriteAllText(output, "pending delivery blocks other work and stop waits for drain");
                    Environment.Exit(0);
                }
                if (invalidPromotion)
                {
                    if (phase != 0) return;
                    phase = 3;
                    var method = typeof(MainWindow).GetMethod("EnsurePeerStoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("Peer store initialization missing.");
                    await (Task)(method.Invoke(window, null) ?? throw new InvalidOperationException("Peer store did not open."));
                    var store = typeof(MainWindow).GetField("peerStore", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as PeerInvitationStore
                        ?? throw new InvalidOperationException("Recovery store unavailable.");
                    if ((await store.GetPairingRecoveryStatusAsync()).Phase != PairingRecoveryPhase.EvidenceProblem)
                        throw new InvalidOperationException("Invalid promotion was not reported as a recovery problem.");
                    try { store.Get(); throw new InvalidOperationException("Invalid promotion exposed a pairing."); }
                    catch (PairingRecoveryRequiredException) { }
                    var startPeer = typeof(MainWindow).GetMethod("StartPeerInsideAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("Peer start missing.");
                    try
                    {
                        await (Task)(startPeer.Invoke(window, null) ?? throw new InvalidOperationException("Peer start did not run."));
                        throw new InvalidOperationException("Peer start accepted invalid promotion.");
                    }
                    catch (InvalidOperationException error) when (error.Message.Contains("待处理的配对", StringComparison.Ordinal)) { }
                    File.WriteAllText(output, "invalid promotion allows recovery but rejects active pairing and peer start");
                    Environment.Exit(0);
                }
                if (phase == 0)
                {
                    phase = 1;
                    var pairing = FindButton(window, "发现与配对") ?? throw new InvalidOperationException("Pairing navigation missing.");
                    pairing.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return;
                }
                var start = (Button)window.FindName("HostFilelessPairingStart");
                var cancel = (Button)window.FindName("HostFilelessPairingCancel");
                var peerCancel = (Button)window.FindName("PeerFilelessPairingCancel");
                if (start.Visibility != Visibility.Visible || start.IsEnabled || cancel.Visibility != Visibility.Collapsed ||
                    peerCancel.Visibility != Visibility.Collapsed || FindButton(window, "选择发现的设备并生成配对文件") is null ||
                    ((FrameworkElement)window.FindName("HostPairingPrepare")).Visibility != Visibility.Visible)
                    throw new InvalidOperationException("Initial controls or existing file pairing are incorrect.");

                var hostRecovery = window.FindName("HostPairingRecovery") as Button;
                var peerRecovery = window.FindName("PeerPairingRecovery") as Button;
                if (hostRecovery is null || peerRecovery is null || hostRecovery.Visibility != Visibility.Visible ||
                    peerRecovery.Visibility != Visibility.Visible)
                    throw new InvalidOperationException("Recovery controls are not available in fileless candidate.");

                if (phase == 1)
                {
                    phase = 2;
                    hostRecovery.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return;
                }
                var feedback = (TextBlock)window.FindName("Feedback");
                if (!feedback.Text.Contains("准备主控", StringComparison.Ordinal))
                    throw new InvalidOperationException("Host recovery without a prepared store did not provide guidance.");

                var themedDialogSeen = false;
                var inspectDialog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                inspectDialog.Tick += (_, _) =>
                {
                    var confirmation = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.Title == "测试核对");
                    if (confirmation is null) return;
                    themedDialogSeen = confirmation.Background is SolidColorBrush background && background.Color == Color.FromRgb(246, 249, 255) &&
                        FindButton(confirmation, "确认") is not null && FindButton(confirmation, "取消") is not null;
                    if (themedDialogSeen && confirmation.Content is FrameworkElement visual && visual.ActualWidth > 0 && visual.ActualHeight > 0)
                    {
                        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(visual);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var image = new FileStream(Path.ChangeExtension(output, ".png"), FileMode.Create, FileAccess.Write, FileShare.None);
                        encoder.Save(image);
                    }
                    inspectDialog.Stop();
                };
                inspectDialog.Start();
                using var expiration = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                var dialog = typeof(MainWindow).GetMethod("AskFilelessPairing", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Confirmation dialog missing.");
                var accepted = dialog.Invoke(window, ["测试核对", "请逐字核对对方设备上的本机键鼠指纹：\n\n" + new string('A', 64) + "\n\n请让对方核对本机控制通道指纹：\n\n" + new string('B', 64), "确认", expiration.Token]);
                inspectDialog.Stop();
                if (accepted is not false) throw new InvalidOperationException("Expired confirmation was accepted.");
                if (!themedDialogSeen) throw new InvalidOperationException("Confirmation did not use the blue shell with both choices.");
                File.WriteAllText(output, "initial controls, file pairing entry and timed cancellation passed");
                Environment.Exit(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(output, error.ToString());
                Environment.Exit(1);
            }
        };
        timer.Start(); app.Run(window);
    }

    private static Button? FindButton(DependencyObject root, string content)
    {
        if (root is Button button && Equals(button.Content, content)) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindButton(VisualTreeHelper.GetChild(root, i), content) is { } found) return found;
        return null;
    }
}
