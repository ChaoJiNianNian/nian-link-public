using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Fact]
    public async Task MarkerAndOldDataSurviveTerminationOfIsolatedChild()
    {
        if (Environment.GetEnvironmentVariable("NIAN_PREPARE_CHILD") == "1") { await RunIsolatedPrepareChildAsync(); return; }
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var pipeName = "nian-prepare-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null, testHost = null;
        try
        {
            string oldHostToken; byte[] activeBefore;
            await using (var vault = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector))
            {
                oldHostToken = await vault.AddConfirmedAsync("old-host-device", new string('A', 64));
                activeBefore = await File.ReadAllBytesAsync(Path.Combine(hostRoot, "multi-device-trust.bin"));
            }
            var oldPeer = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24801, KeyboardPort = 24800, DataPort = 24802,
                HostName = "old-host", PeerName = "old-peer", CertificateSha256 = new string('B', 64), Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            await using (var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector)) await peer.SaveConfirmedAsync(oldPeer, oldPeer.CertificateSha256);
            var oldPeerBytes = await File.ReadAllBytesAsync(Path.Combine(peerRoot, "peer-invitation.bin"));
            process = StartIsolatedPrepareChild(hostRoot, peerRoot, pipeName);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var connected = pipe.WaitForConnectionAsync(deadline.Token); var exited = process.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(connected, exited) == exited) throw new InvalidOperationException("Isolated child exited before the marker barrier.");
            await connected; var signal = new byte[5]; await pipe.ReadExactlyAsync(signal, deadline.Token); Assert.Equal(1, signal[0]);
            var testHostId = BinaryPrimitives.ReadInt32LittleEndian(signal.AsSpan(1));
            var candidate = Process.GetProcessById(testHostId);
            try
            {
                var exe = Path.GetFullPath(candidate.MainModule?.FileName ?? throw new InvalidOperationException("Missing test executable path."));
                var runnerExe = Path.GetFullPath(process.MainModule?.FileName ?? throw new InvalidOperationException("Missing runner executable path."));
                var testExe = Path.Combine(AppContext.BaseDirectory, "testhost.exe");
                if (testHostId == Environment.ProcessId || candidate.StartTime.ToUniversalTime() < process.StartTime.ToUniversalTime() ||
                    !IsTestProcessDescendant(testHostId, process.Id) ||
                    !string.Equals(exe, runnerExe, StringComparison.OrdinalIgnoreCase) && !string.Equals(exe, testExe, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected isolated test process.");
                testHost = candidate; candidate = null!;
            }
            finally { candidate?.Dispose(); }
            var markerPath = Path.Combine(hostRoot, "pending-prepare-unknown.bin"); var pendingPath = Path.Combine(hostRoot, "pending-grant.bin");
            var markerBefore = await File.ReadAllBytesAsync(markerPath); var pendingBefore = await File.ReadAllBytesAsync(pendingPath);
            Assert.False(process.HasExited); Assert.False(testHost.HasExited);
            testHost.Kill(entireProcessTree: true); await testHost.WaitForExitAsync(deadline.Token);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(deadline.Token);
            await using var reopened = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            Assert.Equal(activeBefore, await File.ReadAllBytesAsync(Path.Combine(hostRoot, "multi-device-trust.bin")));
            Assert.Equal(markerBefore, await File.ReadAllBytesAsync(markerPath)); Assert.Equal(pendingBefore, await File.ReadAllBytesAsync(pendingPath));
            Assert.True(reopened.IsAuthorized("old-host-device", oldHostToken)); Assert.Single(reopened.GetTrustedDevices());
            var markerPlain = protector.Unprotect(markerBefore);
            try
            {
                using var parsed = JsonDocument.Parse(markerPlain);
                var stage = parsed.RootElement.GetProperty("stageId").GetGuid();
                var identity = parsed.RootElement.GetProperty("identity").Deserialize<PairingSessionIdentity>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AbortPendingGrantAsync(stage, identity));
                Assert.NotNull(await reopened.GetPendingGrantMetadataAsync(DateTimeOffset.UtcNow));
            }
            finally { CryptographicOperations.ZeroMemory(markerPlain); }
            await using var peerReopened = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            Assert.Equal(oldPeerBytes, await File.ReadAllBytesAsync(Path.Combine(peerRoot, "peer-invitation.bin")));
            Assert.True(peerReopened.Get() is not null); Assert.Null(await peerReopened.GetPendingMetadataAsync());
        }
        finally
        {
            if (testHost is not null) { if (!testHost.HasExited) { testHost.Kill(entireProcessTree: true); await testHost.WaitForExitAsync(); } testHost.Dispose(); }
            if (process is not null) { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } process.Dispose(); }
            CleanVault(hostRoot); CleanVault(peerRoot);
        }
    }

    private static async Task RunIsolatedPrepareChildAsync()
    {
        var hostRoot = Environment.GetEnvironmentVariable("NIAN_PREPARE_HOST_ROOT") ?? throw new InvalidOperationException();
        var peerRoot = Environment.GetEnvironmentVariable("NIAN_PREPARE_PEER_ROOT") ?? throw new InvalidOperationException();
        var pipeName = Environment.GetEnvironmentVariable("NIAN_PREPARE_PIPE") ?? throw new InvalidOperationException();
        ValidateChildRoot(hostRoot); ValidateChildRoot(peerRoot);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var f = await Fixture.OpenAsync(certificateFolder: Path.Combine(hostRoot, "child-test-certs")); await f.BeginAsync(); await f.ConfirmAsync();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(hostRoot, new WindowsCredentialProtector());
        await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, new WindowsCredentialProtector());
        var now = f.Clock(); var stage = (await vault.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
        await vault.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
        var barrier = new ControlledLivePublishBarrier(); vault.BeforeLivePrepareSend = barrier;
        _ = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability());
        _ = vault.SendPrepareAndReceiveReadyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
        try
        {
            await barrier.Entered.WaitAsync(deadline.Token);
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            var signal = new byte[5]; signal[0] = 1; BinaryPrimitives.WriteInt32LittleEndian(signal.AsSpan(1), Environment.ProcessId);
            await pipe.WriteAsync(signal, deadline.Token); await pipe.FlushAsync(deadline.Token);
            await Task.Delay(Timeout.Infinite, deadline.Token);
        }
        finally { barrier.Release(); }
    }

    private static Process StartIsolatedPrepareChild(string hostRoot, string peerRoot, string pipeName)
    {
        ValidateChildRoot(hostRoot); ValidateChildRoot(peerRoot);
        if (string.Equals(hostRoot, peerRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test roots must differ.");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tests", "ConditionalCommitIsolation.Tests", "ConditionalCommitIsolation.Tests.csproj"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Test project root unavailable.");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? @"D:\Projects\pc-toolbox\.tools\dotnet\dotnet.exe";
        var info = new ProcessStartInfo(dotnet) { WorkingDirectory = root.FullName, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "test", "tests/ConditionalCommitIsolation.Tests/ConditionalCommitIsolation.Tests.csproj", "-c", "Release", "--no-build", "--nologo", "--filter", "FullyQualifiedName~MarkerAndOldDataSurviveTerminationOfIsolatedChild", "--logger", "console;verbosity=quiet" }) info.ArgumentList.Add(arg);
        info.Environment["NIAN_PREPARE_CHILD"] = "1"; info.Environment["NIAN_PREPARE_HOST_ROOT"] = hostRoot;
        info.Environment["NIAN_PREPARE_PEER_ROOT"] = peerRoot; info.Environment["NIAN_PREPARE_PIPE"] = pipeName;
        var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start isolated child.");
        _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static void ValidateChildRoot(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        const string prefix = "nian-live-vault-"; var name = Path.GetFileName(full);
        if (!Path.IsPathFullyQualified(root) || !string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
            !Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Unsafe isolated child directory.");
    }

    private static bool IsTestProcessDescendant(int childId, int runnerId)
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1)) throw new IOException("Unable to inspect isolated test process tree.");
        try
        {
            var parents = new Dictionary<int, int>(); var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) throw new IOException("Unable to inspect isolated test processes.");
            do { parents[(int)entry.ProcessId] = (int)entry.ParentProcessId; } while (Process32Next(snapshot, ref entry));
            var current = childId; var visited = new HashSet<int>();
            while (visited.Add(current) && parents.TryGetValue(current, out var parent))
            {
                if (parent == runnerId) return true;
                current = parent;
            }
            return false;
        }
        finally { CloseHandle(snapshot); }
    }

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
}
