using System.Buffers.Binary;
using System.Diagnostics;
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
    public async Task PublishedPeerV3SurvivesVerifiedChildTerminationBeforeReady()
    {
        if (Environment.GetEnvironmentVariable("NIAN_PREPARE_V3_CHILD") == "1") { await RunIsolatedV3ChildAsync(); return; }
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        var pipeName = "nian-v3-recovery-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? runner = null, testHost = null;
        try
        {
            string oldToken;
            await using (var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector)) oldToken = await host.AddConfirmedAsync("old-host-device", new string('C', 64));
            var oldPeer = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24801, KeyboardPort = 24800, DataPort = 24802, HostName = "old-host", PeerName = "old-peer", CertificateSha256 = new string('B', 64), Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            await using (var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector)) await peer.SaveConfirmedAsync(oldPeer, oldPeer.CertificateSha256);
            runner = StartIsolatedV3RecoveryChild(hostRoot, peerRoot, pipeName);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var connected = pipe.WaitForConnectionAsync(deadline.Token); var exited = runner.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(connected, exited) == exited) throw new InvalidOperationException("Child exited before the READY barrier.");
            await connected;
            var signal = new byte[5]; await pipe.ReadExactlyAsync(signal, deadline.Token); Assert.Equal(2, signal[0]);
            var id = BinaryPrimitives.ReadInt32LittleEndian(signal.AsSpan(1));
            Assert.Throws<InvalidOperationException>(() => ValidateV3PipeClient(pipe, id + 1));
            ValidateV3PipeClient(pipe, id);
            testHost = GetVerifiedV3TestHost(runner, id);
            var verifiedStart = testHost.StartTime.ToUniversalTime();
            var verifiedExecutable = Path.GetFullPath(testHost.MainModule?.FileName ?? throw new InvalidOperationException("Missing test executable path."));
            var verifiedHandle = testHost.SafeHandle;
            if (verifiedHandle.IsInvalid || verifiedHandle.IsClosed) throw new InvalidOperationException("Invalid verified process handle.");
            Assert.Throws<InvalidOperationException>(() => RecheckVerifiedV3TestHost(runner, testHost, id, verifiedStart.AddTicks(1), verifiedExecutable));
            var files = new[] { Path.Combine(hostRoot, "multi-device-trust.bin"), Path.Combine(hostRoot, "pending-grant.bin"), Path.Combine(hostRoot, "pending-prepare-unknown.bin"), Path.Combine(peerRoot, "peer-invitation.bin"), Path.Combine(peerRoot, "peer-pending-invitation.bin") };
            var before = await Task.WhenAll(files.Select(path => ReadBoundedV3TestFileAsync(path, deadline.Token)));
            ValidateV3PipeClient(pipe, id);
            RecheckVerifiedV3TestHost(runner, testHost, id, verifiedStart, verifiedExecutable);
            testHost.Kill(); await testHost.WaitForExitAsync(deadline.Token);
            if (!runner.HasExited) runner.Kill();
            await runner.WaitForExitAsync(deadline.Token);
            for (var i = 0; i < files.Length; i++) Assert.Equal(before[i], await ReadBoundedV3TestFileAsync(files[i], deadline.Token));
            var hostCertPath = Path.Combine(hostRoot, "child-test-certs", "host.pem"); var peerCertPath = Path.Combine(hostRoot, "child-test-certs", "peer.pem");
            ValidateV3ArtifactPath(hostRoot, hostCertPath); ValidateV3ArtifactPath(hostRoot, peerCertPath);
            using var hostCert = PrivateFiles.LoadTlsCertificate(hostCertPath); using var peerCert = PrivateFiles.LoadTlsCertificate(peerCertPath);
            await using var reopenedHost = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var reopenedPeer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var now = DateTimeOffset.UtcNow; var h = await reopenedHost.CaptureOfflineRecoveryTestAsync(now); var p = await reopenedPeer.CaptureOfflineRecoveryTestAsync(now);
            Assert.NotNull(h); Assert.NotNull(p); Assert.True(h.MatchesCurrentPeer(p));
            var observation = await IsolatedRecoveryQuery.RunAsync(reopenedHost, reopenedPeer, hostCert, peerCert, now);
            Assert.NotNull(observation); Assert.True(observation.Matched);
            for (var i = 0; i < files.Length; i++) Assert.Equal(before[i], await ReadBoundedV3TestFileAsync(files[i], deadline.Token));
            Assert.True(reopenedHost.IsAuthorized("old-host-device", oldToken)); Assert.True(oldPeer == reopenedPeer.Get());
            var plain = protector.Unprotect(before[1]);
            try { using var json = JsonDocument.Parse(plain); Assert.False(reopenedHost.IsAuthorized(json.RootElement.GetProperty("identity").GetProperty("peerName").GetString()!, json.RootElement.GetProperty("token").GetString()!)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var wrongPath = Path.Combine(hostRoot, "child-test-certs", "wrong.pem"); ValidateV3ArtifactPath(hostRoot, wrongPath);
            await PrivateFiles.EnsureCertificateAsync(wrongPath, deadline.Token); using var wrongCert = PrivateFiles.LoadTlsCertificate(wrongPath);
            Assert.Null(await IsolatedRecoveryQuery.RunAsync(reopenedHost, reopenedPeer, wrongCert, peerCert, DateTimeOffset.UtcNow));
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IsolatedRecoveryQuery.RunAsync(reopenedHost, reopenedPeer, hostCert, peerCert, DateTimeOffset.UtcNow, ct: cancel.Token));
            }
            byte[] damaged = [1, 2, 3]; await File.WriteAllBytesAsync(files[2], damaged, deadline.Token);
            try
            {
                Assert.Null(await IsolatedRecoveryQuery.RunAsync(reopenedHost, reopenedPeer, hostCert, peerCert, DateTimeOffset.UtcNow));
                Assert.Equal(damaged, await ReadBoundedV3TestFileAsync(files[2], deadline.Token));
            }
            finally { await File.WriteAllBytesAsync(files[2], before[2], deadline.Token); }
            var backup = files[4] + ".test-backup"; ValidateV3ArtifactPath(peerRoot, backup);
            File.Move(files[4], backup, false);
            try { Assert.Null(await IsolatedRecoveryQuery.RunAsync(reopenedHost, reopenedPeer, hostCert, peerCert, DateTimeOffset.UtcNow)); }
            finally { File.Move(backup, files[4], false); }
            for (var i = 0; i < files.Length; i++) Assert.Equal(before[i], await ReadBoundedV3TestFileAsync(files[i], deadline.Token));
        }
        finally
        {
            pipe.Dispose();
            if (testHost is not null) { if (!testHost.HasExited) await testHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50)); testHost.Dispose(); }
            if (runner is not null) { if (!runner.HasExited) await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50)); runner.Dispose(); }
            CleanVault(hostRoot); CleanVault(peerRoot);
        }
    }

    [Fact]
    public async Task ParentDisconnectCancelsNeverReleaseBarrierWithoutSendingReady()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var pipeName = "nian-v3-recovery-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? runner = null, testHost = null;
        try
        {
            Directory.CreateDirectory(hostRoot); Directory.CreateDirectory(peerRoot);
            runner = StartIsolatedV3RecoveryChild(hostRoot, peerRoot, pipeName);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var connected = pipe.WaitForConnectionAsync(deadline.Token); var exited = runner.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(connected, exited) == exited) throw new InvalidOperationException("Child exited before the READY barrier.");
            await connected; var signal = new byte[5]; await pipe.ReadExactlyAsync(signal, deadline.Token); Assert.Equal(2, signal[0]);
            var id = BinaryPrimitives.ReadInt32LittleEndian(signal.AsSpan(1)); ValidateV3PipeClient(pipe, id);
            testHost = GetVerifiedV3TestHost(runner, id);
            Assert.True(File.Exists(Path.Combine(hostRoot, "pending-prepare-unknown.bin")));
            Assert.True(File.Exists(Path.Combine(peerRoot, "peer-pending-invitation.bin")));
            pipe.Dispose();
            await testHost.WaitForExitAsync(deadline.Token); await runner.WaitForExitAsync(deadline.Token);
            Assert.NotEqual(0, runner.ExitCode);
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, new WindowsCredentialProtector());
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, new WindowsCredentialProtector());
            var now = DateTimeOffset.UtcNow; var h = await host.CaptureOfflineRecoveryTestAsync(now); var p = await peer.CaptureOfflineRecoveryTestAsync(now);
            Assert.NotNull(h); Assert.NotNull(p); Assert.True(h.MatchesCurrentPeer(p));
            Assert.Empty(host.GetTrustedDevices()); Assert.Null(peer.Get());
        }
        finally
        {
            pipe.Dispose();
            if (testHost is not null) { if (!testHost.HasExited) await testHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50)); testHost.Dispose(); }
            if (runner is not null) { if (!runner.HasExited) await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50)); runner.Dispose(); }
            CleanVault(hostRoot); CleanVault(peerRoot);
        }
    }

    [Fact]
    public async Task NeverReleaseReadyBarrierEndsOnlyByCancellation()
    {
        var barrier = new NeverReleaseReadyBarrier(); using var cancel = new CancellationTokenSource();
        var waiting = barrier.PauseUntilCancelledAsync(cancel.Token); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(waiting.IsCompleted); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("early")]
    [InlineData("stall")]
    public async Task ChildWithoutReadyBarrierCannotYieldRecoverySuccess(string mode)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var pipeName = "nian-v3-recovery-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? runner = null;
        try
        {
            Directory.CreateDirectory(hostRoot); Directory.CreateDirectory(peerRoot);
            runner = StartIsolatedV3RecoveryChild(hostRoot, peerRoot, pipeName, mode);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (mode == "early")
            {
                var connected = pipe.WaitForConnectionAsync(deadline.Token); var exited = runner.WaitForExitAsync(deadline.Token);
                Assert.Same(exited, await Task.WhenAny(connected, exited)); await exited;
            }
            else
            {
                using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipe.WaitForConnectionAsync(shortWait.Token));
                await runner.WaitForExitAsync(deadline.Token);
            }
            Assert.NotEqual(0, runner.ExitCode);
            Assert.False(File.Exists(Path.Combine(hostRoot, "pending-prepare-unknown.bin")));
            Assert.False(File.Exists(Path.Combine(peerRoot, "peer-pending-invitation.bin")));
        }
        finally
        {
            pipe.Dispose();
            if (runner is not null) { if (!runner.HasExited) await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50)); runner.Dispose(); }
            CleanVault(hostRoot); CleanVault(peerRoot);
        }
    }

    private static async Task RunIsolatedV3ChildAsync()
    {
        var hostRoot = Environment.GetEnvironmentVariable("NIAN_V3_HOST_ROOT") ?? throw new InvalidOperationException();
        var peerRoot = Environment.GetEnvironmentVariable("NIAN_V3_PEER_ROOT") ?? throw new InvalidOperationException();
        var pipeName = Environment.GetEnvironmentVariable("NIAN_V3_PIPE") ?? throw new InvalidOperationException();
        ValidateChildRoot(hostRoot); ValidateChildRoot(peerRoot);
        if (string.Equals(hostRoot, peerRoot, StringComparison.OrdinalIgnoreCase) || !pipeName.StartsWith("nian-v3-recovery-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(pipeName["nian-v3-recovery-".Length..], "N", out _)) throw new InvalidOperationException("Invalid isolated child inputs.");
        ValidateV3ArtifactPath(hostRoot, Path.Combine(hostRoot, "child-test-certs")); ValidateV3ArtifactPath(peerRoot, Path.Combine(peerRoot, "peer-pending-invitation.bin"));
        var mode = Environment.GetEnvironmentVariable("NIAN_V3_TEST_MODE") ?? "normal";
        if (mode == "early") throw new InvalidOperationException("Synthetic child exit before READY.");
        using var watchdog = new CancellationTokenSource(mode == "stall" ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(40));
        if (mode == "stall") await Task.Delay(Timeout.Infinite, watchdog.Token);
        if (mode != "normal") throw new InvalidOperationException("Invalid isolated test mode.");
        await using var f = await Fixture.OpenAsync(certificateFolder: Path.Combine(hostRoot, "child-test-certs")); await f.BeginAsync(); await f.ConfirmAsync();
        await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, new WindowsCredentialProtector());
        await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, new WindowsCredentialProtector());
        var now = f.Clock(); var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
        await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
        var stop = new NeverReleaseReadyBarrier(); f.Peer.BeforeLiveReadyStop = stop;
        var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), watchdog.Token, writeV3: true);
        var send = host.SendPrepareAndReceiveReadyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock, watchdog.Token);
        try
        {
            var arrived = stop.Entered.WaitAsync(watchdog.Token);
            if (await Task.WhenAny(arrived, receive, send) != arrived) throw new InvalidOperationException("Prepare did not reach the READY barrier.");
            await arrived;
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(watchdog.Token);
            var signal = new byte[5]; signal[0] = 2; BinaryPrimitives.WriteInt32LittleEndian(signal.AsSpan(1), Environment.ProcessId);
            await pipe.WriteAsync(signal, watchdog.Token); await pipe.FlushAsync(watchdog.Token);
            var incoming = new byte[1];
            if (await pipe.ReadAsync(incoming, watchdog.Token) != 0) throw new InvalidDataException("Unexpected test control message.");
            throw new IOException("Test parent disconnected before terminating child.");
        }
        finally
        {
            watchdog.Cancel();
            try { await Task.WhenAll(receive, send).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException or TimeoutException) { }
        }
    }

    private static Process StartIsolatedV3RecoveryChild(string hostRoot, string peerRoot, string pipeName, string mode = "normal")
    {
        ValidateChildRoot(hostRoot); ValidateChildRoot(peerRoot);
        ValidateV3ArtifactPath(hostRoot, Path.Combine(hostRoot, "child-test-certs")); ValidateV3ArtifactPath(peerRoot, Path.Combine(peerRoot, "peer-pending-invitation.bin"));
        if (string.Equals(hostRoot, peerRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test roots must differ.");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tests", "ConditionalCommitIsolation.Tests", "ConditionalCommitIsolation.Tests.csproj"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Test project root unavailable.");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? @"D:\Projects\pc-toolbox\.tools\dotnet\dotnet.exe";
        var info = new ProcessStartInfo(dotnet) { WorkingDirectory = root.FullName, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "test", "tests/ConditionalCommitIsolation.Tests/ConditionalCommitIsolation.Tests.csproj", "-c", "Release", "--no-build", "--nologo", "--filter", "FullyQualifiedName~PublishedPeerV3SurvivesVerifiedChildTerminationBeforeReady", "--logger", "console;verbosity=quiet" }) info.ArgumentList.Add(arg);
        info.Environment["NIAN_PREPARE_V3_CHILD"] = "1"; info.Environment["NIAN_V3_HOST_ROOT"] = hostRoot; info.Environment["NIAN_V3_PEER_ROOT"] = peerRoot; info.Environment["NIAN_V3_PIPE"] = pipeName;
        info.Environment["NIAN_V3_TEST_MODE"] = mode;
        var child = Process.Start(info) ?? throw new InvalidOperationException("Unable to start isolated child.");
        _ = child.StandardOutput.ReadToEndAsync(); _ = child.StandardError.ReadToEndAsync(); return child;
    }

    private static void ValidateV3PipeClient(NamedPipeServerStream pipe, int expectedId)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var actual) || actual != expectedId)
            throw new InvalidOperationException("Unexpected isolated pipe client.");
    }

    private static Process GetVerifiedV3TestHost(Process runner, int id)
    {
        var candidate = Process.GetProcessById(id);
        try
        {
            var started = candidate.StartTime.ToUniversalTime();
            var exe = Path.GetFullPath(candidate.MainModule?.FileName ?? throw new InvalidOperationException("Missing test executable path."));
            RecheckVerifiedV3TestHost(runner, candidate, id, started, exe); return candidate;
        }
        catch { candidate.Dispose(); throw; }
    }

    private static void RecheckVerifiedV3TestHost(Process runner, Process testHost, int id, DateTime verifiedStart, string verifiedExecutable)
    {
        testHost.Refresh(); runner.Refresh();
        var exe = Path.GetFullPath(testHost.MainModule?.FileName ?? throw new InvalidOperationException("Missing test executable path."));
        var runnerExe = Path.GetFullPath(runner.MainModule?.FileName ?? throw new InvalidOperationException("Missing runner executable path."));
        var testExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "testhost.exe"));
        if (id != testHost.Id || id == Environment.ProcessId || testHost.HasExited || runner.HasExited || testHost.StartTime.ToUniversalTime() != verifiedStart ||
            !string.Equals(exe, verifiedExecutable, StringComparison.OrdinalIgnoreCase) ||
            testHost.StartTime.ToUniversalTime() < runner.StartTime.ToUniversalTime() || !IsTestProcessDescendant(id, runner.Id) ||
            !string.Equals(exe, runnerExe, StringComparison.OrdinalIgnoreCase) && !string.Equals(exe, testExe, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected isolated test process.");
    }

    private static async Task<byte[]> ReadBoundedV3TestFileAsync(string path, CancellationToken ct)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new InvalidOperationException(); ValidateV3ArtifactPath(root, path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var bytes = new byte[65537]; var count = 0;
        while (count < bytes.Length) { var n = await input.ReadAsync(bytes.AsMemory(count), ct); if (n == 0) break; count += n; }
        if (count is <= 0 or > 65536) throw new InvalidDataException("Invalid isolated test file length.");
        return bytes.AsSpan(0, count).ToArray();
    }

    private static void ValidateV3ArtifactPath(string root, string path)
    {
        ValidateChildRoot(root); var full = Path.GetFullPath(path); var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe isolated test artifact.");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Isolated test path cannot contain links."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
}
