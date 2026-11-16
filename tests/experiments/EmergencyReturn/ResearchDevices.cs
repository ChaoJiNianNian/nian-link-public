using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace EmergencyReturn.Research;

internal sealed class ResearchClock : TimeProvider
{
    private long elapsed;
    private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => elapsed;
    public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(elapsed);
    public void Advance(TimeSpan value) => elapsed += value.Ticks;
}

internal sealed class ResearchKeyboard(string initial) : IMultiPeerKeyboard
{
    private readonly object gate = new();
    private string active = initial;
    private long revision = 1;
    public bool HoldNextSwitch { get; init; }
    public IReadOnlyList<string> Connected { get; init; } = ["peer", "other"];
    public TaskCompletionSource SwitchEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseSwitch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SwitchCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PriorQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleasePrior { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int SwitchCalls { get; private set; }
    public string Active { get { lock (gate) return active; } }
    public long Revision { get { lock (gate) return revision; } }
    public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
    public Task<DeskLinkState> GetStateAsync(CancellationToken ct)
    {
        lock (gate) return Task.FromResult(new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
            NativeControlAvailable = true, ActiveComputer = active, Revision = revision, ConnectedComputers = Connected.ToArray() });
    }
    public async Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long expectedRevision, CancellationToken ct)
    {
        lock (gate) SwitchCalls++;
        if (HoldNextSwitch) { SwitchEntered.TrySetResult(); await ReleaseSwitch.Task; }
        lock (gate)
        {
            if (active != expected || revision != expectedRevision) { SwitchCompleted.TrySetResult(); return new DeskLinkSwitchResult { Error = "stale" }; }
            if (active != target) { active = target; revision++; }
            SwitchCompleted.TrySetResult();
            return new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target };
        }
    }
    public async Task<bool> SubmitSyntheticPriorCommandAsync(string target, string expected, long expectedRevision)
    {
        PriorQueued.TrySetResult();
        await ReleasePrior.Task;
        lock (gate)
        {
            if (active != expected || revision != expectedRevision) return false;
            if (active != target) { active = target; revision++; }
            return true;
        }
    }
    public Task<DeskLinkState> StopAsync(CancellationToken ct) => GetStateAsync(ct);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ResearchDisplay : IUnifiedDisplayControl
{
    public int Calls { get; private set; }
    public Task<DisplaySwitchOutcome> ApplyAsync(string target, CancellationToken ct)
    { Calls++; return Task.FromResult(new DisplaySwitchOutcome("verified")); }
}
