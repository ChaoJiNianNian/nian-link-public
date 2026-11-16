using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Views;
using Nian.Input.Security;

// Separate research fixture: no actual Mac, credentials or keyboard.
internal static partial class Program
{
    private static string resultPath = null!;
    private static string mode = null!;
    private static int exitCode = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Absolute result path required.");
        resultPath = args[0];
        mode = args.Length == 2 ? args[1] : "poll";
        if (mode is not ("legacy-real-recovery" or "exception-sentinels" or "legacy-owner-normal" or "legacy-owner-empty" or "legacy-owner-veto" or "legacy-owner-hidden-veto" or "legacy-owner-reentrant" or "legacy-owner-closed" or "return-close-multi" or "return-close-hidden" or "return-close-legacy" or "return-close-normal" or "return-close-failure" or "recovery-target" or "research-close-ready" or "research-close-allow" or "research-close-cancel" or "research-stop-failed-core" or "research-stop-reuse" or "research-stopped-show-failure" or "research-stopped-old-dismiss" or "research-stopped-hide-throw" or "research-stopped-dismiss" or "research-stopped-hidden-veto" or "research-stopped-reentrant" or "default-close-same-id-reentrant" or "default-close-reentrant" or "default-close-hidden-throw" or "default-close-hidden-canceled" or "default-close-canceled" or "default-close-throw" or "default-close-normal" or "research-stop-late-begin" or "research-start-repeat" or "research-start-bound-failure" or "research-stop-bound-pending" or "research-dynamic" or "cross-owner-red" or "research-cross-owner" or "assert-default" or "research-start-preflight" or "research-stop-failure" or "research-close-failure" or "research-stop-inflight" or "research-poll" or "research-button" or "research-failure" or "research-generation" or "research-stop" or "research-collapse" or "research-cancel-release" or "research-reentrant" or "research-show-failure-cleanup" or "poll" or "button" or "unknown" or "ordinary" or "statefailure" or "legacy" or "disposefailure" or "disposepending" or "disposesuccess" or "startfailure" or "startconcurrent" or "startsuccess" or "startcleanfailure" or "startstopfailure" or "closefailure" or "closesuccess")) throw new ArgumentException("Unknown fixture mode.");
#if !NIAN_MAC_RETURN_RESEARCH
        if (mode == "assert-default")
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            if (typeof(MacMultiDeviceWindow).GetMethod("EnsureResearchReturnWindow", flags) is not null ||
                typeof(MacRemotePreviewReturnWindow).GetMethod("ReleaseResearch", flags) is not null)
                throw new InvalidOperationException("Research code leaked into default candidate.");
            File.WriteAllText(resultPath,"Default candidate contains no research entry.");
            return 0;
        }
        if (mode.StartsWith("research-", StringComparison.Ordinal)) throw new InvalidOperationException("Research mode is not enabled.");
#else
        if (mode == "assert-default") throw new InvalidOperationException("Default-mode verification cannot run in research build.");
#endif
        AppBuilder.Configure<FixtureApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        return exitCode;
    }

    private sealed partial class FixtureApp : Application
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
                var life = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
                var backend = new Backend();
                Task<DeskflowPreviewState>? begin = null;
                try
                {
                    if (mode == "legacy-real-recovery")
                    {
                        await VerifyActualViewModelRecoveryAsync();
                        File.WriteAllText(resultPath, "Actual MacLinkViewModel Recovery was unconfirmed.");
                        exitCode = 0;
                        return;
                    }
                    if (mode == "exception-sentinels")
                    {
                        await VerifyExceptionSentinelsAsync();
                        File.WriteAllText(resultPath, "Synthetic exceptions retained an unconfirmed entry.");
                        exitCode = 0;
                        return;
                    }
                    if (mode.StartsWith("legacy-owner-", StringComparison.Ordinal))
                    {
                        VerifyLegacyReturnOwnership();
                        File.WriteAllText(resultPath, "Legacy owner retained the correct return window: " + mode);
                        exitCode = 0;
                        return;
                    }
                    if (mode.StartsWith("return-close-", StringComparison.Ordinal))
                    {
                        await VerifyReturnCloseAsync();
                        File.WriteAllText(resultPath, "Return close status verified: " + mode);
                        exitCode = 0;
                        return;
                    }
                    if (mode == "recovery-target")
                    {
                        await VerifyRecoveryTargetAsync();
                        File.WriteAllText(resultPath, "Recovery target window test passed.");
                        exitCode = 0;
                        return;
                    }
                    if (mode is "default-close-same-id-reentrant" or "default-close-reentrant" or "default-close-hidden-throw" or "default-close-hidden-canceled" or "default-close-canceled" or "default-close-throw" or "default-close-normal")
                    {
                        await VerifyDefaultCloseOwnershipAsync();
                        File.WriteAllText(resultPath, "Default return window close ownership preserved.");
                        exitCode = 0;
                        return;
                    }
                    if (mode is "cross-owner-red" or "research-cross-owner")
                    {
                        await VerifyCrossOwnerAsync();
                        File.WriteAllText(resultPath, "Cross-owner return window remained visible and unique.");
                        exitCode = 0;
                        return;
                    }
#if NIAN_MAC_RETURN_RESEARCH
                    if (mode is "research-stopped-show-failure" or "research-stopped-old-dismiss" or "research-stopped-hide-throw" or "research-stopped-dismiss" or "research-stopped-hidden-veto" or "research-stopped-reentrant") { await VerifyStoppedEntryAsync(); File.WriteAllText(resultPath,"Stopped research entry verified."); exitCode=0; return; }
                    if (mode == "research-dynamic")
                    {
                        await VerifyDynamicReturnAsync();
                        File.WriteAllText(resultPath, "Dynamic research return scenarios passed.");
                        exitCode = 0;
                        return;
                    }
                    if (mode.StartsWith("research-", StringComparison.Ordinal) && mode is not ("research-close-ready" or "research-close-allow" or "research-close-cancel" or "research-stop-failed-core" or "research-stop-reuse" or "research-stop-late-begin" or "research-start-repeat" or "research-start-bound-failure" or "research-stop-bound-pending" or "research-start-preflight" or "research-stop-failure" or "research-close-failure" or "research-stop-inflight"))
                    {
                        await VerifyResearchAsync();
                        File.WriteAllText(resultPath, "Research window invariant verified in the headless fixture.");
                        exitCode = 0;
                        return;
                    }
#endif
                    if(mode is "research-close-ready" or "research-close-allow" or "research-close-cancel" or "research-stop-failed-core" or "research-stop-reuse" or "research-stop-late-begin" or "research-start-repeat" or "research-start-bound-failure" or "research-stop-bound-pending" or "research-start-preflight" or "research-stop-failure" or "research-close-failure" or "research-stop-inflight" or "disposefailure" or "disposepending" or "disposesuccess" or "startfailure" or "startconcurrent" or "startsuccess" or "startcleanfailure" or "startstopfailure" or "closefailure" or "closesuccess")
                    {
                        await VerifyStopLifecycleAsync();
                        File.WriteAllText(resultPath,"Stop retained correct ordering for collaboration and keyboard cleanup.");
                        exitCode=0;
                        return;
                    }
                    if (mode is "unknown" or "ordinary" or "statefailure" or "legacy")
                    {
                        await CheckReturnStatusAsync();
                        File.WriteAllText(resultPath, "The return status matches the owner state and the original action remains available.");
                        exitCode = 0;
                        return;
                    }
                    var profile = Path.Combine(Path.GetDirectoryName(resultPath)!, "mac-ui-race-" + Guid.NewGuid().ToString("N"));
                    var main = new MacMultiDeviceWindow(profile, false, new FakeVault());
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    var assembly = typeof(MacMultiDeviceWindow).Assembly;
                    var ownerType = assembly.GetType("NianLink.MultiDevice.PeerControlOwner", true)!;
                    var owner = Activator.CreateInstance(ownerType, nonPublic: true)!;
                    ownerType.GetMethod("Bind", flags)!.Invoke(owner, [backend]);
                    var guardType = assembly.GetType("NianLink.MultiDevice.GuardedPeerControl", true)!;
                    Func<string, CancellationToken, Task<DeskLinkSwitchResult>> switcher = (target, _) =>
                        Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, Target = target });
                    var guard = (IDeskflowPreviewControl)Activator.CreateInstance(guardType, flags, null, [owner, backend, switcher, "host", "peer"], null)!;
                    var update = typeof(MacMultiDeviceWindow).GetMethod("UpdateRemoteReturn", flags)!;
                    // Button mode supplies a synthetic successful old End. New Begin still traverses the real owner guard.
                    update.Invoke(main, ["old", mode == "button" ? backend : guard]);
                    var field = typeof(MacMultiDeviceWindow).GetField("remoteReturn", flags)!;
                    var overlay = (MacRemotePreviewReturnWindow)field.GetValue(main)!;
                    if (!overlay.IsVisible) throw new InvalidOperationException("Fixture failed to show the initial return window.");
                    using var admitted = new ManualResetEventSlim();
                    overlay.Closing += (_, _) =>
                    {
                        _ = Task.Run(() => { begin = guard.BeginPreviewAsync(); admitted.Set(); });
                        if (!admitted.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Begin was not admitted.");
                    };
                    if (mode == "poll") update.Invoke(main, [null, guard]);
                    else await ((Task)typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync", flags)!.Invoke(overlay, null)!).WaitAsync(TimeSpan.FromSeconds(5));
                    await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (field.GetValue(main) is not Window { IsVisible: true })
                        throw new InvalidOperationException("In-flight keyboard request lost the Mac return window during Closing.");
                    File.WriteAllText(resultPath, "The return window remains available for the in-flight operation.");
                    exitCode = 0;
                }
                catch (Exception error) { File.WriteAllText(resultPath, error.ToString()); }
                finally
                {
                    backend.Complete();
                    if (begin is not null) try { await begin.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                    life.Shutdown();
                }
            });
        }

        private static async Task CheckReturnStatusAsync()
        {
            Func<string, Task> action = _ => Task.FromException(new InvalidOperationException("synthetic failure"));
            Func<bool> state = () => mode == "statefailure" ? throw new InvalidOperationException("synthetic state failure") : mode == "unknown";
            var type = typeof(MacRemotePreviewReturnWindow);
            var window = new MacRemotePreviewReturnWindow("synthetic", action, candidateBlue: mode != "legacy", isUnconfirmed: mode == "legacy" ? null : state);
            window.Show();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var button = (Button)type.GetField("button", flags)!.GetValue(window)!;
            try
            {
                await ((Task)type.GetMethod("ReturnAsync", flags)!.Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(5));
                var text = button.Content as string ?? "";
                if (mode == "unknown" && !text.Contains("尚未确认", StringComparison.Ordinal))
                    throw new InvalidOperationException("The unknown return outcome is not accurately displayed.");
                if (mode == "statefailure" && text != "状态不可用 · 未确认")
                    throw new InvalidOperationException("Unavailable owner state is not accurately displayed.");
                if (mode is "ordinary" or "legacy" && text != "归还未确认 · 检查键鼠")
                    throw new InvalidOperationException("An exception did not retain an unconfirmed status.");
                if (!window.IsVisible || !button.IsEnabled)
                    throw new InvalidOperationException("Failed or unconfirmed return must remain available.");
            }
            finally { window.Close(); }
        }
    }

    private sealed class FakeVault : IIdentityVault
    {
        public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic fixture cannot load identities.");
        public Task StoreAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic fixture cannot store identities.");
    }

    private sealed class Backend : IDeskflowPreviewControl
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int EndCalls;
        private readonly TaskCompletionSource<DeskflowPreviewState> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete() => pending.TrySetResult(new DeskflowPreviewState { Id = "synthetic", Phase = DeskflowPreviewPhase.Active });
        public bool PreviewHandoffSupported => true;
        public string PreviewReturnShortcut => "fixture";
        public string PreviewHandoffHint => "fixture";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) { Entered.TrySetResult(); return pending.Task; }
        public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default) { EndCalls++; return Task.FromResult(new DeskflowPreviewState()); }
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => Task.FromResult(new DeskflowPreviewState());
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) => Task.FromResult(new DeskflowPreviewState());
    }
}
