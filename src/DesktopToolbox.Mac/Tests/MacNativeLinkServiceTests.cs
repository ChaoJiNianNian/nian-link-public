using System.Net;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.ViewModels;
using Nian.Input;
using Nian.Input.Control;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacNativeLinkServiceTests
{
    [Fact]
    public async Task LegacyPairingCannotGrantNativeControl()
    {
        await using var setup = await Setup.CreateAsync();
        var legacy = setup.Bundle with { Version = 1, InputInvitation = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.PreviewPairingAsync(legacy.ToJson()));
        Assert.Null(await setup.Service.GetApprovedPairingAsync());
        Assert.False(setup.Host.Armed);
    }

    [Fact]
    public async Task FinalApprovalRejectsInjectedOrUnhealthyLocalEventBeforePairRequest()
    {
        await using var setup = await Setup.CreateAsync();
        var preview = await setup.Service.PreviewPairingAsync(setup.Bundle.ToJson());
        Assert.Contains("双方", preview.DisplayDescription); Assert.Contains("预览", preview.DisplayDescription);
        setup.Host.ApprovalEvent = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.AcceptPairingAsync(preview.Id));
        setup.Host.ApprovalEvent = true; setup.Host.Healthy = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.AcceptPairingAsync(preview.Id));
        Assert.Equal(0, setup.PairRequests); Assert.Null(await setup.Service.GetApprovedPairingAsync());
    }

    [Fact]
    public async Task DeclinedWindowsApprovalDoesNotPersistPairingOrReverseTrust()
    {
        await using var setup = await Setup.CreateAsync(approve: false);
        var preview = await setup.Service.PreviewPairingAsync(setup.Bundle.ToJson());
        await Assert.ThrowsAnyAsync<Exception>(() => setup.Service.AcceptPairingAsync(preview.Id));
        Assert.Null(await setup.Service.GetApprovedPairingAsync()); Assert.False(setup.Host.Armed);
        await setup.Service.DisposeAsync();
        using var trust = await DeviceTrustStore.OpenAsync(Path.Combine(setup.MacPath, "native-input"));
        Assert.False(trust.Allows(setup.WindowsIdentity.Fingerprint, DevicePermission.Control));
        Assert.False(File.Exists(Path.Combine(setup.MacPath, "native-input", "paired-control.json")));
    }

    [Fact]
    public async Task LocalFingerprintIsPublishedWhileWindowsApprovalIsStillPending()
    {
        await using var setup = await Setup.CreateAsync();
        setup.ApprovalDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Service.Changed += snapshot => { if (snapshot.LocalFingerprint.Length != 0) published.TrySetResult(snapshot.LocalFingerprint); };
        var preview = await setup.Service.PreviewPairingAsync(setup.Bundle.ToJson());
        var accepting = setup.Service.AcceptPairingAsync(preview.Id);
        var actualFingerprint = await setup.PairingSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.True(published.Task.IsCompleted, "The Mac must expose its own fingerprint before Windows finishes approving it.");
            Assert.Equal(actualFingerprint, await published.Task);
        }
        finally { setup.ApprovalDecision.TrySetResult(true); await accepting; }
    }

    [Fact]
    public async Task PairingAdvertisesReceiverAndOnlyAuthenticatedBeginArmsShortLease()
    {
        await using var setup = await Setup.CreateAsync(); await setup.PairAndStartAsync();
        Assert.Equal(setup.Service.NativeReceiverPort, setup.AdvertisedReceiverPort);
        Assert.False(setup.Host.Armed); Assert.Equal(0, setup.Host.ArmCalls);
        await using var receiver = await setup.ConnectToMacAsync();
        var state = await receiver.GetStateAsync(); Assert.True(state.Available); Assert.False(state.InputActive);
        Assert.True((await receiver.BeginAsync()).Ok);
        Assert.True(setup.Host.Armed); Assert.Equal(TimeSpan.FromSeconds(2), setup.Host.LastLease);
        Assert.True((await receiver.HeartbeatAsync()).Ok); Assert.Equal(1, setup.Host.RenewCalls);
        setup.Host.Healthy = false;
        Assert.False((await receiver.HeartbeatAsync()).Ok); Assert.True(setup.Host.Releases > 0);
        Assert.Equal(0, setup.DisplayWrites); Assert.Equal(0, setup.FullSwitchRequests);
    }

    [Fact]
    public async Task RenewalCannotRearmRevokedNativeConsent()
    {
        await using var setup = await Setup.CreateAsync(); await setup.PairAndStartAsync();
        await using var receiver = await setup.ConnectToMacAsync(); Assert.True((await receiver.BeginAsync()).Ok);
        var arms = setup.Host.ArmCalls; setup.Host.SetArmed(false, null);
        Assert.False((await receiver.HeartbeatAsync()).Ok);
        Assert.Equal(arms, setup.Host.ArmCalls); Assert.False(setup.Host.Armed);
    }

    [Fact]
    public async Task NativeSharingUsesIndependentProfileAndDoesNotRequireEnginePath()
    {
        await using var setup = await Setup.CreateAsync();
        var old = Path.Combine(setup.MacPath, "paired-control.json"); Directory.CreateDirectory(setup.MacPath);
        await File.WriteAllTextAsync(old, "old-installed-profile-must-stay");
        await setup.PairAndStartAsync();
        var state = await setup.Service.RefreshAsync(); Assert.True(state.IsRunning); Assert.True(state.HasApprovedPairing);
        Assert.Equal(MacLinkBackend.Native, state.Backend);
        Assert.Equal("old-installed-profile-must-stay", await File.ReadAllTextAsync(old));
        Assert.True(File.Exists(Path.Combine(setup.MacPath, "native-input", "identity.json")));
        Assert.True(File.Exists(Path.Combine(setup.MacPath, "native-input", "trust.json")));
        Assert.Equal(0, setup.Host.ArmCalls);
    }

    [Fact]
    public async Task AuthenticatedWindowsStateKeepsSwitchButtonsUsableWhilePhysicalInputIsIdle()
    {
        await using var setup = await Setup.CreateAsync(); await setup.PairAndStartAsync();
        using var model = new MacLinkViewModel(setup.Service); await model.InitializeAsync();
        Assert.False(setup.Host.Armed); Assert.Equal("Win-PC", setup.WindowsState.ActiveComputer);
        Assert.True(model.CanSwitch); Assert.True(model.CanReturnToWindows); Assert.True(model.IsWindowsActive);
    }

    [Fact]
    public async Task ApprovedPeerAndLocalPermissionsSurviveRestartWithoutReusingExpiredInvitation()
    {
        await using var setup = await Setup.CreateAsync(); await setup.PairAndStartAsync();
        await setup.Service.DisposeAsync(); var requests = setup.PairRequests;
        setup.Service = setup.CreateService();
        var approved = await setup.Service.GetApprovedPairingAsync(); Assert.NotNull(approved);
        await setup.Service.StartAsync(Settings());
        await using var receiver = await setup.ConnectToMacAsync(); Assert.True((await receiver.BeginAsync()).Ok);
        Assert.Equal(requests, setup.PairRequests); Assert.Equal(0, setup.DisplayWrites);
    }

    [Fact]
    public async Task MacStateRpcNeverHandlesFullSwitchOrTriggersDisplayWrite()
    {
        await using var setup = await Setup.CreateAsync(); await setup.PairAndStartAsync();
        await using var receiver = await setup.ConnectToMacAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.FullSwitchAsync(new(1, DesktopSwitchTarget.Remote, 0)));
        Assert.Equal(0, setup.Host.ArmCalls); Assert.Equal(0, setup.DisplayWrites);
    }

    [Fact]
    public async Task PreviewChannelCanBeCreatedBeforeSharingAndReusedAfterEndingItsTransport()
    {
        await using var setup = await Setup.CreateAsync();
        var channel = setup.Service.CreatePreviewChannel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.BeginAsync(CancellationToken.None));
        await setup.PairAndStartAsync();
        setup.RegisterWindowsFrame(); await channel.BeginAsync(CancellationToken.None); await channel.EndAsync(CancellationToken.None);
        setup.RegisterWindowsFrame(); await channel.BeginAsync(CancellationToken.None); await channel.EndAsync(CancellationToken.None);
        Assert.Equal(2, setup.WindowsTarget.ArmCalls); Assert.Equal(2, setup.WindowsTarget.Releases);
        Assert.Equal(0, setup.Host.ArmCalls); Assert.Equal(0, setup.DisplayWrites);
    }

    [Fact]
    public async Task PreviewWaitsForWindowsPhysicalCaptureToStopWithoutAFullSwitch()
    {
        await using var setup = await Setup.CreateAsync(); await setup.PairAndStartAsync();
        await using var physical = await setup.ConnectToMacAsync(); Assert.True((await physical.BeginAsync()).Ok);
        setup.WindowsState = State("My-Mac", 1, 0, "Win-PC");
        setup.StateRequested = new(TaskCreationOptions.RunContinuationsAsynchronously); setup.RegisterWindowsFrame();
        var channel = setup.Service.CreatePreviewChannel(); var starting = channel.BeginAsync(CancellationToken.None);
        try
        {
            await Task.WhenAny(starting, setup.StateRequested.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(starting.IsCompleted, "Preview must wait for authenticated Windows capture restoration.");
            Assert.False(setup.Host.Armed); Assert.Equal(0, setup.WindowsTarget.ArmCalls);
            setup.WindowsState = State("Win-PC", 2, 0, "Win-PC"); setup.RegisterWindowsFrame();
            await starting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False((await physical.HeartbeatAsync()).Ok);
            Assert.Equal(0, setup.FullSwitchRequests); Assert.Equal(0, setup.DisplayWrites);
        }
        finally
        {
            setup.WindowsState = State("Win-PC", 2, 0, "Win-PC");
            try { await starting; } catch { }
            await channel.EndAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InvalidatingCapturedFramesImmediatelyReleasesPreviewButLeavesPhysicalInputAlone()
    {
        await using var setup = await Setup.CreateAsync(); await setup.PairAndStartAsync();
        var proof = setup.Service.BindCapturedFrame([1, 2, 3], setup.Host.ReadPreviewGeometry()); Assert.NotNull(proof);
        await using var receiver = await setup.ConnectToMacAsync(); Assert.True((await receiver.BeginPreviewAsync()).Ok);
        Assert.True((await receiver.SendPreviewAsync(new(1, proof.Id, proof.ContentSha256, PreviewInputKind.PointerDown, 0.5, 0.5))).Ok);
        setup.Service.InvalidateCapturedFrames(); Assert.False(setup.Host.Armed); Assert.True(setup.Host.Releases > 0);
        Assert.False((await receiver.HeartbeatAsync()).Ok);
        await using var physical = await setup.ConnectToMacAsync(); Assert.True((await physical.BeginAsync()).Ok);
        setup.Service.InvalidateCapturedFrames(); Assert.True(setup.Host.Armed);
        Assert.True((await physical.HeartbeatAsync()).Ok); Assert.Equal(0, setup.DisplayWrites);
    }

    [Fact]
    public async Task PreviewOwnershipChangesDoNotBecomeDisplayIntents()
    {
        await using var setup = await Setup.CreateAsync(); await setup.PairAndStartAsync();
        setup.WindowsState = State("My-Mac", 1, 1, "My-Mac"); await setup.Service.RefreshAsync();
        setup.WindowsState = State("Win-PC", 2, 1, "My-Mac"); await setup.Service.RefreshAsync();
        setup.WindowsState = State("My-Mac", 3, 1, "My-Mac"); await setup.Service.RefreshAsync();
        Assert.Equal(0, setup.DisplayWrites);
        setup.WindowsState = State("Win-PC", 4, 2, "Win-PC"); await setup.Service.RefreshAsync();
        Assert.Equal(1, setup.DisplayWrites); await setup.Service.RefreshAsync(); Assert.Equal(1, setup.DisplayWrites);
    }

    [Fact]
    public async Task ExplicitSwitchUsesWindowsRevisionAndConfirmedDisplayIntent()
    {
        await using var setup = await Setup.CreateAsync(); await setup.PairAndStartAsync();
        setup.WindowsState = State("My-Mac", 5, 7, "My-Mac"); await setup.Service.RefreshAsync();
        var outcome = await setup.Service.SwitchAsync("Win-PC");
        Assert.True(outcome.InputTransferred); Assert.Equal(DesktopSwitchTarget.Local, setup.LastSwitch!.Target);
        Assert.Equal(7, setup.LastSwitch.ExpectedDisplayIntentRevision); Assert.Equal(1, setup.DisplayWrites);
    }

    [Fact]
    public async Task ExplicitStopDisarmsBeforeWaitingForRpcThenRequestsWindowsReturn()
    {
        await using var setup = await Setup.CreateAsync(); await setup.PairAndStartAsync();
        setup.WindowsState = State("My-Mac", 1, 1, "My-Mac");
        await using var receiver = await setup.ConnectToMacAsync(); Assert.True((await receiver.BeginAsync()).Ok);
        setup.StateDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.StateRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = setup.Service.RefreshAsync(); await setup.StateRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = setup.Service.StopAsync();
        try { Assert.False(setup.Host.Armed); Assert.False(stopping.IsCompleted); }
        finally { setup.StateDecision.TrySetResult(true); await refreshing; await stopping; }
        Assert.False((await setup.Service.RefreshAsync()).IsRunning);
        Assert.Equal(1, setup.FullSwitchRequests); Assert.Equal(1, setup.DisplayWrites);
        Assert.Equal(DesktopSwitchTarget.Local, setup.LastSwitch!.Target);
    }

    private static MacConnectionSettings Settings() => new() { WindowsHost = "127.0.0.1", WindowsName = "Win-PC", MacName = "My-Mac", EnginePath = "/does-not-exist/unused-core" };
    private static DesktopControlState State(string active, long input, long intent, string display) => new(true, "Win-PC", "My-Mac", active, active == "My-Mac", input, intent, display);

    private sealed class FakeHost : IMacNativeDesktopHost, IPreviewInputTarget
    {
        public bool Healthy = true, ApprovalEvent = true, Armed;
        public int ArmCalls, RenewCalls, Releases; public TimeSpan? LastLease; public long Consent;
        public bool IsGuardHealthy => Healthy;
        public bool IsCurrentEventEligibleForLocalApproval() => ApprovalEvent;
        public IPreviewInputTarget Target => this;
        public bool IsReady => Armed; public long ConsentVersion => Consent; public string UnavailableReason => "Local target is not armed.";
        public PreviewSourceGeometry? ReadPreviewGeometry() => new("mac-display-1", "revision-1", 0, 0, 1000, 700, 1, 1);
        public void SetArmed(bool value, TimeSpan? lease) { if (value) { ArmCalls++; LastLease = lease; } if (Armed != value) Consent++; Armed = value; }
        public bool RenewLease(long expected, TimeSpan lease) { if (!Armed || expected != Consent) return false; RenewCalls++; LastLease = lease; return true; }
        public void RefreshSafety() { }
        public void Apply(InputPacket packet) { if (!Armed) throw new InvalidOperationException(); }
        public void ApplyPreview(ResolvedPreviewInput input, long expected) { if (!Armed || expected != Consent) throw new InvalidOperationException(); }
        public void ReleaseAll() => Interlocked.Increment(ref Releases);
    }

    private sealed class MemoryVault : IIdentityVault
    {
        private readonly Dictionary<string, byte[]> items = [];
        public Task<byte[]?> LoadAsync(string key, CancellationToken ct = default) => Task.FromResult(items.TryGetValue(key, out var value) ? value.ToArray() : null);
        public Task StoreAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default) { items.Add(key, value.ToArray()); return Task.CompletedTask; }
    }

    private sealed class Setup : IAsyncDisposable
    {
        private readonly string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../..", ".artifacts/native-integration/test-profiles/mac-native-" + Guid.NewGuid().ToString("N")));
        private readonly MemoryVault vault = new();
        private DeviceTrustStore windowsTrust = null!; private DesktopInputServer windowsServer = null!;
        public DeviceIdentity WindowsIdentity = null!; public MacNativeLinkService Service = null!; public readonly FakeHost Host = new();
        public readonly FakeHost WindowsTarget = new(); private PreviewFrameAuthority windowsFrames = null!;
        public DeskLinkPairingBundle Bundle = null!; public int PairRequests, DisplayWrites, FullSwitchRequests; public int? AdvertisedReceiverPort;
        public TaskCompletionSource<bool>? ApprovalDecision;
        public readonly TaskCompletionSource<string> PairingSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>? StateDecision;
        public TaskCompletionSource StateRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DesktopControlState WindowsState = State("Win-PC", 0, 0, "Win-PC"); public DesktopFullSwitchRequest? LastSwitch;
        public string MacPath => Path.Combine(path, "mac");
        public static async Task<Setup> CreateAsync(bool approve = true)
        {
            var setup = new Setup(); var win = Path.Combine(setup.path, "windows");
            setup.WindowsIdentity = await DeviceIdentity.OpenAsync(win, "Win-PC", setup.vault); setup.windowsTrust = await DeviceTrustStore.OpenAsync(win);
            setup.windowsFrames = new(setup.WindowsTarget.ReadPreviewGeometry);
            setup.windowsServer = new(setup.WindowsIdentity, setup.windowsTrust, IPAddress.Loopback, 0, setup.WindowsTarget, (request, _) =>
            {
                setup.PairRequests++; setup.AdvertisedReceiverPort = request.ReceiverPort; setup.PairingSeen.TrySetResult(request.Fingerprint);
                return setup.ApprovalDecision?.Task ?? Task.FromResult(approve);
            }, options: new()
            {
                PreviewFrames = setup.windowsFrames,
                PrepareInput = (_, _) => { setup.WindowsTarget.SetArmed(true, TimeSpan.FromSeconds(2)); return true; },
                RenewInput = (_, _, consent) => setup.WindowsTarget.RenewLease(consent, TimeSpan.FromSeconds(2)),
                ReadStateAsync = async (_, token) =>
                {
                    setup.StateRequested.TrySetResult();
                    if (setup.StateDecision is { } decision) await decision.Task.WaitAsync(token);
                    return setup.WindowsState;
                },
                FullSwitchAsync = (_, request, _) =>
                {
                    setup.FullSwitchRequests++; setup.LastSwitch = request;
                    var target = request.Target == DesktopSwitchTarget.Local ? "Win-PC" : "My-Mac";
                    setup.WindowsState = State(target, setup.WindowsState.InputRevision + 1, setup.WindowsState.DisplayIntentRevision + 1, target);
                    return Task.FromResult(new DesktopSwitchResult(true, setup.WindowsState, true, "disabled"));
                }
            });
            await setup.windowsServer.StartAsync(); var invitation = setup.windowsServer.CreateInvitation(DevicePermission.Control | DevicePermission.PreviewControl);
            setup.Bundle = new() { Version = 2, Host = "127.0.0.1", Port = 24801, CertificateSha256 = setup.WindowsIdentity.Fingerprint,
                Token = Convert.ToBase64String(new byte[32]), WindowsName = "Win-PC", MacName = "My-Mac", InputInvitation = invitation.ToCode(),
                Display = new() { Enabled = true, VendorId = 1, ProductId = 2, WindowsInput = 3, MacInput = 4 } };
            setup.Service = setup.CreateService(); return setup;
        }
        public MacNativeLinkService CreateService() => new(MacPath, Host, new MacNativeLinkOptions
        {
            BindAddress = IPAddress.Loopback, ReceiverPort = 0, IdentityVault = vault,
            SwitchDisplayAsync = (_, _) => { Interlocked.Increment(ref DisplayWrites); return Task.FromResult(new MacLocalDisplaySwitchResult(true, "acceptedUnverified", null)); }
        });
        public async Task PairAndStartAsync()
        {
            var preview = await Service.PreviewPairingAsync(Bundle.ToJson()); await Service.AcceptPairingAsync(preview.Id); await Service.StartAsync(Settings());
        }
        public void RegisterWindowsFrame() => windowsFrames.Register([1, 2, 3], WindowsTarget.ReadPreviewGeometry()!);
        public async Task<DesktopInputClient> ConnectToMacAsync()
        {
            var state = await Service.RefreshAsync();
            var client = new DesktopInputClient(WindowsIdentity, new("127.0.0.1", Service.NativeReceiverPort!.Value, state.LocalFingerprint, "My-Mac"));
            await client.ConnectAsync(); return client;
        }
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync(); await windowsServer.DisposeAsync(); windowsTrust.Dispose(); WindowsIdentity.Dispose();
            // Isolated identity/trust evidence is retained; credentials are never treated as build garbage.
        }
    }
}
