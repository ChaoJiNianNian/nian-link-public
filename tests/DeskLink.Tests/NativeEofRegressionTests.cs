using System.Text.Json;
using DesktopToolbox.DeskLink;
namespace DeskLink.Tests;

public sealed class NativeEofRegressionTests
{
    [Fact]
    public async Task ChannelEofDuringStopDoesNotPublishLateRunningDisconnect()
    {
        using var folder = new TestFolder();
        var (ipcClient, ipcServer) = ControlProtocolTests.Duplex();
        var (nativeClient, nativeServer) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        NativeControlClient? channel = null;
        var deps = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(ipcClient),
            ConnectNativeAsync = (_, _) => Task.FromResult(channel = new NativeControlClient(nativeClient))
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = true }, deps);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = 0;
        session.StateChanged += (_, state) =>
        {
            if (state.NativeControlAvailable) ready.TrySetResult();
            if (state.ProcessState == DeskLinkProcessState.Stopping) stopped.TrySetResult();
            if (state.LastError?.StartsWith("Native control disconnected:", StringComparison.Ordinal) == true)
                Interlocked.Increment(ref errors);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await session.StartAsync();
            using var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!);
            await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = request.RootElement.GetProperty("requestId").GetString(),
                ok = true, activeComputer = "My-Mac", connectedComputers = new[] { "My-Mac" }, revision = 4 }, deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            var stopping = session.StopAsync();
            await stopped.Task.WaitAsync(deadline.Token);
            var lifetime = (CancellationTokenSource)typeof(DeskflowSession)
                .GetField("running", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session)!;
            if (!SpinWait.SpinUntil(() => lifetime.IsCancellationRequested, TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Synthetic session did not revoke on stop.");
            await nativeServer.DisposeAsync();
            var terminal = typeof(NativeControlClient).GetField("terminalError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            if (!SpinWait.SpinUntil(() => terminal.GetValue(channel) is not null, TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Synthetic EOF not observed during stop.");
            Assert.Equal(DeskLinkProcessState.Stopping, session.CurrentState.ProcessState);
            process.Exit();
            await stopping.WaitAsync(deadline.Token);
            Assert.Equal(DeskLinkProcessState.Stopped, session.CurrentState.ProcessState);
            Assert.Empty(session.CurrentState.ConnectedComputers);
            Assert.Equal(0, Volatile.Read(ref errors));
        }
        finally
        {
            process.Exit();
            await ipcServer.DisposeAsync();
            await nativeServer.DisposeAsync();
        }
    }

    [Fact]
    public async Task ChannelClosedBeforeSubscriptionStillReportsUnavailable()
    {
        using var folder = new TestFolder();
        var (ipcClient, ipcServer) = ControlProtocolTests.Duplex();
        var (nativeClient, nativeServer) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        await nativeServer.DisposeAsync();
        var deps = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(ipcClient),
            ConnectNativeAsync = (_, _) =>
            {
                var client = new NativeControlClient(nativeClient);
                var field = typeof(NativeControlClient).GetField("terminalError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                if (!SpinWait.SpinUntil(() => field.GetValue(client) is not null, TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Synthetic EOF was not observed before session event subscription.");
                return Task.FromResult(client);
            }
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = true }, deps);
        try
        {
            await session.StartAsync();
            var field = typeof(DeskflowSession).GetField("connections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            await ((Task)field.GetValue(session)!).WaitAsync(TimeSpan.FromSeconds(5));
            var state = session.CurrentState;
            Assert.False(state.NativeControlAvailable);
            Assert.Empty(state.ConnectedComputers);
            Assert.NotNull(state.LastError);
        }
        finally
        {
            process.Exit();
            await ipcServer.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisconnectNotificationMustNotContainOldPeerConnectionEvidence()
    {
        using var folder = new TestFolder();
        var (ipcClient, ipcServer) = ControlProtocolTests.Duplex();
        var (nativeClient, nativeServer) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var deps = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(ipcClient),
            ConnectNativeAsync = (_, _) => Task.FromResult(new NativeControlClient(nativeClient))
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = true }, deps);
        var ready = new TaskCompletionSource<DeskLinkState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<DeskLinkState>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, state) =>
        {
            if (state.NativeControlAvailable && state.ConnectedComputers.Contains("My-Mac")) ready.TrySetResult(state);
            if (state.LastError?.StartsWith("Native control disconnected:", StringComparison.Ordinal) == true) disconnected.TrySetResult(state);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await session.StartAsync();
            using var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!);
            await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = request.RootElement.GetProperty("requestId").GetString(),
                ok = true, activeComputer = "My-Mac", connectedComputers = new[] { "My-Mac" }, revision = 4 }, deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            await nativeServer.DisposeAsync();
            var lost = await disconnected.Task.WaitAsync(deadline.Token);
            Assert.False(lost.NativeControlAvailable);
            Assert.Empty(lost.ConnectedComputers);
        }
        finally
        {
            process.Exit();
            await ipcServer.DisposeAsync();
            await nativeServer.DisposeAsync();
        }
    }
    private sealed class FakeProcess : IDeskflowOwnedProcess
    {
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => 5678;
        public bool HasExited => completion.Task.IsCompleted;
        public Task<int> Completion => completion.Task;
        public void Exit() => completion.TrySetResult(0);
        public Task StopAsync(CancellationToken ct = default) { Exit(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
