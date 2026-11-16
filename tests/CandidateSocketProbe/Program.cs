using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DesktopToolbox.DeskLink;
using NianLink.CandidateDiscovery;
using NianLink.PrivateUdpProbe;

internal static class Program
{
    private static string stage = "initialization";
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("Candidate synthetic probe is disabled."); return 0; }
        if (args.Length != 1 || args[0] is not ("--run" or "--worker")) return 2;
        if (!ProbeExecutionPolicy.Matches(Environment.ProcessPath, Assembly.GetExecutingAssembly().Location))
        { Console.Error.WriteLine("Candidate probe requires its dedicated apphost."); return 2; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (args[0] == "--worker") await WorkerAsync(deadline.Token);
            else await RunAsync(deadline.Token);
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine($"Candidate synthetic probe timed out at {stage}."); return 1; }
        catch (Exception error)
        {
            var category = error is SocketException socketError ? socketError.SocketErrorCode.ToString() : error.GetType().Name;
            Console.Error.WriteLine($"Candidate synthetic probe failed at {stage} ({category}); no fallback was attempted."); return 1;
        }
    }

    private static DiscoveryNetworkBinding Bind(ProbeInterface selected)
    {
        var approved = VerifiedLanBinding.VerifyCurrentNetwork(selected.Address, true);
        var binding = DiscoveryNetworkBinding.CreateForCurrentNetwork(approved);
        if (!Guid.TryParse(binding.InterfaceId, out var id) || id != selected.InterfaceId || binding.InterfaceIndex != selected.Index ||
            !binding.Address.Equals(selected.Address) || !binding.SubnetMask.Equals(selected.Mask) || !binding.IsHardwareAvailable())
            throw new InvalidOperationException("Candidate interface verification failed.");
        return binding;
    }

    private static async Task RunAsync(CancellationToken ct)
    {
        stage = "interface enumeration";
        var selected = PrivateUdpProbeRules.SelectUnique(await ProbeEnvironment.ReadAsync(ct));
        var binding = Bind(selected);
        var pipeName = "nian-candidate-probe-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        stage = "scanner configuration";
        using var scanner = WindowsDiscoveryDatagramSocket.OpenForSyntheticTest(binding, false);
        if (scanner.SyntheticPort is < 49152 or > 65535) throw new InvalidOperationException();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException();
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--worker");
        var challenge = RandomNumberGenerator.GetBytes(16);
        info.Environment["NIAN_CANDIDATE_PIPE"] = pipeName;
        info.Environment["NIAN_CANDIDATE_PARENT"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["NIAN_CANDIDATE_CHALLENGE"] = Convert.ToBase64String(challenge);
        info.Environment["NIAN_CANDIDATE_ID"] = selected.InterfaceId.ToString("D");
        info.Environment["NIAN_CANDIDATE_INDEX"] = selected.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["NIAN_CANDIDATE_ADDRESS"] = selected.Address.ToString();
        info.Environment["NIAN_CANDIDATE_MASK"] = selected.Mask.ToString();
        using var child = Process.Start(info) ?? throw new InvalidOperationException();
        DateTime startTime = default;
        string? childPath = null;
        bool identityVerified = false;
        try
        {
            startTime = child.StartTime.ToUniversalTime();
            childPath = child.MainModule?.FileName ?? throw new InvalidOperationException();
            if (!Path.GetFullPath(childPath).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            identityVerified = true;
            stage = "worker verification";
            await pipe.WaitForConnectionAsync(ct);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != (uint)child.Id || child.HasExited ||
                child.StartTime.ToUniversalTime() != startTime || !string.Equals(child.MainModule?.FileName, childPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException();
            await pipe.WriteAsync(challenge, ct); await pipe.FlushAsync(ct);
            var ready = new byte[7]; await pipe.ReadExactlyAsync(ready, ct);
            if (ready[0] != 1 || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid) || pid != (uint)child.Id ||
                BinaryPrimitives.ReadUInt32LittleEndian(ready.AsSpan(1, 4)) != pid || child.HasExited ||
                child.StartTime.ToUniversalTime() != startTime || !string.Equals(child.MainModule?.FileName, childPath, StringComparison.OrdinalIgnoreCase) ||
                BinaryPrimitives.ReadUInt16LittleEndian(ready.AsSpan(5)) != DiscoveryProtocol.Port) throw new InvalidOperationException();
            var nonce = RandomNumberGenerator.GetBytes(16);
            if (nonce.All(value => value == 0)) throw new InvalidOperationException();
            var control = new byte[18]; BinaryPrimitives.WriteUInt16LittleEndian(control, (ushort)scanner.SyntheticPort); nonce.CopyTo(control, 2);
            await pipe.WriteAsync(control, ct); await pipe.FlushAsync(ct);
            stage = "same-host synthetic exchange";
            await scanner.SendSyntheticQueryAsync(nonce, ct);
            await scanner.ReceiveSyntheticReplyAsync(nonce, ct);
            var done = new byte[1]; await pipe.ReadExactlyAsync(done, ct);
            await child.WaitForExitAsync(ct);
            if (done[0] != 2 || child.ExitCode != 0 || !binding.IsHardwareAvailable() ||
                !PrivateUdpProbeRules.IsCurrent(selected, await ProbeEnvironment.ReadAsync(ct))) throw new InvalidOperationException();
            stage = "port release check";
            using var check = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            check.Bind(new IPEndPoint(binding.Address, DiscoveryProtocol.Port));
            Console.WriteLine("Candidate synthetic same-host exchange and port release passed.");
        }
        finally
        {
            pipe.Dispose();
            if (!child.HasExited)
                await ProbeChildCleanup.CompleteAsync(identityVerified,
                    () => child.StartTime.ToUniversalTime() == startTime && string.Equals(child.MainModule?.FileName, childPath, StringComparison.OrdinalIgnoreCase),
                    () => child.Kill(), child.WaitForExitAsync);
        }
    }

    private static async Task WorkerAsync(CancellationToken ct)
    {
        stage = "worker verification";
        var pipeName = Environment.GetEnvironmentVariable("NIAN_CANDIDATE_PIPE") ?? "";
        const string prefix = "nian-candidate-probe-";
        if (!pipeName.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(pipeName[prefix.Length..], "N", out _))
            throw new InvalidOperationException();
        var expectedId = Guid.Parse(Environment.GetEnvironmentVariable("NIAN_CANDIDATE_ID")!);
        int expectedIndex = int.Parse(Environment.GetEnvironmentVariable("NIAN_CANDIDATE_INDEX")!, System.Globalization.CultureInfo.InvariantCulture);
        var address = IPAddress.Parse(Environment.GetEnvironmentVariable("NIAN_CANDIDATE_ADDRESS")!);
        var mask = IPAddress.Parse(Environment.GetEnvironmentVariable("NIAN_CANDIDATE_MASK")!);
        var selected = PrivateUdpProbeRules.Select(true, address, await ProbeEnvironment.ReadAsync(ct));
        if (selected.InterfaceId != expectedId || selected.Index != expectedIndex || !selected.Mask.Equals(mask)) throw new InvalidOperationException();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(ct);
        int parent = int.Parse(Environment.GetEnvironmentVariable("NIAN_CANDIDATE_PARENT")!, System.Globalization.CultureInfo.InvariantCulture);
        var expectedChallenge = Convert.FromBase64String(Environment.GetEnvironmentVariable("NIAN_CANDIDATE_CHALLENGE")!);
        var challenge = new byte[16]; await pipe.ReadExactlyAsync(challenge, ct);
        if (!ProbeParentIdentity.Verify(pipe.SafePipeHandle, parent, expectedChallenge, challenge) ||
            !PrivateUdpProbeRules.IsCurrent(selected, await ProbeEnvironment.ReadAsync(ct))) throw new InvalidOperationException();
        var binding = Bind(selected);
        stage = "responder configuration";
        using var responder = WindowsDiscoveryDatagramSocket.OpenForSyntheticTest(binding, true);
        var ready = new byte[7]; ready[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(ready.AsSpan(1, 4), (uint)Environment.ProcessId);
        BinaryPrimitives.WriteUInt16LittleEndian(ready.AsSpan(5), (ushort)responder.SyntheticPort);
        await pipe.WriteAsync(ready, ct); await pipe.FlushAsync(ct);
        var control = new byte[18]; await pipe.ReadExactlyAsync(control, ct);
        int peerPort = BinaryPrimitives.ReadUInt16LittleEndian(control);
        if (peerPort is < 49152 or > 65535) throw new InvalidOperationException();
        var nonce = control.AsSpan(2, 16).ToArray();
        stage = "same-host synthetic response";
        var packet = await responder.ReceiveAsync(ct);
        await responder.ReplySyntheticAsync(packet, nonce, peerPort, ct);
        await pipe.WriteAsync(new byte[] { 2 }, ct); await pipe.FlushAsync(ct);
    }
}
