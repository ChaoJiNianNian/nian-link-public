using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
namespace DesktopToolbox.DeskLink;

internal static class LocalControlTransport
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int getsockopt(int socket, int level, int option, out int value, ref uint valueLength);
    public static Task<Stream> ConnectOwnedCoreAsync(int processId, CancellationToken ct) => ConnectAsync("deskflow-core", ct, processId);
    public static async Task<Stream> ConnectAsync(string name, CancellationToken ct, int? expectedProcessId = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(ct);
                if (expectedProcessId is { } expected && (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) || actual != expected))
                    throw new IOException("Deskflow IPC belongs to a different process.");
                return pipe;
            }
            catch { pipe.Dispose(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(Path.GetTempPath(), name)), ct);
            if (expectedProcessId is { } expected && OperatingSystem.IsMacOS())
            {
                uint length = sizeof(int);
                // Darwin <sys/un.h>: SOL_LOCAL=0, LOCAL_PEERPID=2. This authenticates
                // the server process before any stop command or peer state is accepted.
                if (getsockopt(socket.SafeHandle.DangerousGetHandle().ToInt32(), 0, 2, out var actual, ref length) != 0 || actual != expected)
                    throw new IOException("Deskflow IPC belongs to a different process.");
            }
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
