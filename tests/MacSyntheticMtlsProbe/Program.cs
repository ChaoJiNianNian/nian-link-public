using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MacSyntheticMtlsProbe;

public sealed record ProbeOptions(string Role, IPAddress Local, IPAddress Peer, int Port)
{
    public static ProbeOptions Parse(string[] args)
    {
        if (args.Length != 5 || args[0] != "--run" || args[1] is not ("server" or "client") ||
            !IPAddress.TryParse(args[2], out var local) || !IsPrivate(local) ||
            !IPAddress.TryParse(args[3], out var peer) || !IsPrivate(peer) || local.Equals(peer) ||
            !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 49152 or > 65535 or 49473)
            throw new ArgumentException("Invalid synthetic probe arguments.");
        return new(args[1], local, peer, port);
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
    }
}

public static class ProbeInput
{
    // Redirected console reads can block synchronously on macOS despite an async
    // cancellation token. Waiting separately lets the probe exit on deadline;
    // an unfinished background read is abandoned only when this process exits.
    public static async Task<string?> ReadPinAsync(TextReader input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var read = Task.Run(() => input.ReadLine(), CancellationToken.None);
        return await read.WaitAsync(ct).ConfigureAwait(false);
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ProbeOptions options;
        try { options = ProbeOptions.Parse(args); }
        catch (ArgumentException) { Console.WriteLine("{\"status\":\"not_started\"}"); return 2; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            using var certificate = SyntheticCertificate.Create();
            Console.WriteLine(JsonSerializer.Serialize(new { status = "ready", role = options.Role, fingerprint = CertificatePin.Get(certificate) }));
            var pin = await ProbeInput.ReadPinAsync(Console.In, deadline.Token).ConfigureAwait(false);
            if (!CertificatePin.IsValid(pin)) throw new ArgumentException("Invalid pin.");
            var version = options.Role == "server"
                ? await ProbeSession.ServerAsync(options.Local, options.Peer, options.Port, certificate, pin!,
                    _ => Console.WriteLine("{\"status\":\"listening\"}"), deadline.Token).ConfigureAwait(false)
                : await ProbeSession.ClientAsync(options.Local, options.Peer, options.Port, certificate, pin!, deadline.Token).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "ok", tls = version.ToString() }));
            return 0;
        }
        catch (Exception) { Console.WriteLine("{\"status\":\"failed\"}"); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
