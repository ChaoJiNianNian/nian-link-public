using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace EmergencyReturn.Research;

// These are intentionally failing research assertions. They demonstrate why
// the existing manager cannot be reused as an input-only emergency API.
public sealed class EmergencyReturnRedTests
{
    [Fact]
    public async Task OrdinarySwitchCannotGuaranteeInputOnlyReturn()
    {
        var keyboard = new SyntheticKeyboard("peer");
        var display = new SyntheticDisplay();
        await using var manager = new MultiPeerPreviewManager("host", ["peer"], keyboard, display: display);
        await manager.SwitchNormalAsync("peer", "host");
        Assert.Equal("host", keyboard.Active);
        Assert.Equal(0, display.ApplyCalls); // RED: normal switching applies the display.
    }

    [Fact]
    public async Task FocusCallbackCanApplyDisplayAfterInputOnlySwitch()
    {
        var keyboard = new SyntheticKeyboard("peer");
        var display = new SyntheticDisplay();
        await using var manager = new MultiPeerPreviewManager("host", ["peer"], keyboard, display: display);
        var before = await keyboard.GetStateAsync(default);
        await keyboard.SwitchIfCurrentAsync("host", before.ActiveComputer!, before.Revision, default);
        await manager.OnFocusChangedAsync(new FocusChangedEvent("host", keyboard.Revision));
        Assert.Equal(0, display.ApplyCalls); // RED: an input-only native change can trigger display via focus callback.
    }

    [Fact]
    public async Task OldPreviewExpiryCanMoveFocusAwayAfterAttemptedHostReturn()
    {
        var clock = new SyntheticClock();
        var keyboard = new SyntheticKeyboard("peer");
        var display = new SyntheticDisplay();
        await using var manager = new MultiPeerPreviewManager("host", ["peer"], keyboard, clock, display);
        var preview = await manager.BeginAsync("peer", "peer");
        Assert.True(preview.IsActive);
        Assert.Equal("host", keyboard.Active); // Simulated input is already on the host.
        var before = await keyboard.GetStateAsync(default);
        Assert.True((await keyboard.SwitchIfCurrentAsync("host", before.ActiveComputer!, before.Revision, default)).InputTransferred);
        clock.Advance(TimeSpan.FromSeconds(9));
        await manager.ReadForDeviceAsync("peer");
        Assert.Equal("host", keyboard.Active); // RED: lease expiry returns focus to the viewer.
    }

    [Fact]
    public async Task OldPreviewDisposeCanMoveFocusAwayAfterAttemptedHostReturn()
    {
        var keyboard = new SyntheticKeyboard("peer");
        var manager = new MultiPeerPreviewManager("host", ["peer"], keyboard);
        var preview = await manager.BeginAsync("peer", "peer");
        Assert.True(preview.IsActive);
        Assert.Equal("host", keyboard.Active);
        var before = await keyboard.GetStateAsync(default);
        Assert.True((await keyboard.SwitchIfCurrentAsync("host", before.ActiveComputer!, before.Revision, default)).InputTransferred);
        await manager.DisposeAsync();
        Assert.Equal("host", keyboard.Active); // RED: disposal returns to the prior viewer.
    }

    private sealed class SyntheticClock : TimeProvider
    {
        private long elapsed;
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed;
        public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(elapsed);
        public void Advance(TimeSpan time) => elapsed += time.Ticks;
    }

    private sealed class SyntheticKeyboard(string active) : IMultiPeerKeyboard
    {
        public string Active { get; private set; } = active;
        public long Revision { get; private set; } = 1;
        public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
        public Task<DeskLinkState> GetStateAsync(CancellationToken ct) => Task.FromResult(new DeskLinkState
        {
            ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true,
            ActiveComputer = Active, Revision = Revision, ConnectedComputers = ["peer"]
        });
        public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long revision, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Active != expected || Revision != revision) return Task.FromResult(new DeskLinkSwitchResult { Error = "stale" });
            Active = target; Revision++;
            return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target });
        }
        public Task<DeskLinkState> StopAsync(CancellationToken ct) => GetStateAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SyntheticDisplay : IUnifiedDisplayControl
    {
        public int ApplyCalls { get; private set; }
        public Task<DisplaySwitchOutcome> ApplyAsync(string target, CancellationToken ct)
        { ApplyCalls++; return Task.FromResult(new DisplaySwitchOutcome("verified")); }
    }
}
