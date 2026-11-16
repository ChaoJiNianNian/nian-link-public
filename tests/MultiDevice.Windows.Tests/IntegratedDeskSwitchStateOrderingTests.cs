using System.Reflection;
using System.IO;
using DesktopToolbox.Core;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Windows.DeskSwitch;

namespace MultiDevice.Windows.Tests;

public sealed class IntegratedDeskSwitchStateOrderingTests
{
    [Fact]
    public async Task StopWaitingForCleanupReportsStoppingAndDisconnected()
    {
        await using var owner = Create(); await using var current = Session();
        SetSession(owner, current); Send(owner, current, State(1, DeskLinkConnectionState.Connected));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(IntegratedDeskSwitchService).GetField("previewWatch", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, release.Task);
        var stop = owner.StopAsync(CancellationToken.None);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(2);
            while (typeof(IntegratedDeskSwitchService).GetField("session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner) is not null && DateTime.UtcNow < until) await Task.Yield();
            Assert.Null(typeof(IntegratedDeskSwitchService).GetField("session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner));
            Assert.False(stop.IsCompleted);
            Assert.Equal(DeskLinkProcessState.Stopping, owner.Snapshot.Session.ProcessState);
            Assert.Equal(DeskLinkConnectionState.Disconnected, owner.Snapshot.Session.ConnectionState);
        }
        finally { release.TrySetResult(); await stop; }
    }
    [Fact]
    public async Task SnapshotSeesAuthoritativeDisconnectBeforeBlockedSessionObserver()
    {
        await using var owner = Create();
        await using var current = Session();
        SetSession(owner, current);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        EventHandler<DeskLinkState> blocker = (_, value) => { if (value.ConnectionState == DeskLinkConnectionState.Disconnected) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); } };
        current.StateChanged += blocker;
        current.StateChanged += (_, value) => Send(owner, current, value);
        Update(current, DeskLinkConnectionState.Connected);
        var delayed = Task.Run(() => Update(current, DeskLinkConnectionState.Disconnected));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(DeskLinkConnectionState.Disconnected, owner.Snapshot.Session.ConnectionState);
        }
        finally { release.Set(); await delayed; current.StateChanged -= blocker; }
    }

    private static void Update(DeskflowSession session, DeskLinkConnectionState connection) =>
        typeof(DeskflowSession).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(session,
            [new Func<DeskLinkState, DeskLinkState>(value => value with { ProcessState = DeskLinkProcessState.Running, ConnectionState = connection })]);
    [Fact]
    public async Task DelayedOlderConnectedEventCannotOverwriteNewerDisconnect()
    {
        await using var owner = Create();
        await using var current = Session();
        SetSession(owner, current);
        Send(owner, current, State(2, DeskLinkConnectionState.Disconnected));
        Send(owner, current, State(1, DeskLinkConnectionState.Connected));
        Assert.Equal(DeskLinkConnectionState.Disconnected, owner.Snapshot.Session.ConnectionState);
    }

    [Fact]
    public async Task OldSessionEventCannotReplaceNewSessionState()
    {
        await using var owner = Create();
        await using var old = Session();
        await using var current = Session();
        SetSession(owner, current);
        Send(owner, current, State(1, DeskLinkConnectionState.Disconnected));
        Send(owner, old, State(99, DeskLinkConnectionState.Connected));
        Assert.Equal(DeskLinkConnectionState.Disconnected, owner.Snapshot.Session.ConnectionState);
    }

    [Fact]
    public async Task AwaitedOldReadCannotReplaceANewerEvent()
    {
        await using var owner = Create();
        await using var current = Session();
        SetSession(owner, current);
        Assert.True(Apply(owner, current, State(7, DeskLinkConnectionState.Connected)));
        Send(owner, current, State(8, DeskLinkConnectionState.Disconnected));
        Assert.False(Apply(owner, current, State(7, DeskLinkConnectionState.Connected)));
        Assert.Equal(DeskLinkConnectionState.Disconnected, owner.Snapshot.Session.ConnectionState);
    }

    [Fact]
    public async Task DetachedSessionCannotRestoreConnectionEvenWhenKeptForCleanup()
    {
        await using var owner = Create();
        await using var current = Session();
        SetSession(owner, current);
        Send(owner, current, State(4, DeskLinkConnectionState.Disconnected));
        typeof(IntegratedDeskSwitchService).GetField("acceptSessionUpdates", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, false);
        Send(owner, current, State(99, DeskLinkConnectionState.Connected));
        Assert.Equal(DeskLinkConnectionState.Disconnected, owner.Snapshot.Session.ConnectionState);
    }

    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", ".work", "windows-state-order-" + Guid.NewGuid().ToString("N")));
    private static IntegratedDeskSwitchService Create()
    {
        var paths = new AppPaths(Root());
        return new(new ModuleContext(paths, new JsonFileStore(paths), new NoPrompts(), (_, _) => { }), Root(), new NoDisplay());
    }
    private static DeskflowSession Session() => new(new DeskflowSessionOptions
    {
        EnginePath = Path.Combine(Root(), "nonexistent-engine"), ProfileDirectory = Root(),
        LocalName = "Local", RemoteName = "Remote", Role = DeskLinkRole.Server
    });
    private static DeskLinkState State(long version, DeskLinkConnectionState connection) => new()
    { SessionStateVersion = version, ProcessState = DeskLinkProcessState.Running, ConnectionState = connection };
    private static void SetSession(IntegratedDeskSwitchService owner, DeskflowSession session)
    {
        typeof(IntegratedDeskSwitchService).GetField("session", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, session);
        typeof(IntegratedDeskSwitchService).GetField("acceptSessionUpdates", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(owner, true);
    }
    private static void Send(IntegratedDeskSwitchService owner, DeskflowSession sender, DeskLinkState state) =>
        typeof(IntegratedDeskSwitchService).GetMethod("OnState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, [sender, state]);
    private static bool Apply(IntegratedDeskSwitchService owner, DeskflowSession sender, DeskLinkState state) =>
        (bool)typeof(IntegratedDeskSwitchService).GetMethod("ApplySessionState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, [sender, state])!;
    private sealed class NoDisplay : IDisplayBridge
    {
        public Task<DisplayMonitorInfo[]> ListAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task ApplyAsync(string file, string target, bool test, CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class NoPrompts : IUserPrompts
    {
        public Task<bool> ConfirmAsync(string title, string message) => throw new InvalidOperationException();
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => throw new InvalidOperationException();
        public Task<string?> PickFileAsync(string title, string filter) => throw new InvalidOperationException();
        public Task RevealAsync(string path, bool selectFile = false) => throw new InvalidOperationException();
    }
}
