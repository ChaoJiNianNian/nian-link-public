using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;

namespace DeskLink.Tests;

public sealed class DeskflowPreviewUncertainReturnTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnansweredBeginOrEndRemainsUnconfirmedAfterReadAndRetry(bool failBegin)
    {
        var backend = new Backend { FailBegin = failBegin, FailEnd = !failBegin };
        await using var session = CreateSession(backend);
        session.ObserveFrame(Frame());
        if (failBegin) await Assert.ThrowsAsync<IOException>(() => session.EnableAsync());
        else { await session.EnableAsync(); await session.StopAsync("停止"); }
        Assert.Contains("尚未确认", session.Status);
        Assert.False(session.CanStart);
        Assert.Equal(DeskflowPreviewPhase.Viewing, (await backend.ReadPreviewAsync()).Phase);
        await session.CheckSafetyAsync();
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<string>();
        session.Changed += () => notifications.Enqueue(session.Status);
        await session.StopAsync("预览已关闭，键鼠已归还");
        Assert.False(session.CanStart);
        Assert.Contains("尚未确认", session.Status);
        Assert.NotEmpty(notifications);
        Assert.All(notifications, status => Assert.Contains("尚未确认", status));
        Assert.DoesNotContain("重试归还", session.Status);
        Assert.Contains("检查键鼠", session.Status);
        Assert.Equal(failBegin ? 0 : 1, backend.EndCalls);
        notifications.Clear();
        await session.DisposeAsync();
        Assert.NotEmpty(notifications);
        Assert.All(notifications, status => Assert.Contains("尚未确认", status));
    }

    [Fact]
    public async Task CloseCannotPublishConfirmedReturnWhileBeginIsStillInFlight()
    {
        var pending = new TaskCompletionSource<DeskflowPreviewState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend { BeginPending = pending };
        await using var session = CreateSession(backend);
        session.ObserveFrame(Frame());
        var enabling = session.EnableAsync();
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<string>();
        session.Changed += () => notifications.Enqueue(session.Status);
        var stop = session.StopAsync("预览已关闭，键鼠已归还");
        Assert.NotEmpty(notifications);
        Assert.DoesNotContain(notifications, status => status.Contains("键鼠已归还", StringComparison.Ordinal));
        pending.SetResult(new DeskflowPreviewState());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enabling);
        await stop;
        Assert.True(session.CanStart);
    }

    [Fact]
    public async Task QueuedStopAfterConfirmedReturnDoesNotSendOldEnd()
    {
        var backend = new Backend();
        var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new DeskflowPreviewSession(backend, async (enabled, _) =>
        {
            if (!enabled) { restoring.TrySetResult(); await releaseRestore.Task; }
        }, new NoTimerTimeProvider());
        session.ObserveFrame(Frame());
        await session.EnableAsync();
        var check = session.CheckSafetyAsync();
        await restoring.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = session.StopAsync("关闭");
        releaseRestore.TrySetResult();
        await Task.WhenAll(check, stop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.CanStart);
        Assert.Equal(0, backend.EndCalls);
    }

    [Fact]
    public async Task BeginIgnoringCancellationIsBoundedAndLateReplyDoesNotClearUnknown()
    {
        var pending = new TaskCompletionSource<DeskflowPreviewState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend { BeginPending = pending };
        await using var session = CreateSession(backend);
        session.ObserveFrame(Frame());
        var enabling = session.EnableAsync();
        var stopping = session.StopAsync("停止");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enabling);
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(session.CanStart);
            Assert.Contains("尚未确认", session.Status);
            pending.TrySetResult(new DeskflowPreviewState { Id = "late", Phase = DeskflowPreviewPhase.Active });
            await session.CheckSafetyAsync();
            await session.StopAsync("重试");
            Assert.False(session.CanStart);
            Assert.Equal(0, backend.EndCalls);
        }
        finally { pending.TrySetResult(new DeskflowPreviewState()); }
    }

    [Fact]
    public async Task StopBeforeBeginReservationDoesNotSendBegin()
    {
        var backend = new Backend();
        var clock = new NoTimerTimeProvider();
        await using var session = new DeskflowPreviewSession(backend, (_, _) => Task.CompletedTask, clock);
        session.ObserveFrame(Frame());
        Task? stopping = null;
        var checks = 0;
        clock.OnRead = () =>
        {
            if (Interlocked.Increment(ref checks) == 2)
                stopping = session.StopAsync("停止");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.EnableAsync());
        Assert.NotNull(stopping);
        await stopping!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, backend.BeginCalls);
        Assert.Equal(0, backend.EndCalls);
    }

    [Theory]
    [InlineData(DeskflowPreviewPhase.Recovery)]
    [InlineData(DeskflowPreviewPhase.Preparing)]
    [InlineData(DeskflowPreviewPhase.Returning)]
    public async Task IncompleteBeginResultWithIdRemainsUnconfirmed(DeskflowPreviewPhase phase)
    {
        var backend = new Backend { BeginResult = new DeskflowPreviewState { Id = "owned", Phase = phase } };
        await using var session = CreateSession(backend);
        session.ObserveFrame(Frame());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EnableAsync());
        await session.CheckSafetyAsync();
        await session.StopAsync("重试");
        Assert.False(session.CanStart);
        Assert.Contains("尚未确认", session.Status);
        Assert.Equal(0, backend.EndCalls);
    }

    [Fact]
    public async Task FailureBeforeBeginAllowsAnotherAttempt()
    {
        var backend = new Backend();
        await using var session = new DeskflowPreviewSession(backend, (enabled, _) => enabled ? throw new IOException("capture unavailable") : Task.CompletedTask, new NoTimerTimeProvider());
        await Assert.ThrowsAsync<IOException>(() => session.EnableAsync());
        Assert.True(session.CanStart);
        Assert.Equal(0, backend.BeginCalls);
    }

    [Fact]
    public async Task ConfirmedEndAllowsAnotherAttempt()
    {
        var backend = new Backend();
        await using var session = CreateSession(backend);
        session.ObserveFrame(Frame());
        await session.EnableAsync();
        Assert.True(session.IsActive);
        await session.StopAsync("已归还");
        Assert.True(session.CanStart);
        Assert.Equal(1, backend.EndCalls);
    }

    [Fact]
    public async Task EndReturningDifferentActiveSessionMustNotReportReturnOrUnlockNewPreview()
    {
        var backend = new Backend { EndResult = new DeskflowPreviewState { Id = "other", Phase = DeskflowPreviewPhase.Active } };
        var captureRestored = 0;
        await using var session = new DeskflowPreviewSession(backend, (enabled, _) =>
        {
            if (!enabled) captureRestored++;
            return Task.CompletedTask;
        }, new NoTimerTimeProvider());
        session.ObserveFrame(Frame());
        await session.EnableAsync();
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<string>();
        session.Changed += () => statuses.Enqueue(session.Status);

        await session.StopAsync("预览已关闭，键鼠已归还");

        Assert.Contains("尚未确认", session.Status);
        Assert.Contains("检查键鼠", session.Status);
        Assert.False(session.CanStart);
        Assert.Equal(1, captureRestored);
        Assert.Equal(1, backend.EndCalls);
        Assert.DoesNotContain(statuses, text => text.Contains("键鼠已归还", StringComparison.Ordinal));

        await session.CheckSafetyAsync();
        await session.StopAsync("预览已关闭，键鼠已归还");
        Assert.False(session.CanStart);
        Assert.Contains("尚未确认", session.Status);
        Assert.Equal(1, backend.EndCalls);
        Assert.DoesNotContain(statuses, text => text.Contains("键鼠已归还", StringComparison.Ordinal));
    }

    private static DeskflowPreviewSession CreateSession(Backend backend) => new(backend, (_, _) => Task.CompletedTask, new NoTimerTimeProvider());
    private static PreviewFrame Frame() => new() { State = "live", Width = 1, Height = 1, JpegBytes = [1], CapturedAtUtc = DateTimeOffset.UtcNow };

    private sealed class Backend : IDeskflowPreviewControl
    {
        public bool FailBegin, FailEnd;
        public DeskflowPreviewState? BeginResult;
        public DeskflowPreviewState? EndResult;
        public TaskCompletionSource<DeskflowPreviewState>? BeginPending;
        public int BeginCalls, EndCalls;
        public bool PreviewHandoffSupported => true;
        public string PreviewHandoffHint => "fixture";
        public string PreviewReturnShortcut => "return";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken cancellationToken = default)
        {
            BeginCalls++;
            if (BeginPending is not null) return BeginPending.Task;
            return FailBegin ? Task.FromException<DeskflowPreviewState>(new IOException("lost")) :
                Task.FromResult(BeginResult ?? new DeskflowPreviewState { Id = "owned", Phase = DeskflowPreviewPhase.Active });
        }
        public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken cancellationToken = default)
        {
            EndCalls++;
            return FailEnd ? Task.FromException<DeskflowPreviewState>(new IOException("lost")) : Task.FromResult(EndResult ?? new DeskflowPreviewState());
        }
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DeskflowPreviewState());
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(new DeskflowPreviewState());
    }

    private sealed class NoTimerTimeProvider : TimeProvider
    {
        public Action? OnRead;
        public override DateTimeOffset GetUtcNow() { OnRead?.Invoke(); return DateTimeOffset.UtcNow; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
