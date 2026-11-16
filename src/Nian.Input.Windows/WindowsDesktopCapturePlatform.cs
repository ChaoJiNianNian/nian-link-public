using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static Nian.Input.Windows.WindowsDesktopCaptureNativeMethods;

namespace Nian.Input.Windows;

/// <summary>
/// Raw relative motion plus LL key/button suppression. Mouse motion is never suppressed by the LL hook:
/// this avoids relying on undocumented delivery of WM_INPUT after a hook has eaten that mouse movement.
/// ClipCursor affects the legacy cursor only; the physical RAWMOUSE deltas have no screen-edge coordinates.
/// </summary>
internal sealed class WindowsDesktopCapturePlatform
{
    private const uint StopMessage = 0x8001;
    // If an OS unhook call fails, Windows might still own the function pointer. Keeping that
    // delegate alive leaves the failed hook as a safe passthrough instead of a dangling callback.
    private static readonly System.Collections.Concurrent.ConcurrentBag<HookProc> uncertainHooks = [];
    private readonly DesktopCaptureSession session;
    private readonly Action<string> stopped;
    private readonly DesktopCaptureRecovery recovery;
    private readonly object lifecycle = new();
    private readonly HookProc keyboardProc;
    private readonly HookProc mouseProc;
    private readonly WindowProc windowProc;
    private readonly string className = "NianDesktopInput_" + Guid.NewGuid().ToString("N");
    private nint keyboardHook, mouseHook, window, rawBuffer;
    private uint captureThreadId;
    private Rect originalClip, pinnedClip;
    private bool clipChanged, rawRegistered, sessionRegistered;
    private int stopping, notified;
    private long pulse, lastPhysicalMoveHook;
    private Timer? watchdog;

    internal ManualResetEventSlim Started { get; } = new(false);
    internal Exception? StartupError { get; private set; }
    internal bool IsRunning => session.IsRunning;

    internal bool RenewLease(TimeSpan lease)
    {
        if (Volatile.Read(ref stopping) != 0 || !session.IsRunning) return false;
        try
        {
            if (!CursorConstraintIntact()) { Stop("cursor-constraint-changed"); return false; }
            return session.RenewLease(lease, Environment.TickCount64, InteractiveDesktop());
        }
        catch { Stop("capture-renewal-failed"); return false; }
    }

    internal WindowsDesktopCapturePlatform(Func<InputPacket, bool> enqueue, Action<string> stopped)
    {
        this.stopped = stopped;
        session = new(enqueue, OnPolicyStopped);
        recovery = new(ReleaseKeyboard, ReleaseMouse, RestoreCursor);
        keyboardProc = KeyboardCallback;
        mouseProc = MouseCallback;
        windowProc = WindowCallback;
    }

    internal void Run(TimeSpan lease)
    {
        var registeredClass = false;
        var module = GetModuleHandle(null);
        try
        {
            captureThreadId = GetCurrentThreadId();
            lock (lifecycle)
            {
                if (Volatile.Read(ref stopping) != 0) throw new OperationCanceledException();
                if (!InteractiveDesktop()) throw new InvalidOperationException("An unlocked local Windows desktop is required.");
                RequireReleasedPhysicalInput();
                RequireUnclaimedRawMouse();
                var wc = new WindowClass
                {
                    Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = windowProc,
                    Instance = module, Name = className
                };
                if (RegisterClassEx(ref wc) == 0) throw NativeFailure("Register capture window class");
                registeredClass = true;
                // An invisible top-level window also receives session, power and display changes.
                window = CreateWindowEx(0, className, "Nian isolated input capture", 0x80000000, 0, 0, 0, 0, 0, 0, module, 0);
                if (window == 0) throw NativeFailure("Create capture window");
                if (!WTSRegisterSessionNotification(window, 0)) throw NativeFailure("Register session guard");
                sessionRegistered = true;
                rawBuffer = Marshal.AllocHGlobal(64);
                if (!RegisterRawInputDevices([new() { Page = 1, Usage = 2, Flags = 0x100, Target = window }], 1, (uint)Marshal.SizeOf<RawDevice>()))
                    throw NativeFailure("Register raw mouse");
                rawRegistered = true;
                keyboardHook = SetWindowsHookEx(13, keyboardProc, module, 0);
                if (keyboardHook == 0) throw NativeFailure("Install keyboard hook");
                mouseHook = SetWindowsHookEx(14, mouseProc, module, 0);
                if (mouseHook == 0) throw NativeFailure("Install mouse hook");
                RequireReleasedPhysicalInput();
                if (!GetClipCursor(out originalClip) || !GetCursorPos(out var position)) throw NativeFailure("Read cursor confinement");
                pinnedClip = new() { Left = position.X, Top = position.Y, Right = position.X + 1, Bottom = position.Y + 1 };
                if (!ClipCursor(ref pinnedClip)) throw NativeFailure("Temporarily constrain local cursor");
                clipChanged = true;
                if (SetTimer(window, 1, 100, 0) == 0) throw NativeFailure("Start capture safety timer");
                if (Volatile.Read(ref stopping) != 0) throw new OperationCanceledException();
                pulse = lastPhysicalMoveHook = Environment.TickCount64;
                session.Begin(lease, pulse);
                watchdog = new(_ =>
                {
                    try
                    {
                        session.Check(Environment.TickCount64, InteractiveDesktop());
                        if (session.IsRunning && !CursorConstraintIntact()) Stop("cursor-constraint-changed");
                        if (session.IsRunning && Environment.TickCount64 - Volatile.Read(ref pulse) > 1500)
                            Stop("capture-thread-unresponsive");
                    }
                    catch { Stop("capture-watchdog-error"); }
                }, null, 250, 250);
            }
            Started.Set();
            int result;
            while ((result = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                if (message.Id == StopMessage) break;
                DispatchMessage(ref message);
            }
            if (result < 0) throw NativeFailure("Read capture message");
        }
        catch (Exception error)
        {
            if (!Started.IsSet) StartupError = error;
            Stop("capture-platform-error");
        }
        finally
        {
            Stop("capture-ended");
            watchdog?.Dispose();
            // Only capture-thread resources are destroyed here. Input restoration has already happened.
            if (window != 0)
            {
                KillTimer(window, 1);
                if (sessionRegistered) WTSUnRegisterSessionNotification(window);
                DestroyWindow(window);
                window = 0;
            }
            if (rawRegistered)
            {
                RegisterRawInputDevices([new() { Page = 1, Usage = 2, Flags = 1, Target = 0 }], 1, (uint)Marshal.SizeOf<RawDevice>());
                rawRegistered = false;
            }
            if (rawBuffer != 0) { Marshal.FreeHGlobal(rawBuffer); rawBuffer = 0; }
            if (registeredClass) UnregisterClass(className, module);
            Started.Set();
            GC.KeepAlive(keyboardProc);
            GC.KeepAlive(mouseProc);
            GC.KeepAlive(windowProc);
        }
    }

    internal void Stop(string reason)
    {
        Interlocked.Exchange(ref stopping, 1);
        lock (lifecycle)
        {
            session.Stop(reason);
            RecoverAndNotify(reason);
        }
        var threadId = Volatile.Read(ref captureThreadId);
        if (threadId != 0) PostThreadMessage(threadId, StopMessage, 0, 0);
    }

    private void OnPolicyStopped(string reason)
    {
        Interlocked.Exchange(ref stopping, 1);
        lock (lifecycle) RecoverAndNotify(reason);
        var threadId = Volatile.Read(ref captureThreadId);
        if (threadId != 0) PostThreadMessage(threadId, StopMessage, 0, 0);
    }

    private void RecoverAndNotify(string reason)
    {
        var recovered = recovery.Restore();
        if (Interlocked.Exchange(ref notified, 1) != 0) return;
        // Generic cleanup can arrive before OnPolicyStopped; the policy has already committed the original cause.
        var committedReason = session.StopReason ?? reason;
        var result = recovered ? committedReason : committedReason + "; native-recovery-error";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { stopped(result); }
            catch { /* User notification must not break native restoration or crash a hook thread. */ }
        });
    }

    private nint KeyboardCallback(int code, nuint message, nint data)
    {
        if (code < 0 || !session.IsRunning) return CallNextHookEx(0, code, message, data);
        try
        {
            Volatile.Write(ref pulse, Environment.TickCount64);
            var input = Marshal.PtrToStructure<KeyboardHook>(data);
            var down = message is 0x100 or 0x104;
            if ((message is 0x100 or 0x101 or 0x104 or 0x105) &&
                session.Keyboard((int)input.Key, (int)input.Scan, (int)input.Flags, down)) return 1;
        }
        catch { Stop("keyboard-callback-error"); }
        return CallNextHookEx(0, code, message, data);
    }

    private nint MouseCallback(int code, nuint message, nint data)
    {
        if (code < 0 || !session.IsRunning) return CallNextHookEx(0, code, message, data);
        try
        {
            Volatile.Write(ref pulse, Environment.TickCount64);
            var input = Marshal.PtrToStructure<MouseHook>(data);
            if (message == 0x200 && (input.Flags & 3) == 0) Volatile.Write(ref lastPhysicalMoveHook, Environment.TickCount64);
            if (session.Mouse((int)message, input.Data, input.Flags)) return 1;
        }
        catch { Stop("mouse-callback-error"); }
        return CallNextHookEx(0, code, message, data);
    }

    private nint WindowCallback(nint handle, uint message, nuint wParam, nint lParam)
    {
        try
        {
            Volatile.Write(ref pulse, Environment.TickCount64);
            switch (message)
            {
                case 0xff when session.IsRunning:
                    ReadRawMouse(lParam);
                    // WM_INPUT/RIM_INPUT requires DefWindowProc for native buffer cleanup.
                    break;
                case 0x113:
                    Volatile.Write(ref pulse, Environment.TickCount64);
                    session.Check(Environment.TickCount64, InteractiveDesktop());
                    if (session.IsRunning && !CursorConstraintIntact())
                        Stop("cursor-constraint-changed");
                    break;
                case 0x2b1: // WM_WTSSESSION_CHANGE
                case 0x218: // WM_POWERBROADCAST
                case 0x7e:  // WM_DISPLAYCHANGE
                    if (session.IsRunning) Stop("system-session-changed");
                    break;
                case 0x11: // WM_QUERYENDSESSION
                    Stop("session-ending");
                    return 1;
                case 0x16 when wParam != 0:
                    Stop("session-ended");
                    break;
            }
        }
        catch { Stop("capture-window-error"); }
        return DefWindowProc(handle, message, wParam, lParam);
    }

    private void ReadRawMouse(nint input)
    {
        uint size = 64;
        var headerSize = (uint)Marshal.SizeOf<RawHeader>();
        var copied = GetRawInputData(input, 0x10000003, rawBuffer, ref size, headerSize);
        if (copied == uint.MaxValue || copied < headerSize + Marshal.SizeOf<RawMouse>())
            throw NativeFailure("Read raw mouse");
        var header = Marshal.PtrToStructure<RawHeader>(rawBuffer);
        if (header.Type != 0 || header.Device == 0) return; // No synthetic/unknown-source packet loops.
        var mouse = Marshal.PtrToStructure<RawMouse>(rawBuffer + (int)headerSize);
        if ((mouse.Flags & 1) != 0) { session.AbsoluteMouse(); return; }
        if (mouse.X == 0 && mouse.Y == 0) return;
        if (Environment.TickCount64 - Volatile.Read(ref lastPhysicalMoveHook) > 1000)
        {
            Stop("mouse-hook-unavailable");
            return;
        }
        session.RelativeMove(mouse.X, mouse.Y);
    }

    private static void RequireReleasedPhysicalInput()
    {
        for (var key = 1; key < 256; key++)
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                throw new InvalidOperationException("Release all physical keys and mouse buttons before starting desktop control.");
    }

    private static void RequireUnclaimedRawMouse()
    {
        uint count = 0;
        var size = (uint)Marshal.SizeOf<RawDevice>();
        if (GetRegisteredRawInputDevices(null, ref count, size) == uint.MaxValue)
            throw NativeFailure("Read existing raw registrations");
        if (count == 0) return;
        if (count > 1024) throw new InvalidOperationException("Unexpected number of raw input registrations.");
        var devices = new RawDevice[count];
        if (GetRegisteredRawInputDevices(devices, ref count, size) == uint.MaxValue)
            throw NativeFailure("Read existing raw registrations");
        if (devices.Any(d => d.Page == 1 && d.Usage == 2))
            throw new InvalidOperationException("Another component in this process already owns raw mouse input. Use the isolated desktop control app.");
    }

    private static bool InteractiveDesktop()
    {
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var sessionId) || WTSGetActiveConsoleSessionId() != sessionId) return false;
        var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS only.
        if (desktop == 0) return false;
        try
        {
            var name = new StringBuilder(128);
            return GetUserObjectInformation(desktop, 2, name, 256, out _) && name.ToString() == "Default";
        }
        finally { CloseDesktop(desktop); }
    }

    private void ReleaseKeyboard()
    {
        var hook = Interlocked.Exchange(ref keyboardHook, 0);
        if (hook != 0 && !UnhookWindowsHookEx(hook))
        {
            var failure = NativeFailure("Release keyboard hook");
            uncertainHooks.Add(keyboardProc);
            throw failure;
        }
    }

    private void ReleaseMouse()
    {
        var hook = Interlocked.Exchange(ref mouseHook, 0);
        if (hook != 0 && !UnhookWindowsHookEx(hook))
        {
            var failure = NativeFailure("Release mouse hook");
            uncertainHooks.Add(mouseProc);
            throw failure;
        }
    }

    private void RestoreCursor()
    {
        if (!clipChanged) return;
        clipChanged = false;
        // Do not overwrite a confinement another application installed after ours.
        if (GetClipCursor(out var current) && !current.Equals(pinnedClip)) return;
        if (!ClipCursor(ref originalClip) && !ReleaseClipCursor(0)) throw NativeFailure("Restore cursor confinement");
    }

    private static Win32Exception NativeFailure(string operation) => new(Marshal.GetLastWin32Error(), operation);
    private bool CursorConstraintIntact() => GetClipCursor(out var current) && current.Equals(pinnedClip);
}
