using System.IO;
using System.Reflection;
using System.Windows;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;
using NianLink.MultiDevice;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Absolute result path required.");
        using var result = ResearchResultFile.Create(Assembly.GetExecutingAssembly(), args[0]);
        if (result is null) return 2;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var outcome = 1;
        _ = app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "ui-race-" + Guid.NewGuid().ToString("N"));
                var window = new MainWindow(profile, Path.Combine(profile, "synthetic-engine.exe"));
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                if (args.Length == 2 && args[1] is "red-late-switch-disconnect" or "red-late-switch-reconnect" or "late-switch-connected" or "late-switch-unrelated" or "late-switch-denied" or "late-switch-process-error")
                {
                    await VerifyLateSwitchAfterDisconnectAsync(window, profile, args[1]);
                    result.Write("Synthetic switch feedback followed the current IPC evidence: " + args[1]);
                    outcome = 0;
                    return;
                }
                if (args.Length == 2 && args[1].StartsWith("close-ownership-", StringComparison.Ordinal))
                {
                    await VerifyCloseOwnershipAsync(window, args[1], args[0]);
                    result.Write("Return window close ownership was verified: " + args[1]);
                    outcome = 0;
                    return;
                }
                if (args.Length == 2 && args[1] == "recovery-target")
                {
                    await VerifyRecoveryTargetAsync(window);
                    result.Write("Recovery target retains a usable return attempt and respects target boundaries.");
                    outcome = 0;
                    return;
                }
                if (args.Length == 2 && args[1] is "peer-stop-pending-success" or "peer-stop-pending-failure" or "peer-stop-pending-timeout" or "peer-chain-pending-success" or "peer-chain-pending-failure")
                {
                    var blocked = new StopBackend();
                    await using var session = new DeskflowPreviewSession(blocked, (_, _) => Task.CompletedTask, new NoTimerTimeProvider());
                    session.ObserveFrame(new PreviewFrame { State="live", Width=1, Height=1, JpegBytes=[1], CapturedAtUtc=DateTimeOffset.UtcNow });
                    await session.EnableAsync();
                    typeof(MainWindow).GetField("peerDetachedSession", flags)!.SetValue(window, session);
                    var returnTypeForStop = typeof(MainWindow).Assembly.GetType("NianLink.MultiDevice.PreviewReturnWindow", true)!;
                    Func<string, Task> returnAction = _ => Task.CompletedTask;
                    var target = (Window)Activator.CreateInstance(returnTypeForStop, "owned", returnAction)!;
                    typeof(MainWindow).GetField("targetReturn", flags)!.SetValue(window, target);
                    target.Closed += (_, _) => { var field = typeof(MainWindow).GetField("targetReturn", flags)!; if (ReferenceEquals(field.GetValue(window), target)) field.SetValue(window, null); };
                    target.Show();
                    var firstStop = session.StopAsync("正在收回键鼠，请稍候");
                    await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    if (session.CanStop) throw new InvalidOperationException("The first stop must be pending with CanStop false.");
                    var method = args[1].StartsWith("peer-chain-", StringComparison.Ordinal) ? "StopPeerInsideAsync" : "StopPeerPreviewAsync";
                    var stop = (Task)typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, null)!;
                    try
                    {
                        if (stop.IsCompleted || !target.IsVisible || !ReferenceEquals(typeof(MainWindow).GetField("peerDetachedSession", flags)!.GetValue(window), session))
                            throw new InvalidOperationException("Peer preview stop closed the return entry while an existing End was still pending.");
                        if (args[1].EndsWith("-failure", StringComparison.Ordinal)) blocked.Pending.SetException(new IOException("synthetic End failure"));
                        else if (args[1].EndsWith("-success", StringComparison.Ordinal)) blocked.Pending.SetResult(new DeskflowPreviewState());
                        await firstStop.WaitAsync(TimeSpan.FromSeconds(6));
                        await stop.WaitAsync(TimeSpan.FromSeconds(6));
                        if (blocked.EndCalls != 1 || target.IsVisible || typeof(MainWindow).GetField("peerDetachedSession", flags)!.GetValue(window) is not null)
                            throw new InvalidOperationException("Peer preview stop did not finish after the single pending End settled.");
                        if (!args[1].EndsWith("-success", StringComparison.Ordinal) && (!session.Status.Contains("尚未确认") || session.CanStart))
                            throw new InvalidOperationException("A failed End must remain unconfirmed after preview stop.");
                        if (args[1] == "peer-stop-pending-timeout" && blocked.Pending.Task.IsCompleted)
                            throw new InvalidOperationException("A timeout must not be mistaken for cancellation of the synthetic backend.");
                        result.Write("Peer preview stop awaited an existing synthetic End before closing; " + args[1]);
                        outcome=0;
                    }
                    finally { blocked.Pending.TrySetResult(new DeskflowPreviewState()); }
                    return;
                }
                if (args.Length == 2 && args[1] == "default")
                {
                    if (typeof(MainWindow).Assembly.GetType("NianLink.MultiDevice.PeerEmergencyWindow") is not null ||
                        typeof(MainWindow).GetMethod("EnsurePeerEmergencyWindow", flags) is not null)
                        throw new InvalidOperationException("Research emergency entry leaked into default build.");
                    result.Write("Research emergency entry is absent from the default build.");
                    outcome = 0;
                    return;
                }
                if (args.Length == 2 && args[1] == "persistent")
                {
                    var type = typeof(MainWindow).Assembly.GetType("NianLink.MultiDevice.PeerEmergencyWindow", true)!;
                    var selected = 1; var first = 0; var second = 0;
                    Func<Task> dynamicReturn = () => { if (selected == 1) first++; else second++; return Task.CompletedTask; };
                    var persistent = (Window)Activator.CreateInstance(type, flags, null, [dynamicReturn], null)!;
                    persistent.Show();
                    var details = (System.Windows.Controls.StackPanel)type.GetField("details", flags)!.GetValue(persistent)!;
                    if (details.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Emergency entry should start compact.");
                    type.GetMethod("ToggleExpanded", flags)!.Invoke(persistent, null);
                    if (details.Visibility != Visibility.Visible) throw new InvalidOperationException("Emergency entry did not expand.");
                    if (persistent.Left + persistent.ActualWidth > SystemParameters.WorkArea.Right + 1) throw new InvalidOperationException("Expanded entry is outside the work area.");
                    await ((Task)type.GetMethod("ReturnAsync", flags)!.Invoke(persistent, null)!).WaitAsync(TimeSpan.FromSeconds(5));
                    if (!persistent.IsVisible) throw new InvalidOperationException("Successful return closed the only emergency entry.");
                    var warning = (System.Windows.Controls.TextBlock)type.GetField("warning", flags)!.GetValue(persistent)!;
                    if (!warning.Text.Contains("显示器") || !warning.Text.Contains("仍可能执行")) throw new InvalidOperationException("Return side effects are not persistently disclosed.");
                    selected = 2;
                    await ((Task)type.GetMethod("ReturnAsync", flags)!.Invoke(persistent, null)!).WaitAsync(TimeSpan.FromSeconds(5));
                    if (first != 1 || second != 1) throw new InvalidOperationException("Emergency entry used a stale binding.");
                    type.GetMethod("ShowConnectionState", flags)!.Invoke(persistent, [false, true]);
                    var status = (System.Windows.Controls.TextBlock)type.GetField("status", flags)!.GetValue(persistent)!;
                    if (!status.Text.Contains("已停止") || !status.Text.Contains("未确认")) throw new InvalidOperationException("Disconnected uncertainty is not visible.");
                    type.GetMethod("ToggleExpanded", flags)!.Invoke(persistent, null);
                    if (details.Visibility != Visibility.Collapsed || !persistent.IsVisible) throw new InvalidOperationException("Emergency entry did not collapse safely.");
                    persistent.Close();
                    if (!persistent.IsVisible) throw new InvalidOperationException("A normal close removed the emergency entry.");
                    type.GetMethod("Release", flags)!.Invoke(persistent, null);
                    if (persistent.IsVisible) throw new InvalidOperationException("Explicit release did not close the emergency entry.");
                    result.Write("Emergency return entry remains visible after a successful attempt.");
                    outcome = 0;
                    return;
                }
                if (args.Length == 2 && args[1] == "timeout")
                {
                    var type = typeof(MainWindow).Assembly.GetType("NianLink.MultiDevice.PeerEmergencyWindow", true)!;
                    using var occupied = new SemaphoreSlim(0, 1);
                    Func<CancellationToken, Task<DeskLinkSwitchResult>> blocked = async token =>
                    { await occupied.WaitAsync(token); return new DeskLinkSwitchResult { Ok = true, InputTransferred = true }; };
                    Func<Task> action = () => (Task)typeof(MainWindow).GetMethod("AttemptPeerEmergencyAsync", flags)!.Invoke(null, [blocked, TimeSpan.FromMilliseconds(100)])!;
                    var timeoutWindow = (Window)Activator.CreateInstance(type, flags, null, [action], null)!;
                    timeoutWindow.Show();
                    await ((Task)type.GetMethod("ReturnAsync", flags)!.Invoke(timeoutWindow, null)!).WaitAsync(TimeSpan.FromSeconds(5));
                    var status = ((System.Windows.Controls.TextBlock)type.GetField("status", flags)!.GetValue(timeoutWindow)!).Text;
                    var button = (System.Windows.Controls.Button)type.GetField("returnButton", flags)!.GetValue(timeoutWindow)!;
                    if (!status.Contains("未确认") || !status.Contains("可能仍在处理") || !button.IsEnabled || !timeoutWindow.IsVisible)
                        throw new InvalidOperationException("Timed-out emergency return was not safely retryable and unconfirmed.");
                    type.GetMethod("Release", flags)!.Invoke(timeoutWindow, null);
                    result.Write("Blocked emergency return times out, stays unconfirmed and remains actionable.");
                    outcome = 0;
                    return;
                }
                var owner = typeof(MainWindow).GetField("peerControlOwner", flags)!.GetValue(window)!;
                var backend = new Backend();
                owner.GetType().GetMethod("Bind", flags)!.Invoke(owner, [backend]);
                var guardType = typeof(MainWindow).Assembly.GetType("NianLink.MultiDevice.GuardedPeerControl", true)!;
                var switchCalls = 0;
                Func<string, CancellationToken, Task<DeskLinkSwitchResult>> switcher = (target, _) => { switchCalls++; return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, Target = target }); };
                var guard = (IDeskflowPreviewControl)Activator.CreateInstance(guardType, flags, null, [owner, backend, switcher, "host", "peer"], null)!;
                if (args.Length == 2 && args[1] is "research-retired" or "research-clean-stop")
                {
                    var returnMethod = typeof(MainWindow).GetMethod("ReturnPeerEmergencyAsync", flags)
                        ?? throw new InvalidOperationException("This scenario requires the research build.");
                    Func<Task> returnCurrent = () => (Task)returnMethod.Invoke(window, null)!;
                    typeof(MainWindow).GetMethod("EnsurePeerEmergencyWindow", flags)!.Invoke(window, [returnCurrent]);
                    var notice = (Window)typeof(MainWindow).GetField("peerEmergencyWindow", flags)!.GetValue(window)!;
                    if (!notice.IsVisible) throw new InvalidOperationException("The research entry was not visible.");
                    if (args[1] == "research-retired")
                    {
                        _ = guard.BeginPreviewAsync();
                        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    owner.GetType().GetMethod("BeginStopping", flags)!.Invoke(owner, [backend]);
                    owner.GetType().GetMethod("Retire", flags)!.Invoke(owner, [backend]);
                    typeof(MainWindow).GetMethod("PresentPeerEmergencyStopped", flags)!.Invoke(window, null);
                    if (args[1] == "research-clean-stop")
                    {
                        if (notice.IsVisible || typeof(MainWindow).GetField("peerEmergencyWindow", flags)!.GetValue(window) is not null)
                            throw new InvalidOperationException("A clean retired owner retained its research entry.");
                        result.Write("The research entry closes after a clean synthetic retirement.");
                        outcome = 0;
                        return;
                    }
                    var status = (System.Windows.Controls.TextBlock)notice.GetType().GetField("status", flags)!.GetValue(notice)!;
                    if (!notice.IsVisible || !status.Text.Contains("已停止") || !status.Text.Contains("未确认"))
                        throw new InvalidOperationException("A retired unresolved owner lost its visible warning.");
                    await ((Task)notice.GetType().GetMethod("ReturnAsync", flags)!.Invoke(notice, null)!).WaitAsync(TimeSpan.FromSeconds(5));
                    if (!notice.IsVisible || !status.Text.Contains("连接已停止") || switchCalls != 0 || backend.EndCalls != 0)
                        throw new InvalidOperationException("A disconnected research entry sent an action or hid its warning.");
                    notice.GetType().GetMethod("Release", flags)!.Invoke(notice, null);
                    result.Write("A retired unresolved synthetic owner keeps its warning; disconnected click sends no action.");
                    outcome = 0;
                    return;
                }
                if (args.Length == 2 && args[1] is "red-stale" or "red-stop" or "red-no-id")
                {
                    var starting = guard.BeginPreviewAsync();
                    await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (args[1] == "red-stop") owner.GetType().GetMethod("BeginStopping", flags)!.Invoke(owner, [backend]);
                    try
                    {
                        if (args[1] == "red-no-id")
                            await ((Task<DeskLinkSwitchResult>)guardType.GetMethod("SwitchDetailedAsync", flags)!.Invoke(guard, ["host", CancellationToken.None])!);
                        else await guard.EndPreviewAsync("old");
                        throw new InvalidOperationException("An unresolved return was unexpectedly reported as successful.");
                    }
                    catch (InvalidOperationException error) when (error.Message.Contains("尚未确认")) { }
                    if (starting.IsCompleted) throw new InvalidOperationException("The synthetic Begin was no longer in flight.");
                    if (backend.EndCalls != 0 || switchCalls != 0)
                        throw new InvalidOperationException($"An unresolved operation sent an old session End or a normal display-linked switch (end={backend.EndCalls}, switch={switchCalls}).");
                    result.Write("No stale End or ordinary switch was sent while the new request was unresolved.");
                    outcome = 0;
                    return;
                }
                Func<Task> emergencyAction = async () => { await (Task<DeskLinkSwitchResult>)guardType.GetMethod("SwitchDetailedAsync", flags)!.Invoke(guard, ["host", CancellationToken.None])!; };
                var buttonRed = args.Length == 2 && args[1] == "red-button";
                var oldRed = args.Length == 2 && args[1] == "red" || buttonRed;
                if (!oldRed) typeof(MainWindow).GetMethod("EnsurePeerEmergencyWindow", flags)!.Invoke(window, [emergencyAction]);
                var returnType = typeof(MainWindow).Assembly.GetType("NianLink.MultiDevice.PreviewReturnWindow", true)!;
                Func<string, Task> end = _ => Task.CompletedTask;
                var returnWindow = (Window)Activator.CreateInstance(returnType, "old", end)!;
                typeof(MainWindow).GetField("targetReturn", flags)!.SetValue(window, returnWindow);
                returnWindow.Closed += (_, _) => { var field = typeof(MainWindow).GetField("targetReturn", flags)!; if (ReferenceEquals(field.GetValue(window), returnWindow)) field.SetValue(window, null); };
                returnWindow.Show();
                using var invoked = new ManualResetEventSlim();
                Task<DeskflowPreviewState>? begin = null;
                returnWindow.Closing += (_, _) =>
                {
                    _ = Task.Run(() => { begin = guard.BeginPreviewAsync(); invoked.Set(); });
                    if (!invoked.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Begin did not return a task.");
                };
                if (buttonRed)
                    await ((Task)returnType.GetMethod("ReturnAsync", flags)!.Invoke(returnWindow, null)!).WaitAsync(TimeSpan.FromSeconds(5));
                else typeof(MainWindow).GetMethod("ApplyTargetReturn", flags)!.Invoke(window, [new DeskflowPreviewState(), "peer", null]);
                await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await app.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (oldRed)
                {
                    if (typeof(MainWindow).GetField("targetReturn", flags)!.GetValue(window) is not Window { IsVisible: true })
                        throw new InvalidOperationException("In-flight keyboard request lost the return window during Closing.");
                    result.Write("Existing target return entry remains visible.");
                    outcome = 0;
                    return;
                }
                var emergency = (Window)typeof(MainWindow).GetField("peerEmergencyWindow", flags)!.GetValue(window)!;
                var available = emergency.IsVisible;
                if (!available) throw new InvalidOperationException("In-flight keyboard request lost the return window during Closing.");
                await ((Task)emergency.GetType().GetMethod("ReturnAsync", flags)!.Invoke(emergency, null)!).WaitAsync(TimeSpan.FromSeconds(5));
                var message = ((System.Windows.Controls.TextBlock)emergency.GetType().GetField("status", flags)!.GetValue(emergency)!).Text;
                if (switchCalls != 1 || !message.Contains("未确认") || !emergency.IsVisible)
                    throw new InvalidOperationException("Emergency attempt did not run through the guard and preserve uncertainty.");
                result.Write("WPF return entry remains available while a concurrent operation is in flight.");
                outcome = 0;
            }
            catch (Exception error) { result.Write(error.ToString()); }
            finally { app.Shutdown(); }
        });
        app.Run();
        return outcome;
    }

    private sealed class Backend : IDeskflowPreviewControl
    {
        public int EndCalls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<DeskflowPreviewState> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PreviewHandoffSupported => true;
        public string PreviewReturnShortcut => "fixture";
        public string PreviewHandoffHint => "fixture";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) { Entered.TrySetResult(); return pending.Task; }
        public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default) { EndCalls++; return Task.FromResult(new DeskflowPreviewState()); }
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => Task.FromResult(new DeskflowPreviewState());
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) => Task.FromResult(new DeskflowPreviewState());
    }

    private sealed class StopBackend : IDeskflowPreviewControl
    {
        public int EndCalls;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<DeskflowPreviewState> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PreviewHandoffSupported => true;
        public string PreviewReturnShortcut => "fixture";
        public string PreviewHandoffHint => "fixture";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) => Task.FromResult(new DeskflowPreviewState { Phase=DeskflowPreviewPhase.Active, Id="owned" });
        public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default) { EndCalls++; Entered.TrySetResult(); return Pending.Task; }
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => Task.FromResult(new DeskflowPreviewState { Phase=DeskflowPreviewPhase.Active, Id=id });
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) => Task.FromResult(new DeskflowPreviewState());
    }

    private sealed class NoTimerTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
