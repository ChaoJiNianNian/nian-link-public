using System.Reflection;
using Avalonia.Controls;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Views;

internal static partial class Program
{
    private sealed partial class FixtureApp
    {
        private static async Task VerifyRecoveryTargetAsync()
        {
#if NIAN_MAC_RETURN_RESEARCH
            throw new InvalidOperationException("Recovery target test requires the default build.");
#else
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var main = new MacMultiDeviceWindow(Path.Combine(Path.GetDirectoryName(resultPath)!, "recovery-target-" + Guid.NewGuid().ToString("N")), false, new FakeVault());
            var type = typeof(MacMultiDeviceWindow);
            var select = type.GetMethod("TargetReturnId", flags | BindingFlags.Static) ?? throw new InvalidOperationException("The target return selection method is missing.");
            var update = type.GetMethod("UpdateRemoteReturn", flags)!;
            var field = type.GetField("remoteReturn", flags)!;
            var backend = new RecoveryBackend();
            var ownerType = type.Assembly.GetType("NianLink.MultiDevice.PeerControlOwner", true)!;
            var owner = Activator.CreateInstance(ownerType, nonPublic: true)!;
            ownerType.GetMethod("Bind", flags)!.Invoke(owner, [backend]);
            var guardType = type.Assembly.GetType("NianLink.MultiDevice.GuardedPeerControl", true)!;
            var switchCalls = 0;
            Func<string, CancellationToken, Task<DeskLinkSwitchResult>> switcher = (_, _) => { switchCalls++; throw new InvalidOperationException("Unexpected normal switch."); };
            var guard = (IDeskflowPreviewControl)Activator.CreateInstance(guardType, flags, null, [owner, backend, switcher, "host", "peer"], null)!;
            void Apply(DeskflowPreviewState state, string local = "peer", IDeskflowPreviewControl? control = null)
            {
                var id = select.Invoke(null, [state, local]);
                update.Invoke(main, [id, control ?? guard]);
            }
            MacRemotePreviewReturnWindow? Entry() => field.GetValue(main) as MacRemotePreviewReturnWindow;
            Apply(State(DeskflowPreviewPhase.Active, "one", "peer"));
            var old = Entry() ?? throw new InvalidOperationException("Active target did not show the return window.");
            var recovery = await guard.ReadPreviewAsync();
            Apply(recovery);
            if (!ReferenceEquals(old, Entry()) || !old.IsVisible) throw new InvalidOperationException("Recovery state removed the target return window.");
            await ((Task)typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync", flags)!.Invoke(old, null)!).WaitAsync(TimeSpan.FromSeconds(3));
            if (!old.IsVisible || backend.EndCalls != 1 || backend.LastEnd != "one" || switchCalls != 0 ||
                !(bool)ownerType.GetProperty("IsUncertain", flags)!.GetValue(owner)!)
                throw new InvalidOperationException("Guarded return was not attempted or did not remain unconfirmed.");
            var button = (Button)typeof(MacRemotePreviewReturnWindow).GetField("button", flags)!.GetValue(old)!;
            if ((button.Content as string ?? "").Contains("已归还"))
                throw new InvalidOperationException("Unconfirmed recovery falsely reported return.");
            await ((Task)typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync", flags)!.Invoke(old, null)!).WaitAsync(TimeSpan.FromSeconds(3));
            if (!old.IsVisible || backend.EndCalls != 2 || backend.LastEnd != "one" || switchCalls != 0)
                throw new InvalidOperationException("Unconfirmed return did not retain the existing retry attempt.");
            try { await guard.ReadPreviewAsync(); throw new InvalidOperationException("Unconfirmed guard unexpectedly resumed reads."); }
            catch (InvalidOperationException error) when (error.Message.Contains("尚未确认")) { }
            type.GetMethod("CloseRemoteReturn", flags)!.Invoke(main, null);

            Apply(State(DeskflowPreviewPhase.Recovery, "two", "peer"), control: backend);
            var next = Entry() ?? throw new InvalidOperationException("Recovery with a new known ID did not show an entry.");
            if (!next.IsVisible || next.SessionId != "two") throw new InvalidOperationException("Recovery entry has the wrong ID.");
            Apply(State(DeskflowPreviewPhase.Recovery, "two", "other"), control: backend);
            if (Entry() is not null || next.IsVisible) throw new InvalidOperationException("Another target retained an entry.");
            Apply(State(DeskflowPreviewPhase.Recovery, null, "peer"), control: backend);
            if (Entry() is not null) throw new InvalidOperationException("A missing ID created an entry.");
            Apply(State(DeskflowPreviewPhase.Recovery, "", "peer"), control: backend);
            if (Entry() is not null) throw new InvalidOperationException("An empty ID created an entry.");
            Apply(State(DeskflowPreviewPhase.Recovery, " ", "peer"), control: backend);
            if (Entry() is not null) throw new InvalidOperationException("A whitespace ID created an entry.");
            Apply(State(DeskflowPreviewPhase.Recovery, "inflight", ""), "", backend);
            if (Entry() is not null) throw new InvalidOperationException("An empty local target created an entry.");
            Apply(State(DeskflowPreviewPhase.Recovery, "inflight", " "), " ", backend);
            if (Entry() is not null) throw new InvalidOperationException("A whitespace local target created an entry.");
            foreach (var phase in new[] { DeskflowPreviewPhase.Preparing, DeskflowPreviewPhase.Returning })
            {
                Apply(State(phase, "inflight", "peer"), control: backend);
                if (Entry() is not null) throw new InvalidOperationException("An in-flight state unexpectedly created an entry.");
            }
            Apply(State(DeskflowPreviewPhase.Recovery, "three", "host"), "host", backend);
            if (Entry() is not Window { IsVisible: true }) throw new InvalidOperationException("Host target recovery did not show an entry.");
            Apply(new DeskflowPreviewState(), "host", backend);
            if (Entry() is not null) throw new InvalidOperationException("Viewing retained an entry.");
#endif
        }

        private static DeskflowPreviewState State(DeskflowPreviewPhase phase, string? id, string target) =>
            new() { Phase = phase, Id = id, TargetComputer = target };

        private sealed class RecoveryBackend : IDeskflowPreviewControl
        {
            public int EndCalls { get; private set; }
            public string? LastEnd { get; private set; }
            public bool PreviewHandoffSupported => true;
            public string PreviewReturnShortcut => "fixture";
            public string PreviewHandoffHint => "fixture";
            public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default)
            { EndCalls++; LastEnd = id; return Task.FromResult(State(DeskflowPreviewPhase.Recovery, id, "peer")); }
            public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) => Task.FromResult(State(DeskflowPreviewPhase.Recovery, "one", "peer"));
            public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Begin.");
            public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Renew.");
        }
    }
}
