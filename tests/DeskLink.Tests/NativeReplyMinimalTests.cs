using System.Text.Json;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class NativeLateReplyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentSwitchReplyRemainsUsable(bool conditional)
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
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = true }, deps,
            conditional ? Path.Combine(folder.Path, "synthetic-bindings.json") : null);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, state) => { if (state.NativeControlAvailable) ready.TrySetResult(); };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await session.StartAsync();
            using (var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = request.RootElement.GetProperty("requestId").GetString(),
                    ok = true, activeComputer = "My-Mac", connectedComputers = new[] { "My-Mac" }, revision = 4 }, deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            var pending = conditional ? session.SwitchIfCurrentAsync("Win-PC","My-Mac",4,deadline.Token) : session.SwitchAsync("Win-PC",deadline.Token);
            using (var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = request.RootElement.GetProperty("requestId").GetString(),
                    ok = true, activeComputer = "Win-PC", connectedComputers = new[] { "My-Mac" }, revision = 5 }, deadline.Token);
            var result = await pending.WaitAsync(deadline.Token);
            Assert.True(result.Ok);
            Assert.Equal("Win-PC",session.CurrentState.ActiveComputer);
            Assert.True(session.CurrentState.NativeControlAvailable);
        }
        finally { process.Exit(); await ipcServer.DisposeAsync(); await nativeServer.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderSwitchReplyAfterNewerFocusIsUnknown(bool conditional)
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
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = true }, deps,
            conditional ? Path.Combine(folder.Path, "synthetic-bindings.json") : null);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, state) => { if (state.NativeControlAvailable) ready.TrySetResult(); if (state.Revision == 6) newer.TrySetResult(); };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await session.StartAsync();
            using (var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = request.RootElement.GetProperty("requestId").GetString(),
                    ok = true, activeComputer = "My-Mac", connectedComputers = new[] { "My-Mac" }, revision = 4 }, deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            var pending = conditional ? session.SwitchIfCurrentAsync("Win-PC","My-Mac",4,deadline.Token) : session.SwitchAsync("Win-PC",deadline.Token);
            using var command = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!);
            await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, @event="focusChanged", activeComputer = "My-Mac", revision = 6 }, deadline.Token);
            await newer.Task.WaitAsync(deadline.Token);
            await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = command.RootElement.GetProperty("requestId").GetString(),
                ok = true, activeComputer = "Win-PC", connectedComputers = new[] { "My-Mac" }, revision = 5 }, deadline.Token);
            await Assert.ThrowsAsync<IOException>(() => pending);
            Assert.Equal(6,session.CurrentState.Revision);
            Assert.Equal("My-Mac",session.CurrentState.ActiveComputer);
        }
        finally { process.Exit(); await ipcServer.DisposeAsync(); await nativeServer.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerExitDuringStateObserverCannotReturnSwitchSuccess(bool conditional)
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
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = true }, deps,
            conditional ? Path.Combine(folder.Path, "synthetic-bindings.json") : null);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        session.StateChanged += (_, state) =>
        {
            if (state.NativeControlAvailable && state.Revision == 4) ready.TrySetResult();
            if (state.ProcessState == DeskLinkProcessState.Stopped) stopped.TrySetResult();
            if (state.NativeControlAvailable && state.Revision == 5) { observerEntered.TrySetResult(); release.Wait(deadline.Token); }
        };
        Task<NativeControlReply>? pending=null;
        try
        {
            await session.StartAsync();
            using (var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = request.RootElement.GetProperty("requestId").GetString(),
                    ok = true, activeComputer = "My-Mac", connectedComputers = new[] { "My-Mac" }, revision = 4 }, deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            pending = conditional ? session.SwitchIfCurrentAsync("Win-PC","My-Mac",4,deadline.Token) : session.SwitchAsync("Win-PC",deadline.Token);
            using (var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = request.RootElement.GetProperty("requestId").GetString(),
                    ok = true, activeComputer = "Win-PC", connectedComputers = new[] { "My-Mac" }, revision = 5 }, deadline.Token);
            await observerEntered.Task.WaitAsync(deadline.Token);
            process.Exit();
            await stopped.Task.WaitAsync(deadline.Token);
            release.Set();
            await Assert.ThrowsAsync<IOException>(() => pending);
        }
        finally
        {
            release.Set(); process.Exit();
            if (pending is not null) try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            await ipcServer.DisposeAsync(); await nativeServer.DisposeAsync();
        }
    }

    [Fact]
    public async Task OwnerExitDuringStateObserverCannotReturnStaleRead()
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
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        session.StateChanged += (_, state) =>
        {
            if (state.NativeControlAvailable && state.Revision == 4) ready.TrySetResult();
            if (state.ProcessState == DeskLinkProcessState.Stopped) stopped.TrySetResult();
            if (state.NativeControlAvailable && state.Revision == 5) { observerEntered.TrySetResult(); release.Wait(deadline.Token); }
        };
        Task<DeskLinkState>? pending=null;
        try
        {
            await session.StartAsync();
            using (var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = request.RootElement.GetProperty("requestId").GetString(),
                    ok = true, activeComputer = "My-Mac", connectedComputers = new[] { "My-Mac" }, revision = 4 }, deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            pending = session.GetStateAsync(deadline.Token);
            using (var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = request.RootElement.GetProperty("requestId").GetString(),
                    ok = true, activeComputer = "My-Mac", connectedComputers = new[] { "My-Mac" }, revision = 5 }, deadline.Token);
            await observerEntered.Task.WaitAsync(deadline.Token);
            process.Exit();
            await stopped.Task.WaitAsync(deadline.Token);
            release.Set();
            var result = await pending.WaitAsync(deadline.Token);
            Assert.False(result.NativeControlAvailable);
            Assert.Empty(result.ConnectedComputers);
        }
        finally
        {
            release.Set(); process.Exit();
            if (pending is not null) try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            await ipcServer.DisposeAsync(); await nativeServer.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("switch")]
    [InlineData("conditional")]
    public async Task ExitedOwnerCannotAdmitNewNativeRequest(string kind)
    {
        using var folder = new TestFolder();
        var (ipcClient, ipcServer) = ControlProtocolTests.Duplex();
        var (nativeClient, nativeServer) = ControlProtocolTests.Duplex();
        var tracked = new CountingWriteStream(nativeClient);
        var process = new FakeProcess();
        var deps = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(ipcClient),
            ConnectNativeAsync = (_, _) => Task.FromResult(new NativeControlClient(tracked))
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = true }, deps,
            kind == "conditional" ? Path.Combine(folder.Path, "synthetic-bindings.json") : null);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, state) => { if (state.NativeControlAvailable) ready.TrySetResult(); };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var operation = (SemaphoreSlim)typeof(DeskflowSession).GetField("operation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session)!;
        var holding=false; Task? pending=null;
        try
        {
            await session.StartAsync();
            using (var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = request.RootElement.GetProperty("requestId").GetString(),
                    ok = true, activeComputer = "My-Mac", connectedComputers = new[] { "My-Mac" }, revision = 4 }, deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            await operation.WaitAsync(deadline.Token); holding=true;
            process.Exit();
            Assert.Equal(DeskLinkProcessState.Running, session.CurrentState.ProcessState);
            var before=tracked.Writes;
            pending = kind switch {
                "read" => session.GetStateAsync(deadline.Token),
                "switch" => session.SwitchAsync("Win-PC",deadline.Token),
                _ => session.SwitchIfCurrentAsync("Win-PC","My-Mac",4,deadline.Token)
            };
            Assert.Equal(before, tracked.Writes);
            var error=await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            if (kind != "read") Assert.IsType<IOException>(error);
            else Assert.True(error is null or IOException);
            Assert.False(session.CurrentState.NativeControlAvailable);
            Assert.Empty(session.CurrentState.ConnectedComputers);
        }
        finally
        {
            deadline.Cancel(); process.Exit();
            if (holding) operation.Release();
            if (pending is not null) try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            await ipcServer.DisposeAsync(); await nativeServer.DisposeAsync();
        }
    }

    private sealed class CountingWriteStream(Stream inner) : Stream
    {
        private int writes;
        public int Writes => Volatile.Read(ref writes);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer,int offset,int count) => inner.Read(buffer,offset,count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default) => inner.ReadAsync(buffer,ct);
        public override void Write(byte[] buffer,int offset,int count) { Interlocked.Increment(ref writes); inner.Write(buffer,offset,count); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default) { Interlocked.Increment(ref writes); return inner.WriteAsync(buffer,ct); }
        public override long Seek(long offset,SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task NormalFocusObserverKeepsReaderAndSubsequentRequestsUsable()
    {
        using var folder = new TestFolder();
        var (ipcClient, ipcServer) = ControlProtocolTests.Duplex();
        var (nativeClient, nativeServer) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        NativeControlClient? connection = null;
        var deps = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(ipcClient),
            ConnectNativeAsync = (_, _) => Task.FromResult(connection = new NativeControlClient(nativeClient))
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = true }, deps);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var focus = new TaskCompletionSource<FocusChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, state) => { if (state.NativeControlAvailable) ready.TrySetResult(); };
        session.FocusChanged += (_, item) => focus.TrySetResult(item);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await session.StartAsync();
            using (var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = request.RootElement.GetProperty("requestId").GetString(),
                    ok = true, activeComputer = "My-Mac", connectedComputers = new[] { "My-Mac" }, revision = 4 }, deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, @event="focusChanged", activeComputer = "Win-PC", revision = 5 }, deadline.Token);
            Assert.Equal(5,(await focus.Task.WaitAsync(deadline.Token)).Revision);
            Assert.False(connection!.IsDisconnected);
            var pending=session.GetStateAsync(deadline.Token);
            using (var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer, deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer, new { version = 1, requestId = request.RootElement.GetProperty("requestId").GetString(),
                    ok = true, activeComputer = "Win-PC", connectedComputers = new[] { "My-Mac" }, revision = 5 }, deadline.Token);
            var result=await pending.WaitAsync(deadline.Token);
            Assert.True(result.NativeControlAvailable);
            Assert.Equal("Win-PC",result.ActiveComputer);
        }
        finally { process.Exit(); await ipcServer.DisposeAsync(); await nativeServer.DisposeAsync(); }
    }

    [Fact]
    public async Task StateReadDuringReleasedNativeBeforeRestartCannotReturnOldAvailableState()
    {
        using var folder = new TestFolder();
        var (ipcClient, ipcServer) = ControlProtocolTests.Duplex();
        var (nativeClient, nativeServer) = ControlProtocolTests.Duplex();
        var process = new DelayedDisposeProcess();
        var starts=0;
        var deps = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Interlocked.Increment(ref starts)==1
                ? Task.FromResult<IDeskflowOwnedProcess>(process)
                : Task.FromException<IDeskflowOwnedProcess>(new IOException("synthetic restart intentionally stops")),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(ipcClient),
            ConnectNativeAsync = (_, _) => Task.FromResult(new NativeControlClient(nativeClient))
        };
        var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = true }, deps);
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, state) => { if(state.NativeControlAvailable)ready.TrySetResult(); };
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<DeskLinkState>? restart=null;
        try
        {
            await session.StartAsync();
            using (var request=JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer,deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer,new { version=1, requestId=request.RootElement.GetProperty("requestId").GetString(),
                    ok=true, activeComputer="My-Mac", connectedComputers=new[] {"My-Mac"}, revision=4 },deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            process.MarkExitedBeforeCompletion();
            restart=session.StartAsync(deadline.Token);
            await process.DisposeEntered.Task.WaitAsync(deadline.Token);
            Assert.Equal(DeskLinkProcessState.Running,session.CurrentState.ProcessState);
            var result=await session.GetStateAsync(deadline.Token);
            Assert.False(result.NativeControlAvailable);
            Assert.Empty(result.ConnectedComputers);
        }
        finally
        {
            process.ReleaseDispose();
            if(restart is not null) try { await restart.WaitAsync(TimeSpan.FromSeconds(5)); } catch(Exception) { }
            await ipcServer.DisposeAsync(); await nativeServer.DisposeAsync();
            try { await session.DisposeAsync(); } catch(Exception) { }
        }
    }

    private sealed class DelayedDisposeProcess : IDeskflowOwnedProcess
    {
        private readonly TaskCompletionSource<int> completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int exited;
        public TaskCompletionSource DisposeEntered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id=>5678;
        public bool HasExited=>Volatile.Read(ref exited)!=0;
        public Task<int> Completion=>completion.Task;
        public void MarkExitedBeforeCompletion()=>Volatile.Write(ref exited,1);
        public void ReleaseDispose(){ release.TrySetResult(); completion.TrySetResult(0); }
        public Task StopAsync(CancellationToken ct=default){ MarkExitedBeforeCompletion(); ReleaseDispose(); return Task.CompletedTask; }
        public async ValueTask DisposeAsync(){ DisposeEntered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public async Task RejectedNativeReplyMustStillRespectApprovedNames(bool conditional,bool badConnected)
    {
        using var folder=new TestFolder();
        var (ipcClient,ipcServer)=ControlProtocolTests.Duplex();
        var (nativeClient,nativeServer)=ControlProtocolTests.Duplex();
        var process=new FakeProcess();
        var deps=new DeskflowSessionDependencies {
            HasConflictingCore=_=>false,
            StartProcessAsync=(_,_)=>Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync=(_,_)=>Task.FromResult(ipcClient),
            ConnectNativeAsync=(_,_)=>Task.FromResult(new NativeControlClient(nativeClient))
        };
        await using var session=new DeskflowSession(ProfileTests.Options(folder.Path) with {UseNativeControl=true},deps,
            conditional?Path.Combine(folder.Path,"synthetic-bindings.json"):null);
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged+=(_,state)=>{if(state.NativeControlAvailable)ready.TrySetResult();};
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await session.StartAsync();
            using (var request=JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer,deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer,new {version=1,requestId=request.RootElement.GetProperty("requestId").GetString(),
                    ok=true,activeComputer="My-Mac",connectedComputers=new[] {"My-Mac"},revision=4},deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            var pending=conditional ? session.SwitchIfCurrentAsync("Win-PC","My-Mac",4,deadline.Token) : session.SwitchAsync("Win-PC",deadline.Token);
            using (var request=JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer,deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer,new {version=1,requestId=request.RootElement.GetProperty("requestId").GetString(),
                    ok=false,activeComputer=badConnected ? "My-Mac" : "Unexpected",connectedComputers=badConnected ? new[] {"Unexpected"} : new[] {"My-Mac"},
                    revision=4,error="focus_changed"},deadline.Token);
            await Assert.ThrowsAsync<IOException>(()=>pending);
            using var noFallback=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>JsonLineProtocol.ReadAsync(nativeServer,noFallback.Token));
        }
        finally {process.Exit();await ipcServer.DisposeAsync();await nativeServer.DisposeAsync();}
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidNewerRejectionRefreshesFocusAndRemainsRejected(bool conditional)
    {
        using var folder=new TestFolder();
        var (ipcClient,ipcServer)=ControlProtocolTests.Duplex();
        var (nativeClient,nativeServer)=ControlProtocolTests.Duplex();
        var process=new FakeProcess();
        var deps=new DeskflowSessionDependencies {
            HasConflictingCore=_=>false,
            StartProcessAsync=(_,_)=>Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync=(_,_)=>Task.FromResult(ipcClient),
            ConnectNativeAsync=(_,_)=>Task.FromResult(new NativeControlClient(nativeClient))
        };
        await using var session=new DeskflowSession(ProfileTests.Options(folder.Path) with {UseNativeControl=true},deps,
            conditional?Path.Combine(folder.Path,"synthetic-bindings.json"):null);
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged+=(_,state)=>{if(state.NativeControlAvailable)ready.TrySetResult();};
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await session.StartAsync();
            using (var request=JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer,deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer,new {version=1,requestId=request.RootElement.GetProperty("requestId").GetString(),
                    ok=true,activeComputer="My-Mac",connectedComputers=new[] {"My-Mac"},revision=4},deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            var pending=conditional ? session.SwitchIfCurrentAsync("Win-PC","My-Mac",4,deadline.Token) : session.SwitchAsync("Win-PC",deadline.Token);
            using (var request=JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer,deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer,new {version=1,requestId=request.RootElement.GetProperty("requestId").GetString(),
                    ok=false,activeComputer="Win-PC",connectedComputers=new[] {"My-Mac"},revision=5,error="focus_changed"},deadline.Token);
            var rejected=await pending.WaitAsync(deadline.Token);
            Assert.False(rejected.Ok); Assert.Equal("focus_changed",rejected.Error);
            Assert.Equal("Win-PC",session.CurrentState.ActiveComputer);
            Assert.Equal(5,session.CurrentState.Revision);
            using var noFallback=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>JsonLineProtocol.ReadAsync(nativeServer,noFallback.Token));
        }
        finally {process.Exit();await ipcServer.DisposeAsync();await nativeServer.DisposeAsync();}
    }

    [Fact]
    public async Task RestartDoesNotPublishNewOwnerBeforeOldNativeCleanupCompletes()
    {
        using var folder=new TestFolder();
        var (ipc1,ipcServer1)=ControlProtocolTests.Duplex();
        var (native1,nativeServer1)=ControlProtocolTests.Duplex();
        var (ipc2,ipcServer2)=ControlProtocolTests.Duplex();
        var (native2,nativeServer2)=ControlProtocolTests.Duplex();
        var first=new FakeProcess(); var second=new FakeProcess();
        NativeControlClient? firstClient=null; var starts=0;var ipcs=0;var natives=0;
        var deps=new DeskflowSessionDependencies
        {
            HasConflictingCore=_=>false,
            StartProcessAsync=(_,_)=>Task.FromResult<IDeskflowOwnedProcess>(Interlocked.Increment(ref starts)==1 ? first : second),
            ConnectCoreIpcAsync=(_,_)=>Task.FromResult(Interlocked.Increment(ref ipcs)==1 ? ipc1 : ipc2),
            ConnectNativeAsync=(_,_) =>
            {
                var count=Interlocked.Increment(ref natives);
                var client=new NativeControlClient(count==1 ? native1 : native2);
                if(count==1) firstClient=client;
                return Task.FromResult(client);
            }
        };
        await using var session=new DeskflowSession(ProfileTests.Options(folder.Path) with {UseNativeControl=true},deps);
        var ready1=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready2=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        session.StateChanged+=(_,state)=>
        {
            if(state.NativeControlAvailable && Volatile.Read(ref starts)==1)ready1.TrySetResult();
            if(state.NativeControlAvailable && Volatile.Read(ref starts)==2)ready2.TrySetResult();
            if(ready1.Task.IsCompleted && state.ProcessState==DeskLinkProcessState.Stopped)stopped.TrySetResult();
        };
        session.FocusChanged+=(_,_)=>{entered.TrySetResult();release.Wait(deadline.Token);};
        Task<DeskLinkState>? restart=null;
        try
        {
            await session.StartAsync();
            using (var request=JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer1,deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer1,new {version=1,requestId=request.RootElement.GetProperty("requestId").GetString(),
                    ok=true,activeComputer="My-Mac",connectedComputers=new[] {"My-Mac"},revision=4},deadline.Token);
            await ready1.Task.WaitAsync(deadline.Token);
            await JsonLineProtocol.WriteAsync(nativeServer1,new {version=1,@event="focusChanged",activeComputer="Win-PC",revision=5},deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            first.Exit();await stopped.Task.WaitAsync(deadline.Token);
            restart=session.StartAsync(deadline.Token);
            if(!SpinWait.SpinUntil(()=>firstClient!.IsDisconnected,TimeSpan.FromSeconds(3)))
                throw new TimeoutException("Old synthetic native client did not begin disposal.");
            Assert.False(restart.IsCompleted);
            Assert.Equal(1,Volatile.Read(ref starts));
            var during=await session.GetStateAsync(deadline.Token);
            Assert.False(during.NativeControlAvailable);
            release.Set();
            await restart.WaitAsync(deadline.Token);
            using (var request=JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer2,deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer2,new {version=1,requestId=request.RootElement.GetProperty("requestId").GetString(),
                    ok=true,activeComputer="My-Mac",connectedComputers=new[] {"My-Mac"},revision=4},deadline.Token);
            await ready2.Task.WaitAsync(deadline.Token);
            Assert.Equal(2,Volatile.Read(ref starts));
            Assert.True(session.CurrentState.NativeControlAvailable);
        }
        finally
        {
            release.Set();first.Exit();second.Exit();
            if(restart is not null)try{await restart.WaitAsync(TimeSpan.FromSeconds(5));}catch(Exception){}
            await ipcServer1.DisposeAsync();await ipcServer2.DisposeAsync();await nativeServer1.DisposeAsync();await nativeServer2.DisposeAsync();
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
