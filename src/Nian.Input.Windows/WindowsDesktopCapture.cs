namespace Nian.Input.Windows;

/// <summary>
/// Explicit, short-lived physical input capture for an isolated control application.
/// Construction and disposal before Start install no hook and change no cursor setting.
/// The queue callback must be a nonblocking TryEnqueue; stopped is delivered on the thread pool.
/// </summary>
public sealed class WindowsDesktopCapture : IKeyboardCapture
{
    private readonly Func<InputPacket, bool> enqueue;
    private readonly Action<string> stopped;
    private readonly object lifecycle = new();
    private WindowsDesktopCapturePlatform? platform;
    private Thread? thread;
    private bool disposed;

    public WindowsDesktopCapture(Func<InputPacket, bool> enqueue, Action<string> stopped)
    {
        this.enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        this.stopped = stopped ?? throw new ArgumentNullException(nameof(stopped));
    }

    public bool IsRunning => Volatile.Read(ref platform)?.IsRunning == true;

    public void Start(TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(lease), "Control lease must be within five minutes.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        WindowsDesktopCapturePlatform next;
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (thread?.IsAlive == true) throw new InvalidOperationException("The previous capture has not stopped.");
            next = new(enqueue, stopped);
            platform = next;
            thread = new(() => next.Run(lease)) { IsBackground = true, Name = "Nian desktop input capture" };
            thread.Start();
        }
        // No application callbacks or UI dispatch are waited on during this startup handshake.
        if (!next.Started.Wait(TimeSpan.FromSeconds(3)))
        {
            next.Stop("capture-start-timeout");
            throw new TimeoutException("The capture thread did not become ready.");
        }
        if (next.StartupError is { } error)
            throw new InvalidOperationException("Desktop capture could not start. Local input has been restored.", error);
        if (!next.IsRunning) throw new InvalidOperationException("Desktop capture stopped during startup.");
    }

    /// <summary>Unhooks and restores the cursor before returning; remote release notification is asynchronous.</summary>
    public void Stop(string reason) => Volatile.Read(ref platform)?.Stop(string.IsNullOrWhiteSpace(reason) ? "stopped" : reason);

    /// <summary>Explicit production renewal only. Existing Start callers still expire at their original lease.</summary>
    public bool RenewLease(TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(lease));
        return !Volatile.Read(ref disposed) && Volatile.Read(ref platform)?.RenewLease(lease) == true;
    }

    public void Dispose()
    {
        WindowsDesktopCapturePlatform? current;
        lock (lifecycle)
        {
            if (disposed) return;
            disposed = true;
            current = platform;
        }
        current?.Stop("disposed");
        // No join: the UI may be calling Dispose while the queue callback is finishing.
        // Native restoration above is synchronous and does not depend on that callback returning.
    }
}
