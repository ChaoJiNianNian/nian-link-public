#if NIAN_MAC_RETURN_RESEARCH
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Views;

internal static partial class Program
{
    private sealed partial class FixtureApp
    {
        private static async Task VerifyDynamicReturnAsync()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var main = new MacMultiDeviceWindow(Path.Combine(Path.GetDirectoryName(resultPath)!, "dynamic-" + Guid.NewGuid().ToString("N")), false, new FakeVault());
            var type = typeof(MacMultiDeviceWindow);
            var ensure = type.GetMethod("EnsureResearchReturnWindow", flags)!;
            var start = type.GetMethod("BeginResearchConnection", flags) ?? throw new InvalidOperationException("Dynamic connection binding is missing.");
            var stop = type.GetMethod("InvalidateResearchConnection", flags)!;
            var observe = type.GetMethod("ApplyResearchObservation", flags)!;
            var release = type.GetMethod("TryReleaseResearchReturn", flags)!;
            var field = type.GetField("remoteReturn", flags)!;
            ensure.Invoke(main, null);
            var entry = (MacRemotePreviewReturnWindow)field.GetValue(main)!;
            var click = typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync", flags)!;
            var button = (Button)typeof(MacRemotePreviewReturnWindow).GetField("button", flags)!.GetValue(entry)!;
            var first = new DynamicBackend();
            var second = new DynamicBackend();
            var third = new DynamicBackend();
            var firstGuard=DynamicGuard(first);
            var secondGuard=DynamicGuard(second);
            var thirdGuard=DynamicGuard(third);
            try
            {
                var generation1 = (long)start.Invoke(main, null)!;
                if (button.Content is not string noId || !noId.Contains("无会话编号",StringComparison.Ordinal) || !noId.Contains("未确认",StringComparison.Ordinal))
                    throw new InvalidOperationException("The no-ID state does not explain that return cannot be sent.");
                await ((Task)click.Invoke(entry, null)!).WaitAsync(TimeSpan.FromSeconds(3));
                if (first.EndIds.Count != 0 || !entry.IsVisible || button.Content is not string afterNoId || !afterNoId.Contains("无会话编号",StringComparison.Ordinal)) throw new InvalidOperationException("An unknown ID must not dispatch a return or imply an attempt.");
                var screen=entry.Screens.Primary ?? entry.Screens.ScreenFromWindow(entry);
                if(screen is null)throw new InvalidOperationException("Headless fixture has no screen for layout validation.");
                if(entry.Position.X+(int)Math.Ceiling(entry.Width*screen.Scaling)>screen.WorkingArea.Right)
                    throw new InvalidOperationException("The expanded research entry extends beyond the working area.");
                var fold=(Button)typeof(MacRemotePreviewReturnWindow).GetField("researchFold",flags)!.GetValue(entry)!;
                if(ToolTip.GetTip(fold) is not string tip || !tip.Contains("无会话编号",StringComparison.Ordinal))
                    throw new InvalidOperationException("The fold tooltip misrepresents the missing session ID.");
                observe.Invoke(main, [generation1, "old", firstGuard]);
                if(ToolTip.GetTip(fold) is not string observedTip || !observedTip.Contains("尝试",StringComparison.Ordinal))throw new InvalidOperationException("Known session fold hint is missing.");
                observe.Invoke(main, [generation1, null, firstGuard]); // A stale Viewing must not remove the last known attempt.
                var oldClick = (Task)click.Invoke(entry, null)!;
                await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (first.EndIds.Count != 1 || first.EndIds[0] != "old") throw new InvalidOperationException("The observed session was not attempted.");
                stop.Invoke(main, null);
                var generation2 = (long)start.Invoke(main, null)!;
                observe.Invoke(main, [generation2, "new", secondGuard]);
                observe.Invoke(main, [generation1, "stale", firstGuard]);
                first.Completion.SetResult(new DeskflowPreviewState());
                await oldClick.WaitAsync(TimeSpan.FromSeconds(3));
                if (!entry.IsVisible || button.Content is not string text || !text.Contains("未确认", StringComparison.Ordinal))
                    throw new InvalidOperationException("A late old return changed the persistent unconfirmed entry.");
                var newClick = (Task)click.Invoke(entry, null)!;
                await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (second.EndIds.Count != 1 || second.EndIds[0] != "new" || first.EndIds.Count != 1)
                    throw new InvalidOperationException("An old observation or response replaced the new binding.");
                second.Completion.SetResult(new DeskflowPreviewState());
                await newClick.WaitAsync(TimeSpan.FromSeconds(3));
                stop.Invoke(main, null);
                await ((Task)click.Invoke(entry, null)!).WaitAsync(TimeSpan.FromSeconds(3));
                if (second.EndIds.Count != 1 || !entry.IsVisible) throw new InvalidOperationException("Stopped binding was still usable or entry disappeared.");
                var life = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                if (life.Windows.Count(w => w.IsVisible && w.Title == MacRemotePreviewReturnWindow.CaptureTitle) != 1)
                    throw new InvalidOperationException("More than one capture window exists.");
                if (!(bool)release.Invoke(main,[false])!) throw new InvalidOperationException("Old entry did not release.");
                ensure.Invoke(main,null);
                var next=(MacRemotePreviewReturnWindow)field.GetValue(main)!;
                var generation3=(long)start.Invoke(main,null)!;
                observe.Invoke(main,[generation3,"third",thirdGuard]);
                third.Completion.SetResult(new DeskflowPreviewState());
                await ((Task)click.Invoke(entry,null)!).WaitAsync(TimeSpan.FromSeconds(3));
                if(third.EndIds.Count!=0)throw new InvalidOperationException("A released old window sent an action using the new connection.");
                await ((Task)click.Invoke(next,null)!).WaitAsync(TimeSpan.FromSeconds(3));
                if(third.EndIds.Count!=1 || third.EndIds[0]!="third")throw new InvalidOperationException("The current window could not attempt the current observed session.");
            }
            finally
            {
                first.Completion.TrySetResult(new DeskflowPreviewState());
                second.Completion.TrySetResult(new DeskflowPreviewState());
                third.Completion.TrySetResult(new DeskflowPreviewState());
                release.Invoke(main, [false]);
            }
        }

        private static IDeskflowPreviewControl DynamicGuard(DynamicBackend backend)
        {
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
            var assembly=typeof(MacMultiDeviceWindow).Assembly;
            var ownerType=assembly.GetType("NianLink.MultiDevice.PeerControlOwner",true)!;
            var owner=Activator.CreateInstance(ownerType,nonPublic:true)!;
            ownerType.GetMethod("Bind",flags)!.Invoke(owner,[backend]);
            var guardType=assembly.GetType("NianLink.MultiDevice.GuardedPeerControl",true)!;
            Func<string,CancellationToken,Task<DeskLinkSwitchResult>> noSwitch=(_,_)=>throw new InvalidOperationException("Ordinary switch is forbidden in the fixture.");
            return (IDeskflowPreviewControl)Activator.CreateInstance(guardType,flags,null,[owner,backend,noSwitch,"host","peer"],null)!;
        }

        private sealed class DynamicBackend : IDeskflowPreviewControl
        {
            public readonly List<string> EndIds = [];
            public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<DeskflowPreviewState> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool PreviewHandoffSupported => true;
            public string PreviewReturnShortcut => "fixture";
            public string PreviewHandoffHint => "fixture";
            public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default)
            { EndIds.Add(id); Entered.TrySetResult(); return Completion.Task; }
            public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Begin.");
            public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Renew.");
            public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Read.");
        }
    }
}
#endif
