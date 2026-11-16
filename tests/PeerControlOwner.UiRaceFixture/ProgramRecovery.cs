using System.Reflection;
using System.Windows;
using DesktopToolbox.DeskLink;
using NianLink.MultiDevice;

internal static partial class Program
{
    private static async Task VerifyRecoveryTargetAsync(MainWindow main)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = typeof(MainWindow);
        var apply = type.GetMethod("ApplyTargetReturn", flags)!;
        var field = type.GetField("targetReturn", flags)!;
        var attempts = new List<string>();
        var outcome = DeskflowPreviewPhase.Recovery;
        Func<string, Task<DeskflowPreviewState>> end = id =>
        {
            attempts.Add(id);
            return Task.FromResult(outcome == DeskflowPreviewPhase.Viewing ? new DeskflowPreviewState() : State(outcome, id, "peer"));
        };
        void Apply(DeskflowPreviewState state, string? local = "peer") => apply.Invoke(main, [state, local, end]);
        Window? Entry() => field.GetValue(main) as Window;

        Apply(State(DeskflowPreviewPhase.Active, "one", "peer"));
        var old = Entry() ?? throw new InvalidOperationException("Active target did not show the return window.");
        Apply(State(DeskflowPreviewPhase.Recovery, "one", "peer"));
        if (!ReferenceEquals(old, Entry()) || !old.IsVisible)
            throw new InvalidOperationException("Recovery state removed the target return window.");
        await ((Task)old.GetType().GetMethod("ReturnAsync", flags)!.Invoke(old, null)!).WaitAsync(TimeSpan.FromSeconds(3));
        if (!old.IsVisible || attempts.Count != 1 || attempts[0] != "one")
            throw new InvalidOperationException("Recovery return attempt did not remain retryable.");
        var button = (System.Windows.Controls.Button)old.GetType().GetField("button", flags)!.GetValue(old)!;
        if (!(button.Content as string ?? "").Contains("未确认"))
            throw new InvalidOperationException("Recovery outcome did not say the return is unconfirmed.");
        outcome = DeskflowPreviewPhase.Viewing;
        await ((Task)old.GetType().GetMethod("ReturnAsync", flags)!.Invoke(old, null)!).WaitAsync(TimeSpan.FromSeconds(3));
        if (old.IsVisible || attempts.Count != 2 || Entry() is not null)
            throw new InvalidOperationException("Successful retry did not close the window.");

        Apply(State(DeskflowPreviewPhase.Recovery, "two", "peer"));
        var next = Entry() ?? throw new InvalidOperationException("Recovery with a new known ID did not show an entry.");
        if (!next.IsVisible || next.GetType().GetProperty("SessionId")!.GetValue(next) as string != "two")
            throw new InvalidOperationException("Recovery entry has the wrong ID.");
        Apply(State(DeskflowPreviewPhase.Recovery, "two", "other"));
        if (Entry() is not null || next.IsVisible) throw new InvalidOperationException("Another target retained an entry.");
        Apply(State(DeskflowPreviewPhase.Recovery, null, "peer"));
        if (Entry() is not null) throw new InvalidOperationException("A missing ID created an entry.");
        Apply(State(DeskflowPreviewPhase.Recovery, "", "peer"));
        if (Entry() is not null) throw new InvalidOperationException("An empty ID created an entry.");
        Apply(State(DeskflowPreviewPhase.Recovery, " ", "peer"));
        if (Entry() is not null) throw new InvalidOperationException("A whitespace ID created an entry.");
        Apply(State(DeskflowPreviewPhase.Recovery, "inflight", ""), "");
        if (Entry() is not null) throw new InvalidOperationException("An empty local target created an entry.");
        Apply(State(DeskflowPreviewPhase.Recovery, "inflight", " "), " ");
        if (Entry() is not null) throw new InvalidOperationException("A whitespace local target created an entry.");
        foreach (var phase in new[] { DeskflowPreviewPhase.Preparing, DeskflowPreviewPhase.Returning })
        {
            Apply(State(phase, "inflight", "peer"));
            if (Entry() is not null) throw new InvalidOperationException("An in-flight state unexpectedly created an entry.");
        }
        Apply(State(DeskflowPreviewPhase.Recovery, "three", "host"), "host");
        if (Entry() is not Window { IsVisible: true }) throw new InvalidOperationException("Host target recovery did not show an entry.");
        Apply(new DeskflowPreviewState(), "host");
        if (Entry() is not null) throw new InvalidOperationException("Viewing retained an entry.");
    }

    private static DeskflowPreviewState State(DeskflowPreviewPhase phase, string? id, string target) =>
        new() { Phase = phase, Id = id, TargetComputer = target };
}
