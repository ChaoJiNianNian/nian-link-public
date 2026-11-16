using System.Reflection;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacLinkStateOrderingTests
{
    [Fact]
    public async Task DelayedOlderConnectedEventCannotOverwriteNewerDisconnect()
    {
        await using var owner = new MacLinkService(Root());
        await using var current = Session();
        SetSession(owner, current);
        Send(owner, current, State(2, DeskLinkConnectionState.Disconnected));
        Send(owner, current, State(1, DeskLinkConnectionState.Connected));
        Assert.False(Snapshot(owner).IsConnected);
    }

    [Fact]
    public async Task OldSessionEventCannotReplaceNewSessionState()
    {
        await using var owner = new MacLinkService(Root());
        await using var old = Session();
        await using var current = Session();
        SetSession(owner, current);
        Send(owner, current, State(1, DeskLinkConnectionState.Disconnected));
        Send(owner, old, State(99, DeskLinkConnectionState.Connected));
        Assert.False(Snapshot(owner).IsConnected);
    }

    [Fact]
    public async Task AwaitedOldReadCannotReplaceANewerEventAndNewSessionMayRestartVersion()
    {
        await using var owner = new MacLinkService(Root());
        await using var old = Session();
        await using var current = Session();
        Replace(owner, old);
        Assert.True(Apply(owner, old, State(7, DeskLinkConnectionState.Connected)));
        Send(owner, old, State(8, DeskLinkConnectionState.Disconnected));
        Assert.False(Apply(owner, old, State(7, DeskLinkConnectionState.Connected)));
        Assert.False(Snapshot(owner).IsConnected);
        Replace(owner, current);
        Assert.True(Apply(owner, current, State(1, DeskLinkConnectionState.Connected)));
        Assert.True(Snapshot(owner).IsConnected);
        Assert.False(Apply(owner, old, State(99, DeskLinkConnectionState.Disconnected)));
        Assert.True(Snapshot(owner).IsConnected);
    }

    [Fact]
    public async Task SlowObserverDoesNotBlockANewerStateCommit()
    {
        await using var owner = new MacLinkService(Root());
        await using var current = Session();
        Replace(owner, current);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        owner.Changed += value => { if (value.IsConnected) { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); } };
        var older = Task.Run(() => Send(owner, current, State(1, DeskLinkConnectionState.Connected)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            await Task.Run(() => Send(owner, current, State(2, DeskLinkConnectionState.Disconnected))).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(Snapshot(owner).IsConnected);
        }
        finally { release.Set(); await older; }
    }

    [Fact]
    public async Task FailedSessionCleanupRetainsOwnershipWithoutAcceptingLateConnectedEvents()
    {
        await using var owner = new MacLinkService(Root());
        await using var current = Session();
        Replace(owner, current);
        Send(owner, current, State(4, DeskLinkConnectionState.Connected));
        typeof(MacLinkService).GetMethod("DetachSession", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, [current]);
        typeof(MacLinkService).GetMethod("RestoreFailedSession", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, [current]);
        Send(owner, current, State(99, DeskLinkConnectionState.Connected));
        var value = Snapshot(owner);
        Assert.False(value.IsConnected);
        Assert.Equal(DeskLinkProcessState.Faulted, value.PreviewLocalCoreState.ProcessState);
        Assert.Same(current, typeof(MacLinkService).GetField("session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner));
    }

    [Fact]
    public async Task FaultedStateWithLiveProcessDoesNotClaimTheCoreStopped()
    {
        await using var owner = new MacLinkService(Root());
        await using var current = Session();
        Replace(owner, current);
        Send(owner, current, new DeskLinkState { SessionStateVersion = 1, ProcessState = DeskLinkProcessState.Faulted, ProcessId = 123, ConnectionState = DeskLinkConnectionState.Disconnected });
        Assert.DoesNotContain("已停止", Snapshot(owner).StatusMessage);
    }

    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tests", ".work", "mac-state-order-" + Guid.NewGuid().ToString("N")));
    private static DeskflowSession Session() => new(new DeskflowSessionOptions
    {
        EnginePath = Path.Combine(Root(), "nonexistent-engine"), ProfileDirectory = Root(),
        LocalName = "Local", RemoteName = "Remote", Role = DeskLinkRole.Server
    });
    private static DeskLinkState State(long version, DeskLinkConnectionState connection) => new()
    { SessionStateVersion = version, ProcessState = DeskLinkProcessState.Running, ConnectionState = connection };
    private static void SetSession(MacLinkService owner, DeskflowSession session) => Replace(owner, session);
    private static void Send(MacLinkService owner, DeskflowSession sender, DeskLinkState state) =>
        typeof(MacLinkService).GetMethod("OnSessionChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, [sender, state]);
    private static void Replace(MacLinkService owner, DeskflowSession? session) =>
        typeof(MacLinkService).GetMethod("ReplaceSession", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, [session]);
    private static bool Apply(MacLinkService owner, DeskflowSession sender, DeskLinkState state) =>
        (bool)typeof(MacLinkService).GetMethod("ApplySessionState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, [sender, state])!;
    private static MacLinkSnapshot Snapshot(MacLinkService owner) =>
        (MacLinkSnapshot)typeof(MacLinkService).GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null)!;
}
