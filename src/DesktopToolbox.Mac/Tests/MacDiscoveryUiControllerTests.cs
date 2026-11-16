using System.Net;
using DesktopToolbox.DeskLink;
using NianLink.MacDiscoveryCandidate;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacDiscoveryUiControllerTests
{
    private static readonly IPAddress Address = IPAddress.Parse("192.168.55.9");
    private static PeerIdentityOffer Offer() => new() { Name = "Test-Mac", Platform = "mac", KeyboardFingerprint = new string('A', 64), Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2) };

    [Fact]
    public async Task LiveAdapterRefusesUnexpectedDurationBeforeNetworkAccess()
    {
        var backend = new LiveMacDiscoveryBackend();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => backend.StartAsync(Address, Offer(), TimeSpan.FromSeconds(31), CancellationToken.None));
        if (!OperatingSystem.IsMacOS())
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => backend.GetNetworksAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NoNetworkOrConsentNeverStartsDiscovery()
    {
        var backend = new FakeBackend();
        await using var ui = new DiscoveryUiController(backend);
        await ui.RefreshAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ui.StartAsync(null, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true)));
        var result = await ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(false));
        Assert.False(result);
        Assert.Equal(0, backend.StartCount);
        Assert.False(ui.IsDiscoverable);
    }

    [Fact]
    public async Task ApprovalUsesSelectedNetworkAndExactOfferThenStopReleasesSession()
    {
        var backend = new FakeBackend();
        await using var ui = new DiscoveryUiController(backend);
        await ui.RefreshAsync();
        var offer = Offer();
        Assert.True(await ui.StartAsync(Address, () => Task.FromResult(offer), (address, shown) =>
        {
            Assert.Equal(Address, address); Assert.Same(offer, shown); Assert.Equal(0, backend.StartCount);
            return Task.FromResult(true);
        }));
        Assert.Equal(TimeSpan.FromSeconds(30), backend.Duration);
        Assert.True(ui.IsDiscoverable);
        await ui.StopAsync();
        Assert.False(ui.IsDiscoverable);
        Assert.True(backend.Session!.Disposed);
    }

    [Fact]
    public async Task StopDuringPendingConsentPreventsLateStart()
    {
        var backend = new FakeBackend();
        await using var ui = new DiscoveryUiController(backend);
        await ui.RefreshAsync();
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => answer.Task);
        var stopping = ui.StopAsync();
        answer.SetResult(true);
        await stopping;
        Assert.False(await started);
        Assert.Equal(0, backend.StartCount);
        Assert.False(ui.IsDiscoverable);
    }

    [Fact]
    public async Task LateBackendStartIsDisposedAfterStopAndCannotReactivate()
    {
        var backend = new FakeBackend { DelayStart = true };
        await using var ui = new DiscoveryUiController(backend);
        await ui.RefreshAsync();
        var started = ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true));
        await backend.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopping = ui.StopAsync();
        Assert.True(backend.StartToken.IsCancellationRequested);
        backend.AllowStart.SetResult();
        await stopping;
        Assert.False(await started);
        Assert.True(backend.Session!.Disposed);
        Assert.False(ui.IsDiscoverable);
    }

    [Fact]
    public async Task ASecondStartCannotRunWhileConsentIsPending()
    {
        var backend = new FakeBackend();
        await using var ui = new DiscoveryUiController(backend);
        await ui.RefreshAsync();
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => answer.Task);
        Assert.False(ui.CanStart);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true)));
        answer.SetResult(false);
        Assert.False(await first);
        Assert.True(ui.CanStart);
        Assert.Equal(0, backend.StartCount);
    }

    [Fact]
    public async Task CompletionRefreshAndCloseRevokeTheExistingSession()
    {
        var backend = new FakeBackend();
        var ui = new DiscoveryUiController(backend);
        await ui.RefreshAsync();
        Assert.True(await ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true)));
        backend.Session!.Finished.SetResult();
        await ui.ObserveCompletionAsync();
        Assert.False(ui.IsDiscoverable);
        Assert.True(backend.Session.Disposed);
        Assert.True(await ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true)));
        await ui.RefreshAsync();
        Assert.True(backend.Session!.Disposed);
        await ui.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ui.RefreshAsync());
    }

    [Fact]
    public async Task AlreadyFinishedRunIsNeverShownAsDiscoverable()
    {
        var backend = new FakeBackend { FinishImmediately = true };
        await using var ui = new DiscoveryUiController(backend);
        await ui.RefreshAsync();
        Assert.False(await ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true)));
        Assert.False(ui.IsDiscoverable);
        Assert.True(backend.Session!.Disposed);
        Assert.DoesNotContain("本机可被发现", ui.Status);
    }

    [Fact]
    public async Task FailedCleanupPreventsAnotherRun()
    {
        var backend = new FakeBackend { FailDispose = true };
        var ui = new DiscoveryUiController(backend);
        await ui.RefreshAsync();
        Assert.True(await ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ui.StopAsync());
        Assert.False(ui.CanStart);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ui.DisposeAsync());
    }

    [Fact]
    public async Task LateCompletionObservationCannotStopANewerRun()
    {
        var backend = new FakeBackend();
        await using var ui = new DiscoveryUiController(backend);
        await ui.RefreshAsync();
        Assert.True(await ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true)));
        var old = backend.Session!;
        old.Finished.SetResult();
        old.BlockCompletionRead = true;
        var observation = Task.Run(ui.ObserveCompletionAsync);
        try
        {
            await old.CompletionReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await ui.StopAsync();
            Assert.True(await ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true)));
            var newer = backend.Session!;
            old.AllowCompletionRead.Set();
            await observation.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(ui.IsDiscoverable);
            Assert.False(newer.Disposed);
        }
        finally { old.AllowCompletionRead.Set(); }
    }

    [Fact]
    public async Task IncompleteStartupCleanupBlocksFurtherDiscovery()
    {
        var backend = new FakeBackend { FailStartupCleanup = true };
        var ui = new DiscoveryUiController(backend);
        await ui.RefreshAsync();
        await Assert.ThrowsAsync<MacDiscoveryStartupCleanupException>(() => ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true)));
        Assert.False(ui.CanStart);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ui.DisposeAsync());
    }

    [Fact]
    public async Task ErrorCompletionStopsAndRetainsUsefulStatus()
    {
        var backend = new FakeBackend();
        await using var ui = new DiscoveryUiController(backend);
        await ui.RefreshAsync();
        Assert.True(await ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true)));
        backend.Session!.Failure = new IOException("synthetic failure");
        backend.Session.Finished.SetResult();
        await ui.ObserveCompletionAsync();
        Assert.False(ui.IsDiscoverable);
        Assert.True(backend.Session.Disposed);
        Assert.Contains("网络变化或错误", ui.Status);
    }

    [Fact]
    public async Task FailedRefreshInvalidatesPreviousNetworkChoices()
    {
        var backend = new FakeBackend();
        await using var ui = new DiscoveryUiController(backend);
        await ui.RefreshAsync();
        Assert.Single(ui.Networks);
        backend.FailRefresh = true;
        await Assert.ThrowsAsync<IOException>(ui.RefreshAsync);
        Assert.Empty(ui.Networks);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ui.StartAsync(Address, () => Task.FromResult(Offer()), (_, _) => Task.FromResult(true)));
    }

    private sealed class FakeBackend : IMacDiscoveryUiBackend
    {
        public int StartCount;
        public TimeSpan Duration;
        public bool DelayStart, FinishImmediately, FailDispose, FailStartupCleanup, FailRefresh;
        public CancellationToken StartToken;
        public FakeSession? Session;
        public TaskCompletionSource StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<IPAddress>> GetNetworksAsync(CancellationToken ct) => FailRefresh
            ? Task.FromException<IReadOnlyList<IPAddress>>(new IOException("synthetic refresh failure"))
            : Task.FromResult<IReadOnlyList<IPAddress>>([Address]);
        public async Task<IMacDiscoveryUiSession> StartAsync(IPAddress address, PeerIdentityOffer offer, TimeSpan duration, CancellationToken ct)
        {
            StartCount++; Duration = duration; StartToken = ct; StartEntered.TrySetResult();
            if (DelayStart) await AllowStart.Task;
            if (FailStartupCleanup) throw new MacDiscoveryStartupCleanupException(new IOException("cleanup failed"));
            Session = new FakeSession { FailDispose = FailDispose };
            if (FinishImmediately) Session.Finished.SetResult();
            return Session;
        }
    }
    private sealed class FakeSession : IMacDiscoveryUiSession
    {
        public TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed, FailDispose, BlockCompletionRead;
        public Exception? Failure;
        public TaskCompletionSource CompletionReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim AllowCompletionRead = new(false);
        public Task Completion
        {
            get
            {
                if (BlockCompletionRead)
                {
                    CompletionReadEntered.TrySetResult();
                    if (!AllowCompletionRead.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException();
                }
                return Finished.Task;
            }
        }
        public Exception? Error => Failure;
        public ValueTask DisposeAsync() { Disposed = true; return FailDispose ? ValueTask.FromException(new IOException("cleanup failed")) : ValueTask.CompletedTask; }
    }
}
