using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using static Nian.Input.Windows.Win32DesktopInputNative;
using NativeInput = Nian.Input.Windows.Win32DesktopInputNative.Input;

namespace Nian.Input.Windows;

/// <summary>Documented SendInput path; every unknown native safety result rejects input.</summary>
internal sealed class Win32DesktopInputPlatform : IWindowsDesktopInputPlatform
{
    private static readonly HashSet<string> SecurityProcesses = new(StringComparer.OrdinalIgnoreCase)
    { "consent.exe", "logonui.exe", "winlogon.exe", "credentialuibroker.exe", "useroobebroker.exe", "lockapp.exe", "systemsettings.exe" };
    private readonly Func<nint, bool> protectedOwnWindow;
    private volatile bool awake = true, disposed;
    private bool notificationsInstalled;
    public event Action? SafetyChanged;

    internal Win32DesktopInputPlatform(Func<nint, bool>? protectedOwnWindow)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SendInput requires Windows.");
        this.protectedOwnWindow = protectedOwnWindow ?? (_ => true);
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            notificationsInstalled = true;
        }
        catch
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            throw;
        }
    }

    public WindowsDesktopEnvironment ReadEnvironment(bool preview = false)
    {
        if (disposed || !notificationsInstalled) return WindowsDesktopEnvironment.Unsafe;
        try
        {
            return new(IsDefaultInputDesktop(), IsActiveConsoleSession(), awake, IsWindowAllowed(GetForegroundWindow(), preview));
        }
        catch { return WindowsDesktopEnvironment.Unsafe; }
    }

    private static bool IsDefaultInputDesktop()
    {
        var station = new StringBuilder(256);
        if (!ObjectName(GetProcessWindowStation(), 2, station, 512, out _) || station.ToString() != "WinSta0") return false;
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == 0) return false;
        try
        {
            var name = new StringBuilder(256);
            return ObjectName(desktop, 2, name, 512, out _) && name.ToString() == "Default" &&
                ObjectBoolean(desktop, 6, out var receivingInput, sizeof(int), out _) && receivingInput != 0;
        }
        finally { CloseDesktop(desktop); }
    }

    private static bool IsActiveConsoleSession()
    {
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var session) || session != WTSGetActiveConsoleSessionId()) return false;
        if (!WTSQuerySessionInformation(0, session, 8, out var buffer, out var bytes)) return false;
        try { return buffer != 0 && bytes >= sizeof(int) && Marshal.ReadInt32(buffer) == 0; } // WTSActive
        finally { if (buffer != 0) WTSFreeMemory(buffer); }
    }

    private bool IsWindowAllowed(nint window, bool preview = false)
    {
        if (window == 0) return false;
        var root = GetAncestor(window, 2);
        if (root == 0 || GetWindowThreadProcessId(root, out var pid) == 0 || pid == 0) return false;
        if (!ProcessIdToSessionId(pid, out var targetSession) || !ProcessIdToSessionId((uint)Environment.ProcessId, out var ownSession) || targetSession != ownSession) return false;
        if (pid == Environment.ProcessId) return !preview && !protectedOwnWindow(root);
        var target = OpenProcess(0x1000, false, pid);
        var own = OpenProcess(0x1000, false, (uint)Environment.ProcessId);
        if (target == 0 || own == 0) { if (target != 0) CloseHandle(target); if (own != 0) CloseHandle(own); return false; }
        try
        {
            var image = new StringBuilder(32768); var size = (uint)image.Capacity;
            if (!QueryFullProcessImageName(target, 0, image, ref size) || SecurityProcesses.Contains(Path.GetFileName(image.ToString()))) return false;
            var targetIntegrity = ReadIntegrity(target); var ownIntegrity = ReadIntegrity(own);
            // Do not try to bypass UIPI or elevate. Unknown/higher target integrity is unavailable.
            return targetIntegrity is { } targetLevel && ownIntegrity is { } ownLevel && targetLevel <= ownLevel;
        }
        finally { CloseHandle(target); CloseHandle(own); }
    }

    private static uint? ReadIntegrity(nint process)
    {
        if (!OpenProcessToken(process, 0x8, out var token)) return null;
        nint buffer = 0;
        try
        {
            GetTokenInformation(token, 25, 0, 0, out var bytes); // TokenIntegrityLevel
            if (bytes < nint.Size + sizeof(uint) || bytes > 4096) return null;
            buffer = Marshal.AllocHGlobal((int)bytes);
            if (!GetTokenInformation(token, 25, buffer, bytes, out _)) return null;
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == 0 || !IsValidSid(sid)) return null;
            var countPointer = GetSidSubAuthorityCount(sid);
            if (countPointer == 0) return null;
            var count = Marshal.ReadByte(countPointer);
            if (count is < 1 or > 15) return null;
            var value = GetSidSubAuthority(sid, (uint)(count - 1));
            return value == 0 ? null : unchecked((uint)Marshal.ReadInt32(value));
        }
        finally { if (buffer != 0) Marshal.FreeHGlobal(buffer); CloseHandle(token); }
    }

    public IReadOnlyList<WindowsDesktopDisplay> ReadDisplays()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return ReadNativeDisplays();
    }

    internal static IReadOnlyList<WindowsDesktopDisplay> ReadNativeDisplays()
    {
        using var dpi = new PhysicalPixelScope();
        var monitors = new List<(string Id, Rect Bounds, bool Primary)>(); var failed = false;
        bool Collect(nint monitor, nint dc, ref Rect rectangle, nint data)
        {
            try
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
                if (!GetMonitorInfo(monitor, ref info) || string.IsNullOrWhiteSpace(info.Device) || monitors.Count >= 64) { failed = true; return false; }
                monitors.Add((info.Device, info.Monitor, (info.Flags & 1) != 0)); return true;
            }
            catch { failed = true; return false; } // Managed exceptions never cross the EnumDisplayMonitors callback.
        }
        if (!EnumDisplayMonitors(0, 0, Collect, 0) || failed || monitors.Count == 0) throw new InvalidOperationException("Unable to identify physical display bounds.");
        var ordered = monitors.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        var layout = string.Join(";", ordered.Select(m => string.Create(CultureInfo.InvariantCulture,
            $"{m.Id}|{m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Right},{m.Bounds.Bottom},{m.Primary}")));
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout)));
        return ordered.Select(m => new WindowsDesktopDisplay(m.Id, revision, m.Bounds.Left, m.Bounds.Top,
            (double)m.Bounds.Right - m.Bounds.Left, (double)m.Bounds.Bottom - m.Bounds.Top, m.Primary)).ToArray();
    }

    public WindowsDesktopPoint ReadPointer()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var dpi = new PhysicalPixelScope();
        if (!GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(point.X, point.Y);
    }

    public bool IsPointerAllowed(WindowsDesktopPoint position, bool preview = false)
    {
        if (disposed || !position.IsValid || position.X < int.MinValue || position.X > int.MaxValue || position.Y < int.MinValue || position.Y > int.MaxValue) return false;
        try
        {
            using var dpi = new PhysicalPixelScope();
            return IsWindowAllowed(WindowFromPoint(new() { X = (int)Math.Round(position.X), Y = (int)Math.Round(position.Y) }), preview);
        }
        catch { return false; }
    }

    public void Post(WindowsDesktopAction action, WindowsDesktopPoint position)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var dpi = new PhysicalPixelScope();
        var displays = ReadDisplays();
        if (displays.Count == 0 || displays.Any(d => !d.IsValid) || !displays.Any(d => d.Bounds.Contains(position))) throw new InvalidOperationException("Display geometry changed before posting input.");
        if (action.ExpectedSource is not null && !displays.Any(d => d.Matches(action.ExpectedSource)))
            throw new InvalidOperationException("Preview source identity changed before posting input.");
        var left = displays.Min(d => d.Left); var top = displays.Min(d => d.Top);
        var desktop = new WindowsDesktopBounds(left, top, displays.Max(d => d.Left + d.Width) - left, displays.Max(d => d.Top + d.Height) - top);
        var encoded = WindowsDesktopInputEncoding.Encode(action, position, desktop);
        var inputs = encoded.Select(value => value.Keyboard
            ? new NativeInput { Type = 1, Data = new() { Keyboard = new() { ScanCode = value.ScanCode, Flags = value.Flags, ExtraInfo = value.ExtraInfo } } }
            : new NativeInput { Type = 0, Data = new() { Mouse = new() { X = value.X, Y = value.Y, MouseData = value.MouseData, Flags = value.Flags, ExtraInfo = value.ExtraInfo } } }).ToArray();
        // Recheck immediately before SendInput. Protected application windows also need the local marker guard.
        var failure = ReadEnvironment(action.Preview).Failure;
        if (failure.Length != 0 || action.Kind is not (InputKind.KeyDown or InputKind.KeyUp) && !IsPointerAllowed(position, action.Preview))
            throw new InvalidOperationException(failure.Length > 0 ? failure : "The pointer target became protected.");
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        // UIPI deliberately does not expose a unique error code. Any partial/zero count fails closed.
        if (sent != inputs.Length) throw new InvalidOperationException("SendInput did not accept the complete event batch; input may be blocked by UIPI.");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args) => NotifySafetyChanged();
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend) awake = false;
        else if (args.Mode == PowerModes.Resume) awake = true;
        else return;
        NotifySafetyChanged();
    }
    private void NotifySafetyChanged() { if (!disposed) try { SafetyChanged?.Invoke(); } catch { } }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (!notificationsInstalled) return;
        notificationsInstalled = false;
        SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private sealed class PhysicalPixelScope : IDisposable
    {
        private readonly nint previous = SetThreadDpiAwarenessContext(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        internal PhysicalPixelScope() { if (previous == 0) throw new InvalidOperationException("Unable to establish physical desktop coordinates."); }
        public void Dispose() { SetThreadDpiAwarenessContext(previous); }
    }
}
