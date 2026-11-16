using System.Text;
using System.Reflection;
using System.Collections.Concurrent;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class IpcConnectionEvidenceTests
{
    [Theory]
    [InlineData("eof")]
    [InlineData("bye")]
    [InlineData("invalid-fingerprint")]
    public async Task TerminatedStatusChannelInvalidatesConnectionAndRejectsNewControl(string reason)
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var invitation = new MultiPeerInvitation
        {
            Host = "127.0.0.1", Port = 32001, HostName = "Host", PeerName = "Peer",
            CertificateSha256 = new string('A', 64), Token = Convert.ToBase64String(new byte[32])
        };
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client)
        };
        await using var peer = new WindowsPeerLink(Path.Combine(folder.Path, "peer"),
            Path.Combine(folder.Path, "deskflow-core.exe"), invitation, true, dependencies);
        await peer.StartAsync();
        await JsonLineProtocol.ReadAsync(server);
        var fingerprint = new string('D', 64);
        await server.WriteAsync(Encoding.UTF8.GetBytes("peerFingerprint=" + fingerprint + "\nconnectionState=Connected\n"));
        await server.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await peer.GetStateAsync()).PendingPeerFingerprint is null) await Task.Delay(5, deadline.Token);
        await peer.ConfirmKeyboardPeerAsync(fingerprint);
        Assert.Equal(DeskLinkConnectionState.Connected, (await peer.GetStateAsync()).ConnectionState);

        if (reason == "eof") await server.DisposeAsync();
        else
        {
            await server.WriteAsync(Encoding.UTF8.GetBytes(reason == "bye" ? "bye\n" : "peerFingerprint=invalid\n"));
            await server.FlushAsync();
        }

        // The child is still running: only current IPC evidence may keep it Connected.
        while ((await peer.GetStateAsync()).ConnectionState == DeskLinkConnectionState.Connected)
            await Task.Delay(5, deadline.Token);
        var after = await peer.GetStateAsync();
        Assert.Equal(DeskLinkProcessState.Running, after.ProcessState);
        Assert.Equal(DeskLinkConnectionState.Disconnected, after.ConnectionState);
        Assert.NotNull(after.LastError);
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.SwitchAsync("Peer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.BeginPreviewAsync());
        process.Exit();
        if (reason != "eof") await server.DisposeAsync();
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("replacement")]
    [InlineData("aba")]
    [InlineData("cancel")]
    public async Task ConfirmationInFlightCannotCommitAfterEvidenceChanges(string mode)
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client),
            WritePeerTrustAsync = async (path, contents, ct) =>
            {
                started.TrySetResult(); await resume.Task;
                await PrivateFiles.WriteAsync(path, contents, CancellationToken.None);
            }
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        var profile = await session.PrepareAsync();
        await session.StartAsync();
        await JsonLineProtocol.ReadAsync(server);
        var first = new string('D', 64); var replacement = new string('E', 64);
        await server.WriteAsync(Encoding.UTF8.GetBytes("peerFingerprint=" + first + "\n"));
        await server.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await session.GetStateAsync()).PendingPeerFingerprint is null) await Task.Delay(5, deadline.Token);
        using var cancel = new CancellationTokenSource();
        var confirming = session.ConfirmPeerAsync(first, cancel.Token);
        await started.Task.WaitAsync(deadline.Token);
        if (mode == "disconnect")
        {
            await server.DisposeAsync();
            while ((await session.GetStateAsync()).LastError is null) await Task.Delay(5, deadline.Token);
        }
        else if (mode is "replacement" or "aba")
        {
            await server.WriteAsync(Encoding.UTF8.GetBytes("peerFingerprint=" + replacement + "\n"));
            await server.FlushAsync();
            while ((await session.GetStateAsync()).PendingPeerFingerprint != replacement) await Task.Delay(5, deadline.Token);
            if (mode == "aba")
            {
                await server.WriteAsync(Encoding.UTF8.GetBytes("peerFingerprint=" + first + "\n")); await server.FlushAsync();
                while ((await session.GetStateAsync()).PendingPeerFingerprint != first) await Task.Delay(5, deadline.Token);
            }
        }
        else cancel.Cancel();
        resume.TrySetResult();
        var failure = await Record.ExceptionAsync(() => confirming);
        var current = await session.GetStateAsync();
        var trust = Path.Combine(profile.TrustDirectory, "trusted-clients");
        var contents = File.Exists(trust) ? await File.ReadAllTextAsync(trust) : "";
        process.Exit(); if (mode != "disconnect") await server.DisposeAsync();
        if (mode == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.IsType<InvalidOperationException>(failure);
        Assert.DoesNotContain(first, contents, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(mode == "replacement" ? replacement : mode is "aba" or "cancel" ? first : null, current.PendingPeerFingerprint);
        Assert.Empty(Directory.EnumerateFiles(profile.TrustDirectory, "*.pending"));
    }

    [Fact]
    public async Task ThrowingProcessExitObserverLeavesObservableFailure()
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex(); var process = new FakeProcess();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client)
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        EventHandler<DeskLinkState> observer = (_, state) =>
        { if (armed && state.ProcessState == DeskLinkProcessState.Stopped) { notified.TrySetResult(); throw new InvalidOperationException("synthetic exit observer"); } };
        session.StateChanged += observer;
        try
        {
            await session.StartAsync(); await JsonLineProtocol.ReadAsync(server);
            armed = true;
            process.Exit();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await notified.Task.WaitAsync(deadline.Token);
            while ((await session.GetStateAsync()).ProcessState != DeskLinkProcessState.Faulted) await Task.Delay(5, deadline.Token);
            var state = await session.GetStateAsync();
            Assert.NotNull(state.LastError);
            Assert.Equal(DeskLinkConnectionState.Disconnected, state.ConnectionState);
        }
        finally { session.StateChanged -= observer; await server.DisposeAsync(); }
    }

    [Fact]
    public async Task ThrowingStoppingObserverCannotPreventOwnedProcessCleanup()
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client)
        };
        var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        session.StateChanged += (_, state) => { if (state.ProcessState == DeskLinkProcessState.Stopping) throw new InvalidOperationException("synthetic stopping observer"); };
        try
        {
            await session.StartAsync(); await JsonLineProtocol.ReadAsync(server);
            var stopping = session.StopAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var first = await Task.WhenAny(stopping, JsonLineProtocol.ReadAsync(server).WaitAsync(deadline.Token));
            if (first != stopping) process.Exit();
            var failure = await Record.ExceptionAsync(() => stopping);
            Assert.NotNull(failure);
            Assert.True(process.HasExited);
            Assert.True(process.Disposed);
            using var lease = new FileStream(Path.Combine(folder.Path, ".session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { process.Exit(); try { await session.DisposeAsync(); } catch { } await server.DisposeAsync(); }
    }

    [Theory]
    [InlineData(DeskLinkProcessState.Starting)]
    [InlineData(DeskLinkProcessState.Running)]
    public async Task ThrowingStartObserverCannotLeaveAChildOrProfileLease(DeskLinkProcessState failing)
    {
        using var folder = new TestFolder();
        var process = new FakeProcess(); var started = false;
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => { started = true; return Task.FromResult<IDeskflowOwnedProcess>(process); }
        };
        var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        session.StateChanged += (_, state) => { if (state.ProcessState == failing) throw new InvalidOperationException("synthetic start observer"); };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
            if (started) { Assert.True(process.HasExited); Assert.True(process.Disposed); }
            using var lease = new FileStream(Path.Combine(folder.Path, ".session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { process.Exit(); try { await session.DisposeAsync(); } catch { } }
    }

    [Fact]
    public async Task LateCallbackCannotReviveTerminatedChannelInSameSession()
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client)
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        await session.StartAsync(); await JsonLineProtocol.ReadAsync(server);
        await server.WriteAsync(Encoding.UTF8.GetBytes("connectionState=Connected\n")); await server.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await session.GetStateAsync()).ConnectionState != DeskLinkConnectionState.Connected) await Task.Delay(5, deadline.Token);
        var ipc = (DeskflowCoreIpcClient)typeof(DeskflowSession).GetField("coreIpc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var late = (EventHandler<KeyValuePair<string, string>>)typeof(DeskflowCoreIpcClient).GetField("Message", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ipc)!;
        await server.DisposeAsync();
        while ((await session.GetStateAsync()).ConnectionState != DeskLinkConnectionState.Disconnected) await Task.Delay(5, deadline.Token);
        late(ipc, new("connectionState", "Connected"));
        late(ipc, new("peerFingerprint", new string('E', 64)));
        var current = await session.GetStateAsync();
        process.Exit();
        Assert.Equal(DeskLinkConnectionState.Disconnected, current.ConnectionState);
        Assert.Null(current.PendingPeerFingerprint);
    }

    [Fact]
    public async Task TrustCommitFailureRetainsPendingAndDeletesStagedFile()
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client),
            WritePeerTrustAsync = async (path, contents, ct) =>
            {
                await PrivateFiles.WriteAsync(path, contents, ct);
                staged.TrySetResult(); await resume.Task.WaitAsync(ct);
            }
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        var profile = await session.PrepareAsync(); await session.StartAsync(); await JsonLineProtocol.ReadAsync(server);
        var fingerprint = new string('D', 64);
        await server.WriteAsync(Encoding.UTF8.GetBytes("peerFingerprint=" + fingerprint + "\n")); await server.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await session.GetStateAsync()).PendingPeerFingerprint is null) await Task.Delay(5, deadline.Token);
        var confirming = session.ConfirmPeerAsync(fingerprint);
        await staged.Task.WaitAsync(deadline.Token);
        var destination = Path.Combine(profile.TrustDirectory, "trusted-clients");
        Directory.CreateDirectory(destination); // Force the final replacement to fail without changing real files.
        resume.TrySetResult();
        var failure = await Record.ExceptionAsync(() => confirming);
        var pending = (await session.GetStateAsync()).PendingPeerFingerprint;
        var left = Directory.EnumerateFiles(profile.TrustDirectory, "*.pending").ToArray();
        process.Exit(); await server.DisposeAsync();
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(fingerprint, pending);
        Assert.Empty(left);
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task SlowObserverCanIdentifyOlderNotificationAfterProcessExit()
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client)
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim(false);
        var connectedDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stoppedDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new ConcurrentQueue<DeskLinkState>();
        session.StateChanged += (_, state) =>
        {
            if (state.ConnectionState == DeskLinkConnectionState.Connected)
            { blocked.TrySetResult(); if (!resume.Wait(TimeSpan.FromSeconds(4))) throw new TimeoutException("observer not released"); }
        };
        session.StateChanged += (_, state) =>
        {
            notifications.Enqueue(state);
            if (state.ConnectionState == DeskLinkConnectionState.Connected) connectedDelivered.TrySetResult();
            if (state.ProcessState == DeskLinkProcessState.Stopped) stoppedDelivered.TrySetResult();
        };
        await session.StartAsync(); await JsonLineProtocol.ReadAsync(server);
        await server.WriteAsync(Encoding.UTF8.GetBytes("connectionState=Connected\n")); await server.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            await blocked.Task.WaitAsync(deadline.Token);
            var connectedSnapshot = session.CurrentState;
            Assert.Equal(DeskLinkConnectionState.Connected, connectedSnapshot.ConnectionState);
            process.Exit();
            while (session.CurrentState.ProcessState != DeskLinkProcessState.Stopped) await Task.Delay(5, deadline.Token);
            Assert.Equal(DeskLinkConnectionState.Disconnected, session.CurrentState.ConnectionState);
            Assert.Equal(DeskLinkConnectionState.Connected, connectedSnapshot.ConnectionState);
            Assert.True(session.CurrentState.SessionStateVersion > connectedSnapshot.SessionStateVersion);
            await stoppedDelivered.Task.WaitAsync(deadline.Token);
        }
        finally { resume.Set(); }
        await connectedDelivered.Task.WaitAsync(deadline.Token);
        await stoppedDelivered.Task.WaitAsync(deadline.Token);
        var events = notifications.ToArray();
        var final = await session.GetStateAsync();
        Assert.Equal(DeskLinkProcessState.Stopped, final.ProcessState);
        Assert.Equal(DeskLinkConnectionState.Disconnected, final.ConnectionState);
        // A slow consumer can receive events out of order; the final value has
        // a greater local sequence so consumers can reject the stale event.
        Assert.True(final.SessionStateVersion > events.Last().SessionStateVersion);
        Assert.DoesNotContain("SessionStateVersion", System.Text.Json.JsonSerializer.Serialize(final), StringComparison.OrdinalIgnoreCase);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TrustedFingerprintEventDoesNotEraseDifferentPendingFingerprint()
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client)
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        var profile = await session.PrepareAsync();
        var trusted = new string('A', 64); var pending = new string('B', 64);
        await File.WriteAllTextAsync(Path.Combine(profile.TrustDirectory, "trusted-clients"), "v2:sha256:" + trusted.ToLowerInvariant() + "\n");
        await session.StartAsync(); await JsonLineProtocol.ReadAsync(server);
        await server.WriteAsync(Encoding.UTF8.GetBytes("peerFingerprint=" + pending + "\n")); await server.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await session.GetStateAsync()).PendingPeerFingerprint != pending) await Task.Delay(5, deadline.Token);
        await server.WriteAsync(Encoding.UTF8.GetBytes("peerFingerprint=" + trusted + "\nconnectionState=Connected\n")); await server.FlushAsync();
        while ((await session.GetStateAsync()).ConnectionState != DeskLinkConnectionState.Connected) await Task.Delay(5, deadline.Token);
        var state = await session.GetStateAsync();
        process.Exit(); await server.DisposeAsync();
        Assert.Equal(pending, state.PendingPeerFingerprint);
    }

    [Fact]
    public async Task ConsumerThrowingDuringDisconnectDoesNotLeakOwnedProcessOrLease()
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client)
        };
        var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        var failedNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, state) =>
        {
            if (state.ConnectionState == DeskLinkConnectionState.Connected) throw new InvalidOperationException("synthetic message consumer");
            if (state.LastError is not null && failedNotification.TrySetResult()) throw new InvalidOperationException("synthetic disconnect consumer");
        };
        try
        {
            await session.StartAsync();
            await JsonLineProtocol.ReadAsync(server);
            await server.WriteAsync(Encoding.UTF8.GetBytes("connectionState=Connected\n"));
            await server.FlushAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await failedNotification.Task.WaitAsync(deadline.Token);
            var stopping = session.StopAsync();
            Assert.Equal("stop", await JsonLineProtocol.ReadAsync(server).WaitAsync(deadline.Token));
            process.Exit();
            var failure = await Record.ExceptionAsync(() => stopping);
            Assert.NotNull(failure); // The consumer error must not be silently lost.
            Assert.True(process.Disposed); // Even a faulted IPC reader cannot skip owned-child cleanup.
            using var reopenedLease = new FileStream(Path.Combine(folder.Path, ".session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            process.Exit();
            try { await session.DisposeAsync(); } catch { }
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task MessageConsumerExceptionEndsChannelAndClearsConnectionEvidence()
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client)
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        session.StateChanged += (_, state) => { if (state.ConnectionState == DeskLinkConnectionState.Connected) throw new InvalidOperationException("synthetic consumer failure"); };
        await session.StartAsync();
        await JsonLineProtocol.ReadAsync(server);
        await server.WriteAsync(Encoding.UTF8.GetBytes("connectionState=Connected\n"));
        await server.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await session.GetStateAsync()).LastError is null) await Task.Delay(5, deadline.Token);
        Assert.Equal(DeskLinkConnectionState.Disconnected, (await session.GetStateAsync()).ConnectionState);
        process.Exit(); await server.DisposeAsync();
    }

    [Fact]
    public async Task LateCallbacksFromStoppedChannelCannotChangeNewSession()
    {
        using var folder = new TestFolder();
        var (firstClient, firstServer) = ControlProtocolTests.Duplex();
        var (secondClient, secondServer) = ControlProtocolTests.Duplex();
        var first = new FakeProcess(); var second = new FakeProcess();
        var processes = new Queue<FakeProcess>([first, second]);
        var streams = new Queue<Stream>([firstClient, secondClient]);
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(processes.Dequeue()),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(streams.Dequeue())
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        await session.StartAsync();
        await JsonLineProtocol.ReadAsync(firstServer);
        await firstServer.WriteAsync(Encoding.UTF8.GetBytes("connectionState=Connected\n"));
        await firstServer.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await session.GetStateAsync()).ConnectionState != DeskLinkConnectionState.Connected) await Task.Delay(5, deadline.Token);
        var ipc = (DeskflowCoreIpcClient)typeof(DeskflowSession).GetField("coreIpc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var staleMessage = (EventHandler<KeyValuePair<string, string>>)typeof(DeskflowCoreIpcClient).GetField("Message", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ipc)!;
        var staleDisconnected = (EventHandler<Exception>)typeof(DeskflowCoreIpcClient).GetField("Disconnected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ipc)!;
        var stopped = session.StopAsync();
        Assert.Equal("stop", await JsonLineProtocol.ReadAsync(firstServer).WaitAsync(deadline.Token));
        first.Exit(); await stopped.WaitAsync(deadline.Token);
        await session.StartAsync();
        await JsonLineProtocol.ReadAsync(secondServer);
        await secondServer.WriteAsync(Encoding.UTF8.GetBytes("connectionState=Connected\n"));
        await secondServer.FlushAsync();
        while ((await session.GetStateAsync()).ConnectionState != DeskLinkConnectionState.Connected) await Task.Delay(5, deadline.Token);
        staleMessage(ipc, new("connectionState", "Disconnected"));
        staleMessage(ipc, new("peerFingerprint", new string('E', 64)));
        staleDisconnected(ipc, new IOException("stale channel"));
        var current = await session.GetStateAsync();
        Assert.Equal(DeskLinkConnectionState.Connected, current.ConnectionState);
        Assert.Null(current.PendingPeerFingerprint);
        Assert.Null(current.LastError);
        second.Exit(); await firstServer.DisposeAsync(); await secondServer.DisposeAsync();
    }

    [Fact]
    public async Task LostStatusChannelInvalidatesPendingFingerprint()
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client)
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        await session.StartAsync();
        await JsonLineProtocol.ReadAsync(server);
        var fingerprint = new string('D', 64);
        await server.WriteAsync(Encoding.UTF8.GetBytes("peerFingerprint=" + fingerprint + "\n"));
        await server.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await session.GetStateAsync()).PendingPeerFingerprint is null) await Task.Delay(5, deadline.Token);
        await server.DisposeAsync();
        while ((await session.GetStateAsync()).LastError is null) await Task.Delay(5, deadline.Token);
        Assert.Null((await session.GetStateAsync()).PendingPeerFingerprint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConfirmPeerAsync(fingerprint));
        process.Exit();
    }

    [Fact]
    public async Task ExplicitStopDoesNotReportStatusChannelFailure()
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new FakeProcess();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult(client)
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with { UseNativeControl = false }, dependencies);
        await session.StartAsync();
        await JsonLineProtocol.ReadAsync(server);
        await server.WriteAsync(Encoding.UTF8.GetBytes("connectionState=Connected\n"));
        await server.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await session.GetStateAsync()).ConnectionState != DeskLinkConnectionState.Connected) await Task.Delay(5, deadline.Token);
        var stopped = session.StopAsync();
        Assert.Equal("stop", await JsonLineProtocol.ReadAsync(server).WaitAsync(deadline.Token));
        process.Exit();
        var result = await stopped.WaitAsync(deadline.Token);
        Assert.Equal(DeskLinkProcessState.Stopped, result.ProcessState);
        Assert.Equal(DeskLinkConnectionState.Disconnected, result.ConnectionState);
        Assert.Null(result.LastError);
        await server.DisposeAsync();
    }

    private sealed class FakeProcess : IDeskflowOwnedProcess
    {
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => 5678;
        public bool HasExited => completion.Task.IsCompleted;
        public Task<int> Completion => completion.Task;
        public bool Disposed { get; private set; }
        public void Exit() => completion.TrySetResult(0);
        public Task StopAsync(CancellationToken ct = default) { Exit(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
