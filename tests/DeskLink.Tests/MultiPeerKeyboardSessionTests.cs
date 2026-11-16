using System.Security.Cryptography;
using System.Net;
using System.Net.NetworkInformation;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class MultiPeerKeyboardSessionTests
{
    [Fact]
    public async Task VerifiedEngineUsesOnlyConfirmedIdentitiesAndPrivateBindingFile()
    {
        using var folder = new TestFolder();
        var engine = Path.Combine(folder.Path, "synthetic-engine.exe");
        await File.WriteAllTextAsync(engine, "not an executable");
        var engineHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(engine)));
        var profile = Path.Combine(folder.Path, "profile");
        var process = new FakeProcess();
        string? bindingPath = null;
        var (ipcClient, ipcServer) = ControlProtocolTests.Duplex();
        var (nativeClient, nativeServer) = ControlProtocolTests.Duplex();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (start, _) =>
            {
                bindingPath = start.Environment["NIANDESKLINK_PEER_BINDINGS"];
                return Task.FromResult<IDeskflowOwnedProcess>(process);
            },
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(ipcClient),
            ConnectNativeAsync = (_, _) => Task.FromResult(new NativeControlClient(nativeClient))
        };
        await using var host = new MultiPeerKeyboardSession(engine, engineHash, profile, "Win-PC",
            [new("Win-Two", new string('1', 64)), new("Mac-Two", new string('2', 64))], dependencies);
        await host.StartAsync();
        Assert.Equal(Path.Combine(profile, "peer-bindings.json"), bindingPath);
        var bindings = await File.ReadAllTextAsync(bindingPath!);
        Assert.Contains("Win-Two", bindings);
        Assert.Contains("Mac-Two", bindings);
        Assert.DoesNotContain("token", bindings, StringComparison.OrdinalIgnoreCase);
        var trust = await File.ReadAllTextAsync(Path.Combine(profile, "tls", "trusted-clients"));
        Assert.Contains("v2:sha256:" + new string('1', 64), trust);
        Assert.Contains("v2:sha256:" + new string('2', 64), trust);
        var settings = await File.ReadAllTextAsync(Path.Combine(profile, "Deskflow.conf"));
        Assert.Contains("[computer_Win-Two]", settings);
        Assert.Contains("[computer_Mac-Two]", settings);
        Assert.Contains("interface=127.0.0.1", settings);
        process.Exit();
        await ipcServer.DisposeAsync();
        await nativeServer.DisposeAsync();
    }

    [Fact]
    public async Task WrongEngineHashDoesNotStartOrWriteTrust()
    {
        using var folder = new TestFolder();
        var engine = Path.Combine(folder.Path, "synthetic-engine.exe");
        await File.WriteAllTextAsync(engine, "not an executable");
        var started = false;
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => { started = true; throw new InvalidOperationException(); }
        };
        var profile = Path.Combine(folder.Path, "profile");
        await using var host = new MultiPeerKeyboardSession(engine, new string('0', 64), profile, "Win-PC",
            [new("Win-Two", new string('1', 64))], dependencies);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.StartAsync());
        Assert.False(started);
        Assert.False(File.Exists(Path.Combine(profile, "peer-bindings.json")));
    }

    [Fact]
    public async Task UnavailableLanBindingCannotStartKeyboardEngine()
    {
        using var folder = new TestFolder();
        var engine = Path.Combine(folder.Path, "synthetic-engine.exe");
        await File.WriteAllTextAsync(engine, "not an executable");
        var address = IPAddress.Parse("172.30.222.254");
        var snapshot = new LanInterfaceSnapshot("test", "ethernet", NetworkInterfaceType.Ethernet, OperationalStatus.Up, [address]);
        var binding = VerifiedLanBinding.FromSnapshots(address, true, [snapshot]);
        var started = false;
        var deps = new DeskflowSessionDependencies { HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => { started = true; throw new InvalidOperationException(); } };
        Assert.Throws<InvalidDataException>(() => MultiPeerKeyboardSession.CreateForLan(engine, new string('0', 64),
            Path.Combine(folder.Path, "profile"), "Win-PC", [new("Win-Two", new string('1', 64))], binding, deps));
        await using var host = MultiPeerKeyboardSession.CreateForLan(engine, MultiPeerKeyboardSession.ExperimentalEngineSha256, Path.Combine(folder.Path, "profile"),
            "Win-PC", [new("Win-Two", new string('1', 64))], binding, deps);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.False(started);
        Assert.False(Directory.Exists(Path.Combine(folder.Path, "profile")));
    }

    [Fact]
    public async Task MultiplePeerFocusUsesConditionalNativeControlAndRejectsStaleState()
    {
        using var folder = new TestFolder();
        var engine = Path.Combine(folder.Path, "synthetic-engine.exe");
        await File.WriteAllTextAsync(engine, "not an executable");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(engine)));
        var (ipcClient, ipcServer) = ControlProtocolTests.Duplex();
        var (nativeClient, nativeServer) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(ipcClient),
            ConnectNativeAsync = (_, _) => Task.FromResult(new NativeControlClient(nativeClient))
        };
        using var cancellation = new CancellationTokenSource();
        var fakeCore = RunFakeCoreAsync(nativeServer, cancellation.Token);
        await using var host = new MultiPeerKeyboardSession(engine, hash, Path.Combine(folder.Path, "profile"), "Win-PC",
            [new("Win-Two", new string('1', 64)), new("Mac-Two", new string('2', 64))], dependencies);
        await host.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        DeskLinkState state;
        do { await Task.Delay(5, timeout.Token); state = await host.GetStateAsync(timeout.Token); }
        while (!state.NativeControlAvailable);
        Assert.Contains("Win-Two", state.ConnectedComputers);
        Assert.Contains("Mac-Two", state.ConnectedComputers);
        var switched = await host.SwitchIfCurrentAsync("Mac-Two", "Win-PC", state.Revision, timeout.Token);
        Assert.True(switched.InputTransferred);
        Assert.Equal("Mac-Two", switched.ActiveComputer);
        var stale = await host.SwitchIfCurrentAsync("Win-Two", "Win-PC", state.Revision, timeout.Token);
        Assert.False(stale.Ok);
        Assert.Equal("focus_changed", stale.Error);
        await Assert.ThrowsAsync<ArgumentException>(() => host.SwitchIfCurrentAsync("Unknown", "Mac-Two", 1, timeout.Token));
        process.Exit();
        cancellation.Cancel();
        await fakeCore;
        await ipcServer.DisposeAsync();
        await nativeServer.DisposeAsync();
    }

    [Fact]
    public async Task LegacyInvalidActionRemainsExplicitRejectionWithoutFallback()
    {
        using var folder=new TestFolder();
        var engine=Path.Combine(folder.Path,"synthetic-engine.exe");
        await File.WriteAllTextAsync(engine,"not an executable");
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(engine)));
        var (ipcClient,ipcServer)=ControlProtocolTests.Duplex();
        var (nativeClient,nativeServer)=ControlProtocolTests.Duplex();
        var process=new FakeProcess();
        var deps=new DeskflowSessionDependencies {
            HasConflictingCore=_=>false,
            StartProcessAsync=(_,_)=>Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync=(_,_)=>Task.FromResult(ipcClient),
            ConnectNativeAsync=(_,_)=>Task.FromResult(new NativeControlClient(nativeClient))
        };
        await using var host=new MultiPeerKeyboardSession(engine,hash,Path.Combine(folder.Path,"profile"),"Win-PC",
            [new("Mac-Two",new string('2',64))],deps);
        var session=(DeskflowSession)typeof(MultiPeerKeyboardSession).GetField("session",System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(host)!;
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged+=(_,state)=>{if(state.NativeControlAvailable)ready.TrySetResult();};
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await host.StartAsync();
            using (var request=System.Text.Json.JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer,deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer,new {version=1,requestId=request.RootElement.GetProperty("requestId").GetString(),
                    ok=true,activeComputer="Win-PC",connectedComputers=new[] {"Mac-Two"},revision=4},deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            var pending=host.SwitchIfCurrentAsync("Mac-Two","Win-PC",4,deadline.Token);
            using (var request=System.Text.Json.JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer,deadline.Token))!))
            {
                Assert.Equal("switch-if-current",request.RootElement.GetProperty("action").GetString());
                await JsonLineProtocol.WriteAsync(nativeServer,new {version=1,requestId=request.RootElement.GetProperty("requestId").GetString(),
                    ok=false,activeComputer="",connectedComputers=Array.Empty<string>(),revision=0,error="invalid_action"},deadline.Token);
            }
            var result=await pending.WaitAsync(deadline.Token);
            Assert.False(result.Ok); Assert.False(result.InputTransferred); Assert.Equal("invalid_action",result.Error);
            using var noFallback=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>JsonLineProtocol.ReadAsync(nativeServer,noFallback.Token));
        }
        finally {process.Exit();await ipcServer.DisposeAsync();await nativeServer.DisposeAsync();}
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectionCannotExposeNamesOutsideApprovedSession(bool badConnected)
    {
        using var folder=new TestFolder();
        var engine=Path.Combine(folder.Path,"synthetic-engine.exe");
        await File.WriteAllTextAsync(engine,"not an executable");
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(engine)));
        var (ipcClient,ipcServer)=ControlProtocolTests.Duplex();
        var (nativeClient,nativeServer)=ControlProtocolTests.Duplex();
        var process=new FakeProcess();
        var deps=new DeskflowSessionDependencies {
            HasConflictingCore=_=>false,
            StartProcessAsync=(_,_)=>Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync=(_,_)=>Task.FromResult(ipcClient),
            ConnectNativeAsync=(_,_)=>Task.FromResult(new NativeControlClient(nativeClient))
        };
        await using var host=new MultiPeerKeyboardSession(engine,hash,Path.Combine(folder.Path,"profile"),"Win-PC",
            [new("Mac-Two",new string('2',64))],deps);
        var session=(DeskflowSession)typeof(MultiPeerKeyboardSession).GetField("session",System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(host)!;
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged+=(_,state)=>{if(state.NativeControlAvailable)ready.TrySetResult();};
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await host.StartAsync();
            using (var request=System.Text.Json.JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer,deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer,new {version=1,requestId=request.RootElement.GetProperty("requestId").GetString(),
                    ok=true,activeComputer="Win-PC",connectedComputers=new[] {"Mac-Two"},revision=4},deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            var pending=host.SwitchIfCurrentAsync("Mac-Two","Win-PC",4,deadline.Token);
            using (var request=System.Text.Json.JsonDocument.Parse((await JsonLineProtocol.ReadAsync(nativeServer,deadline.Token))!))
                await JsonLineProtocol.WriteAsync(nativeServer,new {version=1,requestId=request.RootElement.GetProperty("requestId").GetString(),
                    ok=false,activeComputer=badConnected ? "Win-PC" : "Unexpected",connectedComputers=badConnected ? new[] {"Unexpected"} : new[] {"Mac-Two"},
                    revision=4,error="focus_changed"},deadline.Token);
            await Assert.ThrowsAsync<IOException>(()=>pending);
            using var noFallback=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>JsonLineProtocol.ReadAsync(nativeServer,noFallback.Token));
        }
        finally {process.Exit();await ipcServer.DisposeAsync();await nativeServer.DisposeAsync();}
    }

    private static async Task RunFakeCoreAsync(Stream stream, CancellationToken ct)
    {
        var active = "Win-PC";
        long revision = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await JsonLineProtocol.ReadAsync(stream, ct);
                if (frame is null) return;
                using var doc = System.Text.Json.JsonDocument.Parse(frame);
                var root = doc.RootElement;
                var id = root.GetProperty("requestId").GetString();
                var action = root.GetProperty("action").GetString();
                var ok = true;
                string? error = null;
                if (action == "switch-if-current")
                {
                    if (root.GetProperty("expectedActive").GetString() != active ||
                        root.GetProperty("expectedRevision").GetInt64() != revision)
                    { ok = false; error = "focus_changed"; }
                    else
                    { active = root.GetProperty("target").GetString()!; revision++; }
                }
                await JsonLineProtocol.WriteAsync(stream, new { version = 1, requestId = id, ok,
                    activeComputer = active, connectedComputers = new[] { "Win-Two", "Mac-Two" }, revision, error }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
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
