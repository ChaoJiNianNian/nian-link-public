namespace Nian.Input.Windows;

/// <summary>Pure capture policy. Event methods run on the capture thread; Stop may run on the watchdog.</summary>
internal sealed class DesktopCaptureSession(Func<InputPacket, bool> enqueue, Action<string> stopped)
{
    private int running;
    private long deadline;
    private long lastObserved;
    private string? stopReason;
    private readonly object leaseGate = new();
    private readonly HashSet<InputKey> modifiers = [];

    public bool IsRunning => Volatile.Read(ref running) != 0;
    public string? StopReason => Volatile.Read(ref stopReason);

    public void Begin(TimeSpan lease, long nowMilliseconds)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(lease), "Control lease must be within five minutes.");
        lock (leaseGate)
        {
            if (IsRunning) throw new InvalidOperationException("Capture is already running.");
            modifiers.Clear(); lastObserved = nowMilliseconds;
            deadline = checked(nowMilliseconds + (long)Math.Ceiling(lease.TotalMilliseconds));
            Volatile.Write(ref stopReason, null);
            Volatile.Write(ref running, 1);
        }
    }

    public void Check(long nowMilliseconds, bool interactiveDesktop)
    {
        CheckOrRenew(nowMilliseconds, interactiveDesktop, null);
    }

    public bool RenewLease(TimeSpan lease, long nowMilliseconds, bool interactiveDesktop)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(lease));
        return CheckOrRenew(nowMilliseconds, interactiveDesktop, lease);
    }

    private bool CheckOrRenew(long nowMilliseconds, bool interactiveDesktop, TimeSpan? renewal)
    {
        string? failure = null;
        lock (leaseGate)
        {
            if (!IsRunning) return false;
            failure = !interactiveDesktop ? "desktop-unavailable" : nowMilliseconds < lastObserved ? "capture-clock-changed" : nowMilliseconds >= deadline ? "lease-expired" : null;
            lastObserved = nowMilliseconds;
            if (failure is not null)
            {
                Volatile.Write(ref stopReason, failure);
                Volatile.Write(ref running, 0);
            }
            else if (renewal is { } duration) deadline = checked(nowMilliseconds + (long)Math.Ceiling(duration.TotalMilliseconds));
        }
        // Native recovery has its own lifecycle gate. Never invoke it while holding leaseGate.
        if (failure is not null) stopped(failure);
        return failure is null && IsRunning;
    }

    public void Stop(string reason)
    {
        bool notify;
        lock (leaseGate)
        {
            notify = IsRunning;
            if (notify)
            {
                // Cleanup may observe the stopped state before this policy's notification is delivered.
                Volatile.Write(ref stopReason, reason);
                Volatile.Write(ref running, 0);
            }
        }
        if (notify) stopped(reason);
    }

    // LLKHF_INJECTED or LLKHF_LOWER_IL_INJECTED: never forward, suppress, or act on emergency keys.
    public bool Keyboard(int virtualKey, int scanCode, int flags, bool down)
    {
        if (!IsRunning || (flags & 0x12) != 0) return false;
        if (down && (virtualKey == 0x7b || virtualKey == 0x1b && HasControl && HasAlt))
        {
            Stop("emergency-stop");
            return true;
        }
        if (!DesktopKeyMap.TryMap(virtualKey, scanCode, (flags & 1) != 0, out var key))
        {
            Stop("unsupported-key");
            return false;
        }
        if ((int)key >= 224)
        {
            if (down) modifiers.Add(key);
            else modifiers.Remove(key);
        }
        return Submit(new(0, down ? InputKind.KeyDown : InputKind.KeyUp, (int)key));
    }

    // WM_MOUSEMOVE deliberately passes through; raw motion is independent of the clipped desktop cursor.
    public bool Mouse(int message, uint data, uint flags)
    {
        if (!IsRunning || (flags & 3) != 0 || message == 0x200) return false;
        var (kind, code) = message switch
        {
            0x201 => (InputKind.ButtonDown, 0), 0x202 => (InputKind.ButtonUp, 0),
            0x204 => (InputKind.ButtonDown, 1), 0x205 => (InputKind.ButtonUp, 1),
            0x207 => (InputKind.ButtonDown, 2), 0x208 => (InputKind.ButtonUp, 2),
            0x20b => (InputKind.ButtonDown, (int)(data >> 16) + 2),
            0x20c => (InputKind.ButtonUp, (int)(data >> 16) + 2),
            0x20a => (InputKind.Wheel, 0),
            _ => (InputKind.MoveRelative, -1)
        };
        // Current wire contract has three mouse buttons and a vertical wheel.
        if (code is < 0 or > 2)
        {
            Stop("unsupported-mouse-input");
            return false;
        }
        return Submit(new(0, kind, code, Delta: kind == InputKind.Wheel ? (short)(data >> 16) : 0));
    }

    public void RelativeMove(int x, int y)
    {
        if (!IsRunning || x == 0 && y == 0) return;
        if (x is < -32767 or > 32767 || y is < -32767 or > 32767)
        {
            Stop("invalid-raw-motion");
            return;
        }
        Submit(new(0, InputKind.MoveRelative, X: x, Y: y));
    }

    public void AbsoluteMouse() => Stop("absolute-pointing-device-not-supported");

    private bool HasControl => modifiers.Contains(InputKey.LeftControl) || modifiers.Contains(InputKey.RightControl);
    private bool HasAlt => modifiers.Contains(InputKey.LeftAlt) || modifiers.Contains(InputKey.RightAlt);

    private bool Submit(InputPacket packet)
    {
        if (!IsRunning) return false;
        try
        {
            if (enqueue(packet)) return IsRunning;
        }
        catch { /* A caller-supplied queue must never leave local input suppressed after failure. */ }
        Stop("send-queue-unavailable");
        return false;
    }
}
