using System.ComponentModel;
using System.Net.Sockets;

namespace Nian.Input;

internal static class InputDiagnostics
{
    public static string Describe(Exception error)
    {
        var codes = new List<string>();
        for (Exception? current = error; current is not null && codes.Count < 3; current = current.InnerException)
        {
            var code = current.GetType().Name + ":" + current.HResult.ToString("X8");
            if (current is Win32Exception native) code += ":native=" + native.NativeErrorCode;
            if (current is SocketException socket) code += ":socket=" + socket.SocketErrorCode;
            codes.Add(code);
        }
        return string.Join("/",codes);
    }
}
