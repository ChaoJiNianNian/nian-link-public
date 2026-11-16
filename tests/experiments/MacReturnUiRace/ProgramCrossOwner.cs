using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;
using DesktopToolbox.Mac.Views;

internal static partial class Program
{
    private sealed partial class FixtureApp
    {
        private static async Task VerifyCrossOwnerAsync()
        {
#if NIAN_MAC_RETURN_RESEARCH
            if (mode != "research-cross-owner") throw new InvalidOperationException("Cross-owner default mode requires the default build.");
#else
            if (mode != "cross-owner-red") throw new InvalidOperationException("Cross-owner research mode requires the research build.");
#endif
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var keyboard = new CrossOwnerKeyboard();
            await using var manager = new MultiPeerPreviewManager("host", ["peer"], keyboard);
            var backend = new CrossOwnerBackend(manager);
            var assembly = typeof(MacMultiDeviceWindow).Assembly;
            var ownerType = assembly.GetType("NianLink.MultiDevice.PeerControlOwner", true)!;
            var owner = Activator.CreateInstance(ownerType, nonPublic: true)!;
            ownerType.GetMethod("Bind", flags)!.Invoke(owner, [backend]);
            var guardType = assembly.GetType("NianLink.MultiDevice.GuardedPeerControl", true)!;
            var ordinarySwitchCalls = 0;
            Func<string, CancellationToken, Task<DeskLinkSwitchResult>> switcher = (_, _) =>
            {
                ordinarySwitchCalls++;
                throw new InvalidOperationException("The cross-owner fixture must not use an ordinary switch.");
            };
            var guard = (IDeskflowPreviewControl)Activator.CreateInstance(guardType, flags, null, [owner, backend, switcher, "host", "peer"], null)!;
            var main = new MacMultiDeviceWindow(Path.Combine(Path.GetDirectoryName(resultPath)!, "cross-owner-" + Guid.NewGuid().ToString("N")), false, new FakeVault());
            var type = typeof(MacMultiDeviceWindow);
            var update = type.GetMethod("UpdateRemoteReturn", flags)!;
            var field = type.GetField("remoteReturn", flags)!;
            var first = await manager.BeginAsync("host", "peer");
            if (!first.IsActive || first.Id is null || keyboard.Active != "peer")
                throw new InvalidOperationException("The initial synthetic host preview did not begin.");
#if NIAN_MAC_RETURN_RESEARCH
            type.GetMethod("EnsureResearchReturnWindow", flags)!.Invoke(main, null);
#else
            update.Invoke(main, [first.Id, guard]);
#endif
            var entry = field.GetValue(main) as Window ?? throw new InvalidOperationException("No initial return window.");
            if (!entry.IsVisible) throw new InvalidOperationException("Initial return window is not visible.");

            // The host ends the first session; the target has not yet processed that update.
            var returned = await manager.EndAsync("host", first.Id);
            if (returned.HasSession || keyboard.Active != "host") throw new InvalidOperationException("The first synthetic session did not end.");

            // Read completes before the independent simulated host calls the real manager.
            var viewing = await guard.ReadPreviewAsync();
            if (viewing.HasSession || viewing.Phase != DeskflowPreviewPhase.Viewing)
                throw new InvalidOperationException("Expected a stale Viewing observation.");
            var started = await Task.Run(() => manager.BeginAsync("host", "peer"));
            if (!started.IsActive || string.IsNullOrEmpty(started.Id) || started.Id == first.Id || started.TargetComputer != "peer" || keyboard.Active != "peer" || keyboard.SwitchCalls != 3)
                throw new InvalidOperationException("The real manager did not begin the synthetic host preview.");
            if ((bool)ownerType.GetProperty("HasUnresolvedOperation", flags)!.GetValue(owner)!)
                throw new InvalidOperationException("The host unexpectedly entered the target owner guard.");
            if (!entry.IsVisible) throw new InvalidOperationException("Window disappeared before stale update.");

            // Deliver the already-read Viewing result after Begin has completed.
            update.Invoke(main, [null, guard]);
            var life = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            if (!ReferenceEquals(field.GetValue(main), entry) || !entry.IsVisible ||
                life.Windows.Count(w => w.IsVisible && w.Title == MacRemotePreviewReturnWindow.CaptureTitle) != 1)
                throw new InvalidOperationException("Cross-owner stale Viewing update lost the return window.");
            if (backend.EndCalls != 0 || ordinarySwitchCalls != 0)
                throw new InvalidOperationException("The visibility check issued a target return or ordinary switch.");
#if NIAN_MAC_RETURN_RESEARCH
            if (!(bool)type.GetMethod("TryReleaseResearchReturn", flags)!.Invoke(main, [false])!)
                throw new InvalidOperationException("Research window did not release after fixture assertions.");
#endif
        }

        private sealed class CrossOwnerBackend(MultiPeerPreviewManager manager) : IDeskflowPreviewControl
        {
            public int EndCalls { get; private set; }
            public bool PreviewHandoffSupported => true;
            public string PreviewReturnShortcut => "fixture";
            public string PreviewHandoffHint => "fixture";
            public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) => throw new InvalidOperationException("Host begins directly in this fixture.");
            public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected renew.");
            public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default) { EndCalls++; return manager.EndAsync("peer", id, ct); }
            public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) => manager.ReadForDeviceAsync("peer", ct);
        }

        private sealed class CrossOwnerKeyboard : IMultiPeerKeyboard
        {
            private long revision = 1;
            public string Active { get; private set; } = "host";
            public int SwitchCalls { get; private set; }
            public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
            public Task<DeskLinkState> GetStateAsync(CancellationToken ct) => Task.FromResult(new DeskLinkState
            {
                ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true,
                ActiveComputer = Active, ConnectedComputers = ["peer"], Revision = revision
            });
            public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long expectedRevision, CancellationToken ct)
            {
                SwitchCalls++;
                if (Active != expected || revision != expectedRevision) return Task.FromResult(new DeskLinkSwitchResult { Error = "stale synthetic state" });
                Active = target; revision++;
                return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target, Target = target });
            }
            public Task<DeskLinkState> StopAsync(CancellationToken ct) => GetStateAsync(ct);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
