using System.Net;
using System.Reflection;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace EmergencyReturn.Research;

// These tests exercise the current, unchanged manager and runtime. The first
// assertion is an intentionally retained RED for a proposed narrower policy.
public sealed class ReadParticipationTests
{
    [Fact]
    public async Task UnrelatedReadShouldNotTriggerAnotherDevicesExpiredReturn_Red()
    {
        var clock = new SyntheticClock();
        var keyboard = new SyntheticKeyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard, clock);
        Assert.True((await manager.BeginAsync("host", "one")).IsActive);
        clock.Advance(TimeSpan.FromSeconds(9));
        var before = keyboard.SwitchCalls;
        Assert.False((await manager.ReadForDeviceAsync("two")).HasSession);
        Assert.Equal("one", keyboard.Active); // RED: unrelated read handles the expired lease first.
        Assert.Equal(before, keyboard.SwitchCalls);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("one")]
    public async Task ParticipantReadReturnsExpiredPreviewToViewer(string reader)
    {
        var clock = new SyntheticClock();
        var keyboard = new SyntheticKeyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard, clock);
        Assert.True((await manager.BeginAsync("host", "one")).IsActive);
        var before = keyboard.SwitchCalls;
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False((await manager.ReadForDeviceAsync(reader)).HasSession);
        Assert.Equal("host", keyboard.Active);
        Assert.Equal(before + 1, keyboard.SwitchCalls);
    }

    [Fact]
    public async Task UnrelatedReadBeforeExpiryReturnsEmptyWithoutSwitch()
    {
        var clock = new SyntheticClock();
        var keyboard = new SyntheticKeyboard();
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard, clock);
        Assert.True((await manager.BeginAsync("host", "one")).IsActive);
        var before = keyboard.SwitchCalls;
        Assert.False((await manager.ReadForDeviceAsync("two")).HasSession);
        Assert.Equal("one", keyboard.Active);
        Assert.Equal(before, keyboard.SwitchCalls);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False((await manager.ReadAsync()).HasSession);
        Assert.Equal("host", keyboard.Active);
    }

    [Fact]
    public async Task CanceledUnrelatedReadWaitingBehindBeginDoesNotSwitch()
    {
        var keyboard = new SyntheticKeyboard { HoldPeerSwitch = true };
        await using var manager = new MultiPeerPreviewManager("host", ["one", "two"], keyboard);
        var begin = manager.BeginAsync("host", "one");
        try
        {
            await keyboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var cancel = new CancellationTokenSource();
            var read = manager.ReadForDeviceAsync("two", cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.Equal(1, keyboard.SwitchCalls);
        }
        finally { keyboard.Release.TrySetResult(); }
        Assert.True((await begin.WaitAsync(TimeSpan.FromSeconds(3))).IsActive);
        Assert.Equal("one", keyboard.Active);
    }

    [Fact]
    public async Task RunningRuntimeWatcherReturnsExpiredPreviewWithoutAnyPeerRead()
    {
        // Reject unsafe output before opening the vault or creating test keys.
        var output = RequireIsolatedOutput();
        var root = Path.Combine(output, "synthetic-runtime-watch", Guid.NewGuid().ToString("N"));
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(root, "vault"), new SyntheticProtector());
        await vault.AddConfirmedAsync("one", new string('A', 64));
        var keyboard = new SyntheticKeyboard();
        await using var runtime = new MultiPeerRuntime(vault, Path.Combine(root, "runtime"), "host", IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), CancellationToken.None);
        var preview = await runtime.BeginPreviewAsync("one", CancellationToken.None);
        Assert.True(preview.IsActive);
        Assert.Equal("one", keyboard.Active);
        // No control client, peer-read, manual End or synthetic clock advance is
        // made. Runtime owns its real watcher and the real eight-second lease.
        await keyboard.Returned.Task.WaitAsync(TimeSpan.FromSeconds(14));
        Assert.Equal("host", keyboard.Active);
        Assert.Equal(2, keyboard.SwitchCalls);
    }

    private static string RequireIsolatedOutput()
    {
        var work = typeof(ReadParticipationTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "NianExperimentWorkRoot")?.Value;
        var output = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(work) || !Path.IsPathFullyQualified(work) || !Path.IsPathFullyQualified(output))
            throw new InvalidOperationException("An isolated research output directory is required.");
        var root = Path.GetFullPath(work);
        var absolute = Path.GetFullPath(output);
        var relative = Path.GetRelativePath(root, absolute);
        if (relative == "." || Path.IsPathFullyQualified(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) || !Directory.Exists(absolute))
            throw new InvalidOperationException("The research output must be inside the isolated work directory.");
        for (var current = absolute; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked research output directories are not allowed.");
        return absolute;
    }

    private sealed class SyntheticClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        internal void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }

    private sealed class SyntheticProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }

    private sealed class SyntheticKeyboard : IMultiPeerKeyboard
    {
        private readonly object gate = new();
        private long revision = 1;
        private string active = "host";
        private int switches;
        internal bool HoldPeerSwitch;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Active { get { lock (gate) return active; } }
        internal int SwitchCalls { get { lock (gate) return switches; } }
        public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
        public Task<DeskLinkState> GetStateAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(new DeskLinkState { ProcessState=DeskLinkProcessState.Running,
                NativeControlAvailable=true, ActiveComputer=active, Revision=revision, ConnectedComputers=["one", "two"] });
        }
        public async Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long expectedRevision, CancellationToken ct)
        {
            lock (gate) switches++;
            if (target == "one" && HoldPeerSwitch) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            lock (gate)
            {
                if (active != expected || revision != expectedRevision) return new DeskLinkSwitchResult { Error="synthetic stale" };
                active=target; revision++;
                if (target == "host") Returned.TrySetResult();
                return new DeskLinkSwitchResult { Ok=true, InputTransferred=true, ActiveComputer=active, Target=target };
            }
        }
        public Task<DeskLinkState> StopAsync(CancellationToken ct) => GetStateAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
