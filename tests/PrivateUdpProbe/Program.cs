using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NianLink.PrivateUdpProbe;

internal static class Program
{
    private static string stage = "initialization";
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("Private UDP probe is disabled."); return 0; }
        if (args.Length != 1 || (args[0] != "--run" && args[0] != "--worker")) return 2;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (args[0] == "--worker") await WorkerAsync(deadline.Token);
            else await RunAsync(deadline.Token);
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine($"Private UDP probe timed out or was cancelled at {stage}."); return 1; }
        catch (Exception error)
        {
            string category = error is System.Net.Sockets.SocketException socketError ? socketError.SocketErrorCode.ToString() : error.GetType().Name;
            Console.Error.WriteLine($"Private UDP probe failed at {stage} ({category}); no fallback was attempted."); return 1;
        }
    }

    private static async Task RunAsync(CancellationToken ct)
    {
        stage = "interface enumeration";
        var interfaces = await ProbeEnvironment.ReadAsync(ct);
        var selected = PrivateUdpProbeRules.SelectUnique(interfaces);
        IPAddress group;
        do { var b = RandomNumberGenerator.GetBytes(2); group = IPAddress.Parse($"239.255.{1 + b[0] % 254}.{1 + b[1] % 254}"); }
        while (!PrivateUdpProbeRules.IsAllowedGroup(group));
        string pipeName = "nian-udp-probe-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        stage = "requester socket configuration";
        using var requester = ProbeSocket.Open(selected, group, false);
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--worker");
        start.Environment["NIAN_UDP_CHILD"] = "1";
        start.Environment["NIAN_UDP_PIPE"] = pipeName;
        start.Environment["NIAN_UDP_IFACE"] = selected.InterfaceId.ToString("D");
        start.Environment["NIAN_UDP_INDEX"] = selected.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["NIAN_UDP_IP"] = selected.Address.ToString();
        start.Environment["NIAN_UDP_MASK"] = selected.Mask.ToString();
        start.Environment["NIAN_UDP_GROUP"] = group.ToString();
        byte[] challenge = RandomNumberGenerator.GetBytes(16);
        start.Environment["NIAN_UDP_PARENT"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["NIAN_UDP_CHALLENGE"] = Convert.ToBase64String(challenge);
        using var child = Process.Start(start) ?? throw new InvalidOperationException();
        DateTime startTime = default;
        string? childPath = null;
        bool identityVerified = false;
        try
        {
            startTime = child.StartTime.ToUniversalTime();
            childPath = child.MainModule?.FileName ?? throw new InvalidOperationException();
            if (!Path.GetFullPath(childPath).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            identityVerified = true;
            stage = "worker identity verification";
            await pipe.WaitForConnectionAsync(ct);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint firstPid) || firstPid != (uint)child.Id || child.HasExited ||
                child.StartTime.ToUniversalTime() != startTime || !string.Equals(child.MainModule?.FileName, childPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException();
            await pipe.WriteAsync(challenge, ct); await pipe.FlushAsync(ct);
            var ready = new byte[7]; await pipe.ReadExactlyAsync(ready, ct);
            if (ready[0] != 1 || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint actualPid) ||
                actualPid != (uint)child.Id || BinaryPrimitives.ReadUInt32LittleEndian(ready.AsSpan(1, 4)) != actualPid ||
                child.HasExited || child.StartTime.ToUniversalTime() != startTime ||
                !string.Equals(child.MainModule?.FileName, childPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            int childPort = BinaryPrimitives.ReadUInt16LittleEndian(ready.AsSpan(5));
            if (childPort is < 49152 or > 65535) throw new InvalidOperationException();
            byte[] port = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(port, (ushort)requester.Port);
            await pipe.WriteAsync(port, ct); await pipe.FlushAsync(ct);
            byte[] nonce = RandomNumberGenerator.GetBytes(16);
            stage = "synthetic query and reply";
            await requester.SendQueryAsync(nonce, childPort, ct);
            await requester.ReceiveReplyAsync(nonce, childPort, ct);
            var done = new byte[1]; await pipe.ReadExactlyAsync(done, ct);
            await child.WaitForExitAsync(ct);
            if (done[0] != 2 || child.ExitCode != 0) throw new InvalidOperationException();
            stage = "final interface verification";
            if (!PrivateUdpProbeRules.IsCurrent(selected, await ProbeEnvironment.ReadAsync(ct))) throw new InvalidOperationException();
            ct.ThrowIfCancellationRequested();
            Console.WriteLine("Private UDP synthetic query and reply passed interface checks.");
        }
        finally
        {
            pipe.Dispose();
            if (!child.HasExited)
            {
                await ProbeChildCleanup.CompleteAsync(identityVerified,
                    () => child.StartTime.ToUniversalTime() == startTime && string.Equals(child.MainModule?.FileName, childPath, StringComparison.OrdinalIgnoreCase),
                    () => child.Kill(), child.WaitForExitAsync);
            }
        }
    }

    private static async Task WorkerAsync(CancellationToken ct)
    {
        stage = "worker initialization";
        if (Environment.GetEnvironmentVariable("NIAN_UDP_CHILD") != "1") throw new InvalidOperationException();
        string pipeName = Environment.GetEnvironmentVariable("NIAN_UDP_PIPE") ?? "";
        if (!pipeName.StartsWith("nian-udp-probe-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(pipeName[15..], "N", out _)) throw new InvalidOperationException();
        var id = Guid.Parse(Environment.GetEnvironmentVariable("NIAN_UDP_IFACE")!);
        int index = int.Parse(Environment.GetEnvironmentVariable("NIAN_UDP_INDEX")!, System.Globalization.CultureInfo.InvariantCulture);
        var address = IPAddress.Parse(Environment.GetEnvironmentVariable("NIAN_UDP_IP")!);
        var mask = IPAddress.Parse(Environment.GetEnvironmentVariable("NIAN_UDP_MASK")!);
        var group = IPAddress.Parse(Environment.GetEnvironmentVariable("NIAN_UDP_GROUP")!);
        var selected = PrivateUdpProbeRules.Select(true, address, await ProbeEnvironment.ReadAsync(ct));
        if (selected.InterfaceId != id || selected.Index != index || !selected.Mask.Equals(mask)) throw new InvalidOperationException();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(ct);
        int expectedParent = int.Parse(Environment.GetEnvironmentVariable("NIAN_UDP_PARENT")!, System.Globalization.CultureInfo.InvariantCulture);
        byte[] expectedChallenge = Convert.FromBase64String(Environment.GetEnvironmentVariable("NIAN_UDP_CHALLENGE")!);
        byte[] challenge = new byte[16]; await pipe.ReadExactlyAsync(challenge, ct);
        if (!ProbeParentIdentity.Verify(pipe.SafePipeHandle, expectedParent, expectedChallenge, challenge)) throw new InvalidOperationException();
        if (!PrivateUdpProbeRules.IsCurrent(selected, await ProbeEnvironment.ReadAsync(ct))) throw new InvalidOperationException();
        stage = "responder socket configuration";
        using var responder = ProbeSocket.Open(selected, group, true);
        byte[] ready = new byte[7]; ready[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(ready.AsSpan(1, 4), (uint)Environment.ProcessId);
        BinaryPrimitives.WriteUInt16LittleEndian(ready.AsSpan(5), (ushort)responder.Port);
        await pipe.WriteAsync(ready, ct); await pipe.FlushAsync(ct);
        var port = new byte[2]; await pipe.ReadExactlyAsync(port, ct);
        int parentPort = BinaryPrimitives.ReadUInt16LittleEndian(port);
        if (parentPort is < 49152 or > 65535) throw new InvalidOperationException();
        stage = "synthetic query and reply";
        await responder.ReceiveAndReplyAsync(parentPort, ct);
        await pipe.WriteAsync(new byte[] { 2 }, ct); await pipe.FlushAsync(ct);
    }
}
