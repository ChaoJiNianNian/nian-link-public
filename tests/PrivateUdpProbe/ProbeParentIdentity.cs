using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace NianLink.PrivateUdpProbe;

internal static class ProbeParentIdentity
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    internal static bool Matches(int expected, int server, int parent, string serverPath, string ownPath, byte[] expectedChallenge, byte[] actualChallenge) =>
        expected > 0 && expected == server && expected == parent && !string.IsNullOrWhiteSpace(serverPath) &&
        string.Equals(serverPath, ownPath, StringComparison.OrdinalIgnoreCase) && expectedChallenge.Length == 16 &&
        actualChallenge.Length == 16 && CryptographicOperations.FixedTimeEquals(expectedChallenge, actualChallenge);

    internal static bool Verify(SafePipeHandle pipe, int expected, byte[] expectedChallenge, byte[] actualChallenge)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint server)) return false;
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new IOException("Unable to inspect probe parent process.");
        int parent = 0;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) throw new IOException("Unable to inspect probe processes.");
            do { if (entry.ProcessId == (uint)Environment.ProcessId) { parent = (int)entry.ParentProcessId; break; } }
            while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        using var process = Process.GetProcessById(expected);
        if (process.HasExited || process.StartTime.ToUniversalTime() > Process.GetCurrentProcess().StartTime.ToUniversalTime()) return false;
        return Matches(expected, (int)server, parent, process.MainModule?.FileName ?? "", Environment.ProcessPath ?? "", expectedChallenge, actualChallenge);
    }
}
