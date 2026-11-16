using System.IO;
using System.Runtime.CompilerServices;
using Nian.Input.Control;

namespace Nian.Input.Windows;

/// <summary>Owns physical capture after an authenticated Begin ACK. Local restoration always precedes network cleanup.</summary>
public sealed class WindowsKeyboardControlSession : IAsyncDisposable
{
    private static readonly TimeSpan ActiveLease = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PendingLease = TimeSpan.FromSeconds(5);
    private sealed class ReturnDispatchState
    {
        internal readonly object Gate = new();
        internal long Generation;
    }
    private static readonly ConditionalWeakTable<DesktopControlArbiter, ReturnDispatchState> returnStates = new();
    private sealed class Run(long generation)
    {
        internal readonly long Generation = generation;
        internal readonly object Gate = new(), CleanupGate = new();
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource<InputReply> Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DesktopControlLease? Lease;
        internal IKeyboardCapture? Capture;
        internal InputSendPump? Pump;
        internal Task? Cleanup;
        internal bool BeginAttempted;
        internal int Stopped, EndStarted, ReturnNotified;
    }
    private sealed class PhysicalFactory : ICaptureFactory
    {
        public IKeyboardCapture Create(Func<InputPacket, bool> enqueue, Action<string> stopped) => new WindowsDesktopCapture(enqueue, stopped);
    }
    private readonly IKeyboardControlChannel channel;
    private readonly DesktopControlArbiter arbiter;
    private readonly string peer;
    private readonly ICaptureFactory captureFactory;
    private readonly ReturnDispatchState returnState;
    private readonly SemaphoreSlim operations = new(1, 1);
    private Run? active;
    private int disposed;
    private string? lastError, lastStopReason;

    public WindowsKeyboardControlSession(IKeyboardControlChannel channel, DesktopControlArbiter sharedArbiter, string peerFingerprint, ICaptureFactory? captureFactory = null)
    {
        ArgumentNullException.ThrowIfNull(channel); ArgumentNullException.ThrowIfNull(sharedArbiter);
        if (peerFingerprint is not { Length: 64 } || !peerFingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("A pinned peer fingerprint is required.", nameof(peerFingerprint));
        this.channel = channel; arbiter = sharedArbiter; peer = peerFingerprint.ToUpperInvariant();
        this.captureFactory = captureFactory ?? new PhysicalFactory();
        returnState = returnStates.GetValue(sharedArbiter, _ => new());
    }

    public bool IsRunning
    {
        get
        {
            var run = Volatile.Read(ref active);
            return run is not null && Volatile.Read(ref run.Stopped) == 0 && Volatile.Read(ref run.Capture)?.IsRunning == true && Volatile.Read(ref run.Pump)?.IsStopped == false;
        }
    }
    public string? LastError => Volatile.Read(ref lastError);
    public string? LastStopReason => Volatile.Read(ref lastStopReason);
    /// <summary>Only an explicit physical emergency-return key emits this intent. The host decides whether to apply DDC.</summary>
    public event Action<string>? LocalReturnRequested;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        Run? run = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (IsRunning) return;
            long nextGeneration;
            lock (returnState.Gate)
            {
                // Publish the new user action before waiting for old transport cleanup, including across instances.
                nextGeneration = checked(++returnState.Generation);
            }
            if (Volatile.Read(ref active) is { } previous) await CleanupAsync(previous).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (returnState.Gate)
                if (nextGeneration != returnState.Generation) throw new OperationCanceledException("Keyboard start was superseded.");
            run = new(nextGeneration); Volatile.Write(ref active, run); Volatile.Write(ref lastError, null); Volatile.Write(ref lastStopReason, null);
            var current = run;
            var reservation = arbiter.Enter(peer, DesktopControlMode.Keyboard, reason => StopLocal(current, reason), PendingLease, replaceSamePeer: true);
            Volatile.Write(ref run.Lease, reservation);
            // Stop can occur after publishing active but before Enter returns. Never leave that late reservation owned.
            if (Volatile.Read(ref run.Stopped) != 0) { arbiter.Stop(reservation, "start-cancelled"); throw new OperationCanceledException(); }
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Stop.Token);
            run.BeginAttempted = true;
            var reply = await channel.BeginAsync(request.Token).WaitAsync(request.Token).ConfigureAwait(false);
            if (!reply.Ok) throw new InvalidOperationException("The receiver did not acknowledge keyboard control.");
            request.Token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref run.Stopped) != 0) throw new OperationCanceledException(request.Token);
            var started = arbiter.Execute(run.Lease, () =>
            {
                lock (current.Gate)
                {
                    request.Token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref current.Stopped) != 0) throw new OperationCanceledException(request.Token);
                    if (!arbiter.Renew(current.Lease!, ActiveLease)) throw new OperationCanceledException(request.Token);
                    current.Capture = captureFactory.Create(packet => Enqueue(current, packet), reason => CaptureStopped(current, reason));
                    current.Pump = new InputSendPump((packet, token) => SendAsync(current, packet, token),
                        token => HeartbeatAsync(current, token), token => EndAsync(current, token));
                    current.Capture.Start(ActiveLease);
                    if (!current.Capture.IsRunning || Volatile.Read(ref current.Stopped) != 0) throw new OperationCanceledException(request.Token);
                }
            });
            if (!started || Volatile.Read(ref run.Stopped) != 0) throw new OperationCanceledException(request.Token);
            _ = ObservePumpAsync(run);
        }
        catch
        {
            if (run is not null) { StopLocal(run, "start-failed"); await CleanupAsync(run).ConfigureAwait(false); }
            throw;
        }
        finally { operations.Release(); }
    }

    private static bool Enqueue(Run run, InputPacket packet) => Volatile.Read(ref run.Stopped) == 0 && Volatile.Read(ref run.Pump)?.TryEnqueue(packet) == true;

    private bool IsCurrent(Run run) => Volatile.Read(ref run.Stopped) == 0 && run.Lease is { } lease && arbiter.Execute(lease, () => { });

    private async Task<InputReply> SendAsync(Run run, InputPacket packet, CancellationToken token)
    {
        try
        {
            if (!IsCurrent(run)) throw new IOException("Keyboard control ownership changed.");
            var reply = await channel.SendAsync(packet, token).WaitAsync(token).ConfigureAwait(false);
            if (!reply.Ok) StopLocal(run, "input-rejected");
            return reply;
        }
        catch { StopLocal(run, "connection-or-send-failed"); throw; }
    }

    private async Task<InputReply> HeartbeatAsync(Run run, CancellationToken token)
    {
        try
        {
            if (!IsCurrent(run)) throw new IOException("Keyboard control ownership changed.");
            var reply = await channel.HeartbeatAsync(token).WaitAsync(token).ConfigureAwait(false);
            if (!reply.Ok) { StopLocal(run, "heartbeat-rejected"); return reply; }
            var renewed = false;
            var current = arbiter.Execute(run.Lease!, () =>
            {
                lock (run.Gate)
                {
                    if (Volatile.Read(ref run.Stopped) != 0 || run.Capture?.IsRunning != true) return;
                    // An ACK alone cannot renew a stale generation, and local native safety must still pass.
                    renewed = run.Capture.RenewLease(ActiveLease) && arbiter.Renew(run.Lease!, ActiveLease);
                }
            });
            if (!current || !renewed)
            {
                StopLocal(run, "capture-renewal-failed");
                return new(false, "heartbeat", reply.Session, reply.Sequence, "Local keyboard capture is no longer active.");
            }
            return reply;
        }
        catch { StopLocal(run, "connection-or-heartbeat-failed"); throw; }
    }

    private void CaptureStopped(Run run, string reason)
    {
        StopLocal(run, reason);
        lock (returnState.Gate)
        {
            if (Volatile.Read(ref disposed) != 0 || run.Generation != returnState.Generation) return;
            if (reason.Contains("native-recovery-error", StringComparison.Ordinal)) Volatile.Write(ref lastError, "Native input restoration reported a failure.");
            if (!reason.StartsWith("emergency-stop", StringComparison.Ordinal) || Interlocked.Exchange(ref run.ReturnNotified, 1) != 0) return;
            Volatile.Write(ref lastStopReason, reason);
        }
        // Intent delivery is independent of idempotent restoration and never runs on a native callback stack.
        ThreadPool.QueueUserWorkItem(_ => NotifyLocalReturn(run, reason));
    }

    private void StopLocal(Run run, string reason)
    {
        lock (run.Gate)
        {
            // A concurrent cleanup must not observe Stopped until it can join this restoration gate.
            if (Volatile.Read(ref run.Stopped) == 0)
            {
                Volatile.Write(ref run.Stopped, 1);
                Volatile.Write(ref lastStopReason, reason);
                // No network or asynchronous UI work can precede the native unhook/cursor restore.
                try { run.Capture?.Stop(reason); }
                catch { Volatile.Write(ref lastError, "Native input restoration reported a failure."); try { run.Capture?.Dispose(); } catch { } }
                try { run.Stop.Cancel(); } catch (ObjectDisposedException) { }
                if (run.Pump is { } pump) _ = pump.StopAsync();
            }
        }
        // Duplicate callbacks still join release of this exact reservation before any host notification.
        if (Volatile.Read(ref run.Lease) is { } lease) arbiter.Stop(lease, reason);
    }

    private bool CanNotifyReturn(Run run)
    {
        lock (returnState.Gate) return Volatile.Read(ref disposed) == 0 && run.Generation == returnState.Generation;
    }

    private void NotifyLocalReturn(Run run, string reason)
    {
        // A preview/other owner supersedes the old emergency; its consumed intent must never be retried.
        if (arbiter.Current is not null || !CanNotifyReturn(run) || LocalReturnRequested is not { } handlers) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            if (!CanNotifyReturn(run)) break;
            // External code runs with no restoration, ownership, operations, or notification lock held.
            // The host revalidates the source/revision when consuming its asynchronous switch request.
            try { handler(reason); } catch { }
        }
    }

    private async Task<InputReply> EndAsync(Run run, CancellationToken token)
    {
        StopLocal(run, "sharing-ended");
        if (Interlocked.CompareExchange(ref run.EndStarted, 1, 0) == 0)
        {
            try
            {
                var reply = await channel.EndAsync(token).WaitAsync(token).ConfigureAwait(false);
                run.Ended.TrySetResult(reply); return reply;
            }
            catch (Exception error) { run.Ended.TrySetException(error); _ = run.Ended.Task.Exception; throw; }
        }
        return await run.Ended.Task.WaitAsync(token).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => StopAsync("user-stop", cancellationToken);
    public async Task StopAsync(string reason, CancellationToken cancellationToken = default)
    {
        var run = Volatile.Read(ref active);
        if (run is not null) StopLocal(run, string.IsNullOrWhiteSpace(reason) ? "user-stop" : reason);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref active);
            if (current is not null) { StopLocal(current, reason); await CleanupAsync(current).ConfigureAwait(false); }
            else if (run is not null) await CleanupAsync(run).ConfigureAwait(false);
        }
        finally { operations.Release(); }
    }

    private Task CleanupAsync(Run run)
    {
        TaskCompletionSource completion;
        lock (run.CleanupGate)
        {
            if (run.Cleanup is { } existing) return existing;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            run.Cleanup = completion.Task;
        }
        // Async/native work must run outside CleanupGate; pump completion can reenter this method.
        _ = CompleteCleanupAsync(run, completion);
        return completion.Task;
    }
    private async Task CompleteCleanupAsync(Run run, TaskCompletionSource completion)
    {
        try { await CleanupCoreAsync(run).ConfigureAwait(false); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
    }
    private async Task CleanupCoreAsync(Run run)
    {
        StopLocal(run, "sharing-ended");
        try
        {
            if (run.Pump is { } pump) await pump.DisposeAsync().ConfigureAwait(false);
            else if (run.BeginAttempted)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await EndAsync(run, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) { Volatile.Write(ref lastError, "Keyboard connection cleanup failed (" + error.GetType().Name + ")."); }
        finally
        {
            lock (run.Gate) { try { run.Capture?.Dispose(); } catch { Volatile.Write(ref lastError, "Native capture disposal reported a failure."); } }
            Interlocked.CompareExchange(ref active, null, run);
            run.Stop.Dispose();
        }
    }
    private async Task ObservePumpAsync(Run run)
    {
        await run.Pump!.Completion.ConfigureAwait(false);
        StopLocal(run, run.Pump.Failure is null ? "sharing-ended" : "connection-or-queue-failed");
        await CleanupAsync(run).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) await StopAsync("disposed").ConfigureAwait(false);
    }
}
