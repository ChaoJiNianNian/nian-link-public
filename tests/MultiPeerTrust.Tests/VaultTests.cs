using System.Security.Cryptography;
using System.IO;
using System.Text;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.DeskLink.Coordination;
using DesktopToolbox.DeskLink.Preview;
using DesktopToolbox.App.DeskLink;
using NianLink.Windows;

namespace MultiPeerTrust.Tests;

public sealed class VaultTests
{
    [Fact]
    public async Task AlreadyCanceledManualStopDoesNotSuppressLaterNetworkCleanup()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new FakeKeyboard(vault.GetTrustedDevices());
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var report = runtime.CaptureNetworkLoss();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.StopAsync(cancel.Token));
        await report().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HostNetworkLossStatus.AwaitingNetworkApproval, runtime.NetworkLossStatus);
        Assert.True(keyboard.Stopped);
    }

    [Fact]
    public async Task ManualStopCanceledWhileWaitingDoesNotDropNetworkCleanup()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new BlockingReadKeyboard();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var report = runtime.CaptureNetworkLoss();
        var read = runtime.GetKeyboardStateAsync(default);
        Task? stop = null; Task? network = null;
        using var cancel = new CancellationTokenSource();
        try
        {
            await keyboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop = runtime.StopAsync(cancel.Token);
            network = report();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        }
        finally { keyboard.Release.TrySetResult(); }
        await read.WaitAsync(TimeSpan.FromSeconds(5));
        await network!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HostNetworkLossStatus.AwaitingNetworkApproval, runtime.NetworkLossStatus);
        Assert.True(keyboard.Stopped);
    }

    private sealed class BlockingReadKeyboard : IMultiPeerKeyboard
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped;
        public Task<DeskLinkState> StartAsync(CancellationToken ct) => Task.FromResult(State());
        public async Task<DeskLinkState> GetStateAsync(CancellationToken ct)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return State(); }
        public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long expectedRevision, CancellationToken ct) =>
            Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target });
        public Task<DeskLinkState> StopAsync(CancellationToken ct) { Stopped = true; return Task.FromResult(State()); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static DeskLinkState State() => new() { ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true, ActiveComputer = "host", Revision = 1 };
    }

    [Fact]
    public async Task NetworkLossReportsStoppingUntilRealCleanupCompletesAndPreservesTrust()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var token = await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new BlockingStopKeyboard();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var report = runtime.CaptureNetworkLoss();
        var cleanup = report();
        try
        {
            await keyboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HostNetworkLossStatus.Stopping, runtime.NetworkLossStatus);
        }
        finally { keyboard.Release.TrySetResult(); }
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HostNetworkLossStatus.AwaitingNetworkApproval, runtime.NetworkLossStatus);
        Assert.Empty(runtime.Devices);
        Assert.True(vault.IsAuthorized("one", token));
        Assert.Equal(1, keyboard.Stops);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync(vault.GetTrustedDevices(), default));
        Assert.Equal(HostNetworkLossStatus.AwaitingNetworkApproval, runtime.NetworkLossStatus);
    }

    [Fact]
    public async Task NetworkLossRetainsUnconfirmedCleanupAfterRepeatedManualStop()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new StatefulKeyboard();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        keyboard.ExternalFocus("one"); keyboard.FailTarget = "host";
        await runtime.CaptureNetworkLoss()().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HostNetworkLossStatus.CleanupUnconfirmed, runtime.NetworkLossStatus);
        await runtime.StopAsync(default);
        Assert.Equal(HostNetworkLossStatus.CleanupUnconfirmed, runtime.NetworkLossStatus);
        keyboard.FailTarget = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync(vault.GetTrustedDevices(), default));
        Assert.Equal(HostNetworkLossStatus.CleanupUnconfirmed, runtime.NetworkLossStatus);
    }

    [Fact]
    public async Task OldNetworkLossCallbackCannotStopANewRuntimeSession()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        var created = new List<FakeKeyboard>();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, peers => { var result = new FakeKeyboard(peers); created.Add(result); return result; });
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var oldReport = runtime.CaptureNetworkLoss();
        await runtime.StopAsync(default);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        await oldReport().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(created[1].Stopped);
        Assert.Single(runtime.Devices);
        Assert.Equal(HostNetworkLossStatus.None, runtime.NetworkLossStatus);
    }

    [Fact]
    public async Task UserStopWinsAgainstDelayedNetworkCallback()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new BlockingStopKeyboard();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var report = runtime.CaptureNetworkLoss();
        var manualStop = runtime.StopAsync(default);
        Task delayedReport;
        try
        {
            await keyboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            delayedReport = report();
        }
        finally { keyboard.Release.TrySetResult(); }
        await manualStop.WaitAsync(TimeSpan.FromSeconds(5));
        await delayedReport.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HostNetworkLossStatus.None, runtime.NetworkLossStatus);
        Assert.Equal(1, keyboard.Stops);
    }

    [Fact]
    public async Task FailedManualStopPreventsDelayedNetworkCallbackFromRetryingCleanup()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new BlockingStopKeyboard { FailStop = true };
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var report = runtime.CaptureNetworkLoss();
        var manualStop = runtime.StopAsync(default);
        Task delayedReport;
        try
        {
            await keyboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            delayedReport = report();
        }
        finally { keyboard.Release.TrySetResult(); }
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => manualStop);
            await delayedReport.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HostNetworkLossStatus.CleanupUnconfirmed, runtime.NetworkLossStatus);
            Assert.Equal(1, keyboard.Stops);
        }
        finally { keyboard.FailStop = false; await runtime.StopAsync(default); }
    }

    [Fact]
    public async Task DuplicateNetworkLossCannotOverwriteUnconfirmedPreviewCleanup()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new StatefulKeyboard();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var active = await runtime.BeginPreviewAsync("one", default);
        keyboard.FailTarget = "host";
        var report = runtime.CaptureNetworkLoss();
        try
        {
            await report().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HostNetworkLossStatus.CleanupUnconfirmed, runtime.NetworkLossStatus);
            keyboard.FailTarget = null;
            await report().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HostNetworkLossStatus.CleanupUnconfirmed, runtime.NetworkLossStatus);
            Assert.True((await runtime.ReadPreviewAsync(default)).HasSession);
        }
        finally
        {
            keyboard.FailTarget = null;
            await runtime.EndPreviewAsync(active.Id!, default);
            await runtime.StopAsync(default);
        }
    }

    [Fact]
    public async Task FailedManualPreviewReturnIsVisibleWhenNetworkLossArrives()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new StatefulKeyboard();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var report = runtime.CaptureNetworkLoss();
        var active = await runtime.BeginPreviewAsync("one", default);
        keyboard.FailTarget = "host";
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StopAsync(default));
            await report().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HostNetworkLossStatus.CleanupUnconfirmed, runtime.NetworkLossStatus);
            Assert.NotEqual(0, runtime.ControlPort);
            Assert.True((await runtime.ReadPreviewAsync(default)).HasSession);
        }
        finally
        {
            keyboard.FailTarget = null;
            await runtime.EndPreviewAsync(active.Id!, default);
            await runtime.StopAsync(default);
        }
    }

    private sealed class BlockingStopKeyboard : IMultiPeerKeyboard
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops;
        public bool FailStop { get; set; }
        public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
        public Task<DeskLinkState> GetStateAsync(CancellationToken ct) => Task.FromResult(new DeskLinkState
            { ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true, ActiveComputer = "host", Revision = 1 });
        public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long expectedRevision, CancellationToken ct) =>
            Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target });
        public async Task<DeskLinkState> StopAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Stops); Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
            if (FailStop) throw new InvalidOperationException("synthetic stop failure");
            return new DeskLinkState { ProcessState = DeskLinkProcessState.Stopped };
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ExistingProtectedSettingsWithoutScreenTopologyStillLoad()
    {
        using var folder = new Fixture();
        var key = new string('A', 64);
        var legacy = "{\"version\":1,\"devices\":{\"" + key + "\":{\"clipboardEnabled\":true}}}";
        var protector = new WindowsCredentialProtector();
        await File.WriteAllBytesAsync(System.IO.Path.Combine(folder.Path, "device-settings.bin"), protector.Protect(Encoding.UTF8.GetBytes(legacy)));
        await using var store = await MultiPeerSettingsStore.OpenAsync(folder.Path, protector);
        Assert.True(store.Get(key).ClipboardEnabled);
        Assert.Null(store.GetScreenConfiguration());
    }

    [Fact]
    public async Task MultiScreenSettingsSurviveReloadButNotTheLossOfTheirDeviceGrant()
    {
        using var folder = new Fixture();
        var macKey = new string('A', 64); var unrelatedKey = new string('B', 64);
        var config = new MultiScreenConfiguration("desktop", [new("mac", macKey)],
            [new("shared", false, [new("desktop", "display-a", 15), new("mac", null, 16)])]);
        await using (var store = await MultiPeerSettingsStore.OpenAsync(folder.Path, new WindowsCredentialProtector()))
        {
            await store.SetScreenConfigurationAsync(config);
            await store.SetAsync(unrelatedKey, new MultiPeerFileSettings(ClipboardEnabled: true));
        }
        await using var reopened = await MultiPeerSettingsStore.OpenAsync(folder.Path, new WindowsCredentialProtector());
        Assert.Equal("shared", Assert.Single(reopened.GetScreenConfiguration()!.Screens).ScreenId);
        await reopened.RetainAsync(new HashSet<string> { macKey });
        Assert.NotNull(reopened.GetScreenConfiguration());
        await reopened.RetainAsync(new HashSet<string>());
        Assert.Null(reopened.GetScreenConfiguration());
    }

    [Fact]
    public async Task MultiScreenSettingsAreAtomicAndRevokedDevicesCannotInheritThem()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("laptop", new string('1', 64)); await vault.AddConfirmedAsync("mac", new string('2', 64));
        var protector = new FaultyProtector();
        await using var settings = await MultiPeerSettingsStore.OpenAsync(System.IO.Path.Combine(folder.Path, "settings"), protector);
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "desktop",
            System.Net.IPAddress.Loopback, 0, 0, peers => new FakeKeyboard(peers), settings);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        MultiScreenDefinition[] screens = [new("left", false, [new("desktop", "left-local", 15), new("mac", null, 16)]), new("portable", true, [new("laptop", null, null)])];
        await runtime.SetScreenTopologyAsync(screens, default);
        screens[0] = new("changed", true, [new("laptop", null, null)]);
        var view = (await runtime.GetScreenTopologyAsync(default))!;
        Assert.Contains(view.Screens, screen => screen.ScreenId == "left");
        Assert.DoesNotContain("grantKey", System.Text.Json.JsonSerializer.Serialize(view), StringComparison.OrdinalIgnoreCase);
        protector.Fail = true;
        await Assert.ThrowsAsync<CryptographicException>(() => runtime.SetScreenTopologyAsync([new("new-screen", true, [new("laptop", null, null)])], default));
        protector.Fail = false;
        Assert.Contains((await runtime.GetScreenTopologyAsync(default))!.Screens, screen => screen.ScreenId == "left");
        await runtime.StopAsync(default);
        await vault.RevokeAsync("mac"); await vault.AddConfirmedAsync("mac", new string('3', 64));
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        Assert.Null(await runtime.GetScreenTopologyAsync(default));
    }

    [Fact]
    public async Task MonitorGroupSavesAllDevicesTogetherAndRetainsOldValuesOnFailure()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64)); await vault.AddConfirmedAsync("two", new string('2', 64));
        var protector = new FaultyProtector();
        await using var settings = await MultiPeerSettingsStore.OpenAsync(System.IO.Path.Combine(folder.Path, "settings"), protector);
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, peers => new FakeKeyboard(peers), settings);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var initial = new Dictionary<string, MultiPeerMonitorSettings> { ["one"] = new("screen", 15, 16), ["two"] = new("screen", 15, 17) };
        await runtime.SetMonitorGroupAsync(initial, default);
        var changed = new Dictionary<string, MultiPeerMonitorSettings> { ["one"] = new("screen", 15, 18), ["two"] = new("screen", 15, 19) };
        protector.Fail = true;
        await Assert.ThrowsAsync<CryptographicException>(() => runtime.SetMonitorGroupAsync(changed, default));
        protector.Fail = false;
        Assert.Equal(initial["one"], await runtime.GetDisplayPreferenceAsync("one", default));
        Assert.Equal(initial["two"], await runtime.GetDisplayPreferenceAsync("two", default));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.SetMonitorGroupAsync(new Dictionary<string, MultiPeerMonitorSettings>
        { ["one"] = new("screen", 15, 16), ["two"] = new("screen", 15, 16) }, default));
        Assert.Equal(initial["two"], await runtime.GetDisplayPreferenceAsync("two", default));
        await runtime.SetMonitorGroupAsync(new Dictionary<string, MultiPeerMonitorSettings> { ["one"] = initial["one"] }, default);
        Assert.Equal(initial["one"], await runtime.GetDisplayPreferenceAsync("one", default));
        Assert.Null(await runtime.GetDisplayPreferenceAsync("two", default));
    }

    [Fact]
    public void ClipboardPausesWhileSetupAndMiniSelectionsDisagree()
    {
        Assert.Null(MultiPeerClipboardTarget.ReconcileSelection("one", true, "two"));
        Assert.Null(MultiPeerClipboardTarget.ReconcileSelection("one", true, null));
        Assert.Equal("one", MultiPeerClipboardTarget.ReconcileSelection("one", true, "one"));
        Assert.Equal("one", MultiPeerClipboardTarget.ReconcileSelection("one", false, null));
    }

    [Fact]
    public async Task MiniKeepsPreviewControlAvailableWhenTheWindowTakesThePreparedSession()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var token = await vault.AddConfirmedAsync("one", new string('1', 64));
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => new StatefulKeyboard());
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = runtime.ControlPort,
            DataPort = runtime.Devices.Single().DataPort, HostName = "host", PeerName = "one",
            CertificateSha256 = runtime.CertificateSha256!, Token = token };
        await using var client = MultiPeerCollaborationClient.Open(invitation, true);
        await client.SetPreviewSourceAsync(new PreviewSourceStatus(Guid.NewGuid(), 1, "enabled"));
        await client.GetStatusAsync(supportsLivePreview: true, supportsPassivePreviewGeneration: true);
        await using var mini = new MultiDeviceMiniService(runtime, vault, "host");
        await mini.SelectDeviceAsync("one"); mini.SetPreviewVisible(true); await mini.SetPreviewEnabledAsync(true);
        Assert.True(mini.PreviewControlReady);
        await using var session = mini.CreateDeskflowPreviewSession();
        Assert.NotNull(session);
        Assert.True(session.Supported);
        Assert.True(mini.PreviewControlReady);
        await client.SetPreviewSourceAsync(new PreviewSourceStatus(Guid.NewGuid(), 1, "locked"));
        await mini.RefreshAsync();
        Assert.False(mini.PreviewControlReady);
    }

    [Fact]
    public void MiniLabelsChangePresentationButPreserveRoutingIdentity()
    {
        var devices = new[] { new MiniDevice("one", "Mac · one"), new MiniDevice("two", "Windows · two") };
        var shown = MiniDeviceLabels.Apply(devices, name => name == "one" ? "工作 Mac" : name);
        Assert.Equal("one", shown[0].Name);
        Assert.Equal("Mac · 工作 Mac", shown[0].Label);
        Assert.Equal("Windows · two", shown[1].Label);
        Assert.Equal("Mac · one", devices[0].Label);
    }

    [Fact]
    public async Task MiniFileSendRemainsBoundToTheDisplayedRecipientAfterAnotherSelection()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var oneToken = await vault.AddConfirmedAsync("one", new string('1', 64));
        var twoToken = await vault.AddConfirmedAsync("two", new string('2', 64));
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, peers => new FakeKeyboard(peers));
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        await using var mini = new MultiDeviceMiniService(runtime, vault, "host");
        await mini.SelectDeviceAsync("one");
        await mini.SelectDeviceAsync("two");
        var file = System.IO.Path.Combine(folder.Path, "message.txt"); await File.WriteAllTextAsync(file, "fixture");
        await mini.SendFilesToDeviceAsync("one", [file]);
        await mini.SelectDeviceAsync("one"); await mini.RefreshAsync();
        Assert.Single(mini.Snapshot.Transfers);
        await mini.SelectDeviceAsync("two"); await mini.RefreshAsync();
        Assert.Empty(mini.Snapshot.Transfers);
        var shared = System.IO.Path.Combine(folder.Path, "shared"); Directory.CreateDirectory(shared);
        await File.WriteAllTextAsync(System.IO.Path.Combine(shared, "visible.txt"), "hello");
        await mini.SetSharedDirectoryAsync(shared);
        var roots = await mini.ListSharedAsync();
        Assert.Single(roots.Items);
        var files = await mini.ListSharedAsync("shared");
        Assert.Equal("visible.txt", Assert.Single(files.Items).Name);
        MultiPeerInvitation Invite(string peer, string token) => new() { Host = "127.0.0.1", Port = runtime.ControlPort,
            DataPort = runtime.Devices.Single(x => x.Name == peer).DataPort, HostName = "host", PeerName = peer,
            CertificateSha256 = runtime.CertificateSha256!, Token = token };
        await using var one = MultiPeerCollaborationClient.Open(Invite("one", oneToken), true);
        await using var two = MultiPeerCollaborationClient.Open(Invite("two", twoToken), true);
        Assert.Single((await one.GetStatusAsync()).PendingTransfers);
        Assert.Empty((await two.GetStatusAsync()).PendingTransfers);
        await Assert.ThrowsAsync<ArgumentException>(() => mini.SelectDeviceAsync("unknown"));
    }

    [Fact]
    public async Task MiniCannotChangeDevicesUntilTheActivePreviewIsReturned()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        await vault.AddConfirmedAsync("two", new string('2', 64));
        var keyboard = new StatefulKeyboard();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        await using var mini = new MultiDeviceMiniService(runtime, vault, "host");
        await mini.SelectDeviceAsync("one");
        var preview = await runtime.BeginPreviewAsync("one", default);
        Assert.True(preview.IsActive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mini.SelectDeviceAsync("two"));
        Assert.Equal("one", mini.Snapshot.SelectedDevice);
        await runtime.EndPreviewAsync(preview.Id!, default);
        await mini.SelectDeviceAsync("two");
        Assert.Equal("two", mini.Snapshot.SelectedDevice);
    }

    [Fact]
    public async Task MiniPreviewRequestsOnlyTheSelectedDeviceAndStopsThePreviousOne()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var first = await vault.AddConfirmedAsync("one", new string('1', 64));
        var second = await vault.AddConfirmedAsync("two", new string('2', 64));
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, peers => new FakeKeyboard(peers));
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        await using var mini = new MultiDeviceMiniService(runtime, vault, "host");
        MultiPeerInvitation Invite(string peer, string token) => new() { Host = "127.0.0.1", Port = runtime.ControlPort,
            DataPort = runtime.Devices.Single(x => x.Name == peer).DataPort, HostName = "host", PeerName = peer,
            CertificateSha256 = runtime.CertificateSha256!, Token = token };
        await using var one = MultiPeerCollaborationClient.Open(Invite("one", first), true);
        await using var two = MultiPeerCollaborationClient.Open(Invite("two", second), true);
        await mini.SelectDeviceAsync("one"); mini.SetPreviewVisible(true); await mini.SetPreviewEnabledAsync(true);
        Assert.True((await one.GetStatusAsync()).CaptureRequested);
        Assert.False((await two.GetStatusAsync()).CaptureRequested);
        await mini.SelectDeviceAsync("two");
        Assert.False((await one.GetStatusAsync()).CaptureRequested);
        await mini.SetPreviewEnabledAsync(true);
        Assert.True((await two.GetStatusAsync()).CaptureRequested);
    }

    [Fact]
    public void ExistingDeviceSettingsWithoutPreviewFieldsKeepTheThirtySecondDefault()
    {
        var previous = System.Text.Json.JsonSerializer.Deserialize<MultiPeerFileSettings>("{}", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(previous);
        Assert.Equal(30, previous.PreviewRefreshSeconds);
        Assert.Null(previous.Monitor);
    }

    [Fact]
    public async Task EncryptedControlKeepsPreviewOwnedByOnePeerAndReturnsToItsViewer()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var oneToken = await vault.AddConfirmedAsync("one", new string('1', 64));
        var twoToken = await vault.AddConfirmedAsync("two", new string('2', 64));
        var keyboard = new StatefulKeyboard();
        await using var preview = new MultiPeerPreviewManager("host", ["one", "two"], keyboard);
        await using var server = new MultiPeerControlServer(System.IO.Path.Combine(folder.Path, "control"),
            System.Net.IPAddress.Loopback, 0, "host", vault, keyboard.GetStateAsync, keyboard.SwitchIfCurrentAsync, preview);
        await server.StartAsync();
        MultiPeerInvitation Invitation(string peer, string token) => new() { Host = "127.0.0.1", Port = server.Port,
            HostName = "host", PeerName = peer, CertificateSha256 = server.CertificateSha256!, Token = token };
        await using var one = new MultiPeerControlClient(Invitation("one", oneToken), true);
        await using var two = new MultiPeerControlClient(Invitation("two", twoToken), true);
        Assert.True(await one.SwitchAsync("one"));
        var active = await one.BeginPreviewAsync();
        Assert.True(active.IsActive);
        Assert.Equal("host", (await keyboard.GetStateAsync(default)).ActiveComputer);
        Assert.False((await two.ReadPreviewAsync()).HasSession);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => two.EndPreviewAsync(active.Id!));
        Assert.False(await two.SwitchAsync("two"));
        Assert.True((await one.RenewPreviewAsync(active.Id!)).IsActive);
        var returned = await one.EndPreviewAsync(active.Id!);
        Assert.False(returned.HasSession);
        Assert.Equal("one", (await keyboard.GetStateAsync(default)).ActiveComputer);
    }

    [Fact]
    public async Task PeerCanDistinguishKeyboardHandoffFromUnverifiedDisplayChange()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var token = await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new StatefulKeyboard();
        await using var preview = new MultiPeerPreviewManager("host", ["one", "two"], keyboard, display: new UnverifiedDisplay());
        await using var server = new MultiPeerControlServer(System.IO.Path.Combine(folder.Path, "control"),
            System.Net.IPAddress.Loopback, 0, "host", vault, keyboard.GetStateAsync, keyboard.SwitchIfCurrentAsync, preview);
        await server.StartAsync();
        await using var client = new MultiPeerControlClient(new MultiPeerInvitation { Host = "127.0.0.1", Port = server.Port,
            HostName = "host", PeerName = "one", CertificateSha256 = server.CertificateSha256!, Token = token }, true);
        var result = await client.SwitchDetailedAsync("one");
        Assert.True(result.InputTransferred);
        Assert.Equal("acceptedUnverified", result.VideoStatus);
        Assert.Equal("one", result.ActiveComputer);
        Assert.DoesNotContain("private-device-details", result.Error ?? "");
    }

    [Fact]
    public async Task AChangedFocusDoesNotRevealAnotherPeerToTheRequester()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var token = await vault.AddConfirmedAsync("one", new string('1', 64));
        await vault.AddConfirmedAsync("two", new string('2', 64));
        var keyboard = new StatefulKeyboard { NextExternalFocus = "two" };
        await using var preview = new MultiPeerPreviewManager("host", ["one", "two"], keyboard);
        await using var server = new MultiPeerControlServer(System.IO.Path.Combine(folder.Path, "control"),
            System.Net.IPAddress.Loopback, 0, "host", vault, keyboard.GetStateAsync, keyboard.SwitchIfCurrentAsync, preview);
        await server.StartAsync();
        await using var client = new MultiPeerControlClient(new MultiPeerInvitation { Host = "127.0.0.1", Port = server.Port,
            HostName = "host", PeerName = "one", CertificateSha256 = server.CertificateSha256!, Token = token }, true);
        var result = await client.SwitchDetailedAsync("one");
        Assert.False(result.InputTransferred);
        Assert.Null(result.ActiveComputer);
    }

    [Fact]
    public async Task RuntimeRoutesNativeFocusEventsThroughTheDisplayGuard()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new StatefulKeyboard(); var display = new RecordingDisplay();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard, displayControl: display);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        keyboard.ExternalFocus("one");
        Assert.Equal("one", await display.Seen.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task RuntimeRetainsControlChannelIfPreviewReturnCannotBeConfirmed()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new StatefulKeyboard();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var active = await runtime.BeginPreviewAsync("one", default);
        Assert.True(active.IsActive);
        Assert.Equal(active.Id, (await runtime.ReadPreviewAsync(default)).Id);
        var port = runtime.ControlPort;
        keyboard.FailTarget = "host";
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StopAsync(default));
        Assert.Equal(port, runtime.ControlPort);
        Assert.Equal("one", (await runtime.GetKeyboardStateAsync(default)).ActiveComputer);
        keyboard.FailTarget = null;
        Assert.False((await runtime.EndPreviewAsync(active.Id!, default)).HasSession);
        await runtime.StopAsync(default);
    }

    [Fact]
    public async Task LiveCaptureRequiresTheSelectedPeersEnabledSourceAndStopsWhenLocked()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var token = await vault.AddConfirmedAsync("one", new string('1', 64));
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, peers => new FakeKeyboard(peers));
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var invite = new MultiPeerInvitation { Host = "127.0.0.1", Port = runtime.ControlPort, DataPort = runtime.Devices.Single().DataPort,
            HostName = "host", PeerName = "one", CertificateSha256 = runtime.CertificateSha256!, Token = token };
        await using var client = MultiPeerCollaborationClient.Open(invite, true);
        var provider = new SyntheticLiveCapture();
        await runtime.SetPreviewSourceAsync("one", new(Guid.NewGuid(), 1, "enabled"), default, provider);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var gotFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = client.LivePreview.ReceiveAsync(Guid.NewGuid(), new(), (_, packet) =>
        { if (packet.Kind == LivePreviewCaptureKind.HealthyIdle) gotFrame.TrySetResult(); }, timeout.Token);
        await gotFrame.Task.WaitAsync(timeout.Token);
        await runtime.SetPreviewSourceAsync("one", new(Guid.NewGuid(), 1, "locked"), default, provider);
        try { await receiver.WaitAsync(timeout.Token); } catch (IOException) { } catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
        await Assert.ThrowsAnyAsync<Exception>(() => client.LivePreview.ReceiveAsync(Guid.NewGuid(), new(), (_, _) => { }, timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task HostWaitsForLiveFrameBeforeHandoffAndReturnsKeyboardWhenStopped()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var token = await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new StatefulKeyboard();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var invite = new MultiPeerInvitation { Host = "127.0.0.1", Port = runtime.ControlPort, DataPort = runtime.Devices.Single().DataPort,
            HostName = "host", PeerName = "one", CertificateSha256 = runtime.CertificateSha256!, Token = token };
        await using var client = MultiPeerCollaborationClient.Open(invite, true);
        var source = new PreviewSourceStatus(Guid.NewGuid(), 1, "enabled");
        await client.SetPreviewSourceAsync(source);
        await client.GetStatusAsync(supportsLivePreview: true, supportsPassivePreviewGeneration: true);
        await using var session = await runtime.CreateHostPreviewSessionAsync("one", default);
        Assert.True(session.Supported);
        session.LivePacketReceived += (id, packet) => session.NotifyLiveFramePresented(id, packet);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var enabling = session.EnableAsync(timeout.Token);
        LivePreviewDemand? demand = null;
        while (demand is null)
        {
            demand = (await client.GetStatusAsync(timeout.Token, supportsLivePreview: true, supportsPassivePreviewGeneration: true)).RequestedLivePreview;
            if (demand is null) await Task.Delay(10, timeout.Token);
        }
        Assert.Equal("host", (await keyboard.GetStateAsync(default)).ActiveComputer);
        using var uploadStop = new CancellationTokenSource();
        var upload = client.LivePreview.UploadAsync(demand, new SyntheticImageCapture(SyntheticFrame()), uploadStop.Token);
        await enabling.WaitAsync(timeout.Token);
        Assert.True(session.IsActive);
        Assert.Equal("one", (await keyboard.GetStateAsync(default)).ActiveComputer);
        await session.StopAsync("已停止操作，仅观看画面");
        Assert.Equal("host", (await keyboard.GetStateAsync(default)).ActiveComputer);
        uploadStop.Cancel();
        try { await upload; } catch (IOException) { } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task RemoteViewerGetsLiveHostFrameBeforeTakingControlAndReturnsToItself()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var token = await vault.AddConfirmedAsync("one", new string('1', 64));
        var keyboard = new StatefulKeyboard();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => keyboard);
        await runtime.StartAsync(vault.GetTrustedDevices(), default);
        var invite = new MultiPeerInvitation { Host = "127.0.0.1", Port = runtime.ControlPort, DataPort = runtime.Devices.Single().DataPort,
            HostName = "host", PeerName = "one", CertificateSha256 = runtime.CertificateSha256!, Token = token };
        await using var data = MultiPeerCollaborationClient.Open(invite, true);
        await using var control = new MultiPeerControlClient(invite, true);
        await runtime.SetPreviewSourceAsync("one", new(Guid.NewGuid(), 1, "enabled"), default, new SyntheticImageCapture(SyntheticFrame()));
        await data.GetStatusAsync(supportsLivePreview: true, supportsPassivePreviewGeneration: true);
        Assert.True((await runtime.SwitchKeyboardAsync("one", default)).InputTransferred);
        await using var session = new DeskflowPreviewSession(new RemoteControlAdapter(control),
            new RemoteSubscriptionControlLivePreviewSource(() => data, () => true));
        session.LivePacketReceived += (id, packet) => session.NotifyLiveFramePresented(id, packet);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await session.EnableAsync(timeout.Token);
        Assert.True(session.IsActive);
        Assert.Equal("host", (await keyboard.GetStateAsync(default)).ActiveComputer);
        await session.StopAsync("已停止操作，仅观看画面");
        Assert.Equal("one", (await keyboard.GetStateAsync(default)).ActiveComputer);
    }

    [Fact]
    public async Task ApprovedPeersSurviveRestartAndAreIndividuallyRevocable()
    {
        using var folder = new Fixture();
        var protect = new WindowsCredentialProtector();
        string first, second;
        await using (var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protect))
        {
            first = await vault.AddConfirmedAsync("Win-Two", new string('1', 64));
            second = await vault.AddConfirmedAsync("Mac-Two", new string('2', 64));
            Assert.True(vault.IsAuthorized("Win-Two", first));
            Assert.False(vault.IsAuthorized("Mac-Two", first));
            var trusted = vault.GetTrustedDevices();
            Assert.Equal(2, trusted.Count);
            Assert.Contains(trusted, x => x.Name == "Win-Two" && x.Fingerprint == new string('1', 64));
            Assert.DoesNotContain(first, System.Text.Json.JsonSerializer.Serialize(trusted));
            await Assert.ThrowsAsync<IOException>(() => MultiDeviceCredentialVault.OpenAsync(folder.Path, protect));
        }
        var bytes = await File.ReadAllBytesAsync(System.IO.Path.Combine(folder.Path, "multi-device-trust.bin"));
        Assert.DoesNotContain(first, Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain(second, Encoding.UTF8.GetString(bytes));
        await using (var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protect))
        {
            Assert.True(vault.IsAuthorized("Win-Two", first));
            Assert.True(vault.IsAuthorized("Mac-Two", second));
            Assert.True(await vault.RevokeAsync("Win-Two"));
        }
        await using var reopened = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protect);
        Assert.False(reopened.IsAuthorized("Win-Two", first));
        Assert.True(reopened.IsAuthorized("Mac-Two", second));
    }

    [Fact]
    public async Task TamperedVaultFailsClosedAndIsNotReplaced()
    {
        using var folder = new Fixture();
        var protect = new WindowsCredentialProtector();
        await using (var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protect))
            await vault.AddConfirmedAsync("Win-Two", new string('1', 64));
        var path = System.IO.Path.Combine(folder.Path, "multi-device-trust.bin");
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0x40;
        await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => MultiDeviceCredentialVault.OpenAsync(folder.Path, protect));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task DuplicateNamesFingerprintsAndFifthDeviceAreRejected()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.AddConfirmedAsync("ONE", new string('2', 64)));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.AddConfirmedAsync("two", new string('1', 64)));
        await vault.AddConfirmedAsync("two", new string('2', 64));
        await vault.AddConfirmedAsync("three", new string('3', 64));
        await vault.AddConfirmedAsync("four", new string('4', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.AddConfirmedAsync("five", new string('5', 64)));
        Assert.Equal(new[] { 1, 2, 3, 4 }, vault.GetTrustedDevices().Select(x => x.Slot).Order().ToArray());
        await vault.RevokeAsync("two");
        await vault.AddConfirmedAsync("five", new string('5', 64));
        Assert.Equal(2, vault.GetTrustedDevices().Single(x => x.Name == "five").Slot);
    }

    [Fact]
    public async Task FailedPersistenceDoesNotGrantNewDeviceOrDamageExistingTrust()
    {
        using var folder = new Fixture();
        var protect = new FaultyProtector();
        string first;
        await using (var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protect))
        {
            first = await vault.AddConfirmedAsync("one", new string('1', 64));
            protect.Fail = true;
            await Assert.ThrowsAsync<CryptographicException>(() => vault.AddConfirmedAsync("two", new string('2', 64)));
            Assert.True(vault.IsAuthorized("one", first));
            protect.Fail = false;
            await vault.AddConfirmedAsync("two", new string('2', 64));
        }
        await using var reopened = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protect);
        Assert.True(reopened.IsAuthorized("one", first));
    }

    [Fact]
    public async Task NetworkRevocationIsPersistedAndDoesNotRevokeOtherDevice()
    {
        using var folder = new Fixture();
        var protector = new WindowsCredentialProtector();
        string first, second;
        await using (var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protector))
        {
            first = await vault.AddConfirmedAsync("one", new string('1', 64));
            second = await vault.AddConfirmedAsync("two", new string('2', 64));
            await using var server = new MultiPeerControlServer(System.IO.Path.Combine(folder.Path, "endpoint"),
                System.Net.IPAddress.Loopback, 0, "host", vault,
                _ => Task.FromResult(new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
                    NativeControlAvailable = true, ActiveComputer = "host", ConnectedComputers = ["one", "two"] }),
                (target, owner, revision, _) => Task.FromResult(new DeskLinkSwitchResult
                    { Ok = true, ActiveComputer = target, InputTransferred = true }));
            await server.StartAsync();
            var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = server.Port, HostName = "host",
                PeerName = "one", CertificateSha256 = server.CertificateSha256!, Token = first };
            await using var client = new MultiPeerControlClient(invitation, true);
            Assert.True(await client.SwitchAsync("one"));
            Assert.True(await server.RevokeAsync("one"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SwitchAsync("one"));
        }
        await using var reopened = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protector);
        Assert.False(reopened.IsAuthorized("one", first));
        Assert.True(reopened.IsAuthorized("two", second));
    }

    [Fact]
    public async Task DeviceDataEndpointsKeepFilesAndClipboardSeparateAndRejectRevokedTokens()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var first = await vault.AddConfirmedAsync("one", new string('1', 64));
        var second = await vault.AddConfirmedAsync("two", new string('2', 64));
        var certRoot = System.IO.Path.Combine(folder.Path, "certificate");
        await using var control = new PairedControlServer(new() { ProfileDirectory = certRoot, Port = 0, AllowedTargets = ["host", "one"] },
            _ => Task.FromResult(new DeskLinkState()), (_, _) => Task.FromResult(new DeskLinkSwitchResult()));
        await control.StartAsync();
        var receiveOne = System.IO.Path.Combine(folder.Path, "receive-one");
        var receiveTwo = System.IO.Path.Combine(folder.Path, "receive-two");
        var shareOne = System.IO.Path.Combine(folder.Path, "share-one");
        var shareTwo = System.IO.Path.Combine(folder.Path, "share-two");
        Directory.CreateDirectory(shareOne);
        Directory.CreateDirectory(shareTwo);
        await File.WriteAllTextAsync(System.IO.Path.Combine(shareOne, "one.txt"), "one only");
        await File.WriteAllTextAsync(System.IO.Path.Combine(shareTwo, "two.txt"), "two only");
        await using var dataOne = MultiPeerCollaborationEndpoint.Create(vault, "one",
            System.IO.Path.Combine(folder.Path, "data"), System.IO.Path.Combine(certRoot, "control.pem"),
            System.Net.IPAddress.Loopback, 0, receiveOne, [new SharedDirectory("one-share", "one", shareOne)]);
        await using var dataTwo = MultiPeerCollaborationEndpoint.Create(vault, "two",
            System.IO.Path.Combine(folder.Path, "data"), System.IO.Path.Combine(certRoot, "control.pem"),
            System.Net.IPAddress.Loopback, 0, receiveTwo, [new SharedDirectory("two-share", "two", shareTwo)]);
        await dataOne.StartAsync();
        await dataTwo.StartAsync();
        DeskLinkPairingBundle Bundle(string peer, string token) => new()
        {
            Host = "127.0.0.1", Port = control.Port, WindowsName = "host", MacName = peer,
            CertificateSha256 = control.CertificateSha256!, Token = token
        };
        var inviteOne = new MultiPeerInvitation { Host = "127.0.0.1", Port = control.Port, DataPort = dataOne.Port,
            HostName = "host", PeerName = "one", CertificateSha256 = control.CertificateSha256!, Token = first };
        var inviteTwo = inviteOne with { PeerName = "two", Token = second, DataPort = dataTwo.Port };
        Assert.Throws<InvalidOperationException>(() => MultiPeerCollaborationClient.Open(inviteOne, false));
        await using var clientOne = MultiPeerCollaborationClient.Open(inviteOne, true);
        await using var clientTwo = MultiPeerCollaborationClient.Open(inviteTwo, true);
        await using var wrongPort = new CollaborationClient(Bundle("one", first), true, dataTwo.Port);
        var source = System.IO.Path.Combine(folder.Path, "fixture.txt");
        await File.WriteAllTextAsync(source, "synthetic test");
        var receipt = await clientOne.SendFilesAsync([source]);
        Assert.Equal("synthetic test", await File.ReadAllTextAsync(Assert.Single(receipt.SavedPaths)));
        Assert.False(Directory.Exists(receiveTwo) && Directory.EnumerateFiles(receiveTwo).Any());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrongPort.GetStatusAsync());
        await dataOne.QueueFilesAsync([source]);
        Assert.Single((await clientOne.GetStatusAsync()).PendingTransfers);
        Assert.Empty((await clientTwo.GetStatusAsync()).PendingTransfers);
        var pulled = await clientOne.ReceivePendingAsync(System.IO.Path.Combine(folder.Path, "peer-one-receive"));
        Assert.Equal("synthetic test", await File.ReadAllTextAsync(Assert.Single(Assert.Single(pulled).SavedPaths)));
        Assert.Equal("one.txt", Assert.Single((await clientOne.ListSharedAsync("one-share")).Items).Name);
        Assert.Equal("two.txt", Assert.Single((await clientTwo.ListSharedAsync("two-share")).Items).Name);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => clientTwo.ListSharedAsync("one-share"));
        dataOne.SetPreviewRequested(true);
        await clientOne.PublishPreviewAsync(SyntheticFrame());
        Assert.Equal("live", dataOne.GetRemotePreview().State);
        Assert.NotEqual("live", dataTwo.GetRemotePreview().State);
        dataOne.SetLocalClipboardEnabled(true);
        dataTwo.SetLocalClipboardEnabled(true);
        await clientOne.SetClipboardEnabledAsync(true);
        await clientTwo.SetClipboardEnabledAsync(true);
        await clientOne.PublishClipboardAsync(new(Guid.NewGuid().ToString("N"), "one", DateTimeOffset.UtcNow, "private test"));
        Assert.Equal("private test", dataOne.GetRemoteClipboard()?.Text);
        Assert.Null(dataTwo.GetRemoteClipboard());
        await vault.RevokeAsync("one");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => clientOne.GetStatusAsync());
        Assert.NotNull(await clientTwo.GetStatusAsync());
    }

    [Fact]
    public async Task RepairedDeviceCannotAccessOldTransferQueueOrOldEndpoint()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var first = await vault.AddConfirmedAsync("one", new string('1', 64));
        var root = System.IO.Path.Combine(folder.Path, "runtime");
        await using var runtime = new MultiPeerRuntime(vault, root, "host", System.Net.IPAddress.Loopback, 0, 0, peers => new FakeKeyboard(peers));
        var fingerprint = await runtime.PrepareControlIdentityAsync(CancellationToken.None);
        var cert = System.IO.Path.Combine(root, "control", "multi-control.pem");
        var dataRoot = System.IO.Path.Combine(folder.Path, "data");
        await using var oldEndpoint = MultiPeerCollaborationEndpoint.Create(vault, "one", dataRoot, cert, System.Net.IPAddress.Loopback, 0);
        await oldEndpoint.StartAsync();
        var original = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24801, DataPort = oldEndpoint.Port,
            HostName = "host", PeerName = "one", CertificateSha256 = fingerprint, Token = first };
        var source = System.IO.Path.Combine(folder.Path, "old.txt");
        await File.WriteAllTextAsync(source, "old queued content");
        await oldEndpoint.QueueFilesAsync([source]);
        await using var oldClient = MultiPeerCollaborationClient.Open(original, true);
        Assert.Single((await oldClient.GetStatusAsync()).PendingTransfers);
        await vault.RevokeAsync("one");
        var second = await vault.AddConfirmedAsync("one", new string('1', 64));
        await using var newTokenOnOldEndpoint = MultiPeerCollaborationClient.Open(original with { Token = second }, true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => newTokenOnOldEndpoint.GetStatusAsync());
        await oldEndpoint.DisposeAsync();
        await using var newEndpoint = MultiPeerCollaborationEndpoint.Create(vault, "one", dataRoot, cert, System.Net.IPAddress.Loopback, 0);
        await newEndpoint.StartAsync();
        await using var newClient = MultiPeerCollaborationClient.Open(original with { Token = second, DataPort = newEndpoint.Port }, true);
        Assert.Empty((await newClient.GetStatusAsync()).PendingTransfers);
    }

    [Fact]
    public async Task PairingRequiresMatchingReviewedFingerprintAndConsumesOfferOnce()
    {
        using var folder = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var offer = await PeerIdentityOffer.CreateAsync(System.IO.Path.Combine(folder.Path, "peer"), "Win-Two", "windows", now);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('A', 64));
        var preview = pairing.Review(offer.ToJson(now), now);
        Assert.Empty(vault.GetTrustedDevices());
        await Assert.ThrowsAsync<InvalidOperationException>(() => pairing.ConfirmAsync(preview.Id, new string('F', 64), now));
        Assert.Empty(vault.GetTrustedDevices());
        var invite = await pairing.ConfirmAsync(preview.Id, offer.KeyboardFingerprint, now);
        Assert.Equal("Win-Two", invite.PeerName);
        Assert.True(vault.IsAuthorized("Win-Two", invite.Token));
        Assert.Equal("windows", Assert.Single(vault.GetTrustedDevices()).Platform);
        Assert.Equal(24802, invite.DataPort);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pairing.ConfirmAsync(preview.Id, offer.KeyboardFingerprint, now));
        var other = await PeerIdentityOffer.CreateAsync(System.IO.Path.Combine(folder.Path, "peer-two"), "Mac-Two", "mac", now);
        var otherPreview = pairing.Review(other.ToJson(now), now);
        var otherInvite = await pairing.ConfirmAsync(otherPreview.Id, other.KeyboardFingerprint, now);
        Assert.Equal(24803, otherInvite.DataPort);
        Assert.NotEqual(invite.Token, otherInvite.Token);
        Assert.Equal(2, vault.GetTrustedDevices().Count);
    }

    [Fact]
    public async Task ExpiredOfferCannotBeConfirmedAndDoesNotCreateTrust()
    {
        using var folder = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var offer = await PeerIdentityOffer.CreateAsync(System.IO.Path.Combine(folder.Path, "peer"), "Win-Two", "windows", now);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('A', 64));
        var preview = pairing.Review(offer.ToJson(now), now);
        await Assert.ThrowsAsync<ArgumentException>(() => pairing.ConfirmAsync(preview.Id, offer.KeyboardFingerprint, now.AddMinutes(11)));
        Assert.Empty(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task AddingDeviceRestartsRuntimeBeforeReturningInvitation()
    {
        using var folder = new Fixture();
        var now = DateTimeOffset.UtcNow;
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("old", new string('1', 64));
        var offer = await PeerIdentityOffer.CreateAsync(System.IO.Path.Combine(folder.Path, "peer"), "new", "windows", now);
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('A', 64));
        var preview = pairing.Review(offer.ToJson(now), now);
        var runtime = new FakeRuntime();
        var change = new MultiPeerTrustChangeCoordinator(vault, runtime);
        var invitation = await change.AddAsync(pairing, preview.Id, offer.KeyboardFingerprint, now);
        Assert.Equal(new[] { "stop", "start:old,new" }, runtime.Events);
        Assert.True(vault.IsAuthorized("new", invitation.Token));
    }

    [Fact]
    public async Task CompletedFilelessPairingRebuildsRuntimeWithoutChangingTrust()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var old = await vault.AddConfirmedWithSlotAsync("old", new string('1', 64), "windows");
        var added = await vault.AddConfirmedWithSlotAsync("new", new string('2', 64), "mac");
        var expected = vault.GetTrustedDevices().Single(x => x.Name == "new");
        var runtime = new FakeRuntime();
        var change = new MultiPeerTrustChangeCoordinator(vault, runtime);

        await change.RebuildAfterFilelessPairingAsync(expected);

        Assert.Equal(new[] { "stop", "start:old,new" }, runtime.Events);
        Assert.True(vault.IsAuthorized("old", old.Token));
        Assert.True(vault.IsAuthorized("new", added.Token));
    }

    [Fact]
    public async Task FilelessRuntimeRebuildRejectsDifferentIdentityBeforeStopping()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedWithSlotAsync("new", new string('2', 64), "mac");
        var actual = vault.GetTrustedDevices().Single();
        var runtime = new FakeRuntime();
        var change = new MultiPeerTrustChangeCoordinator(vault, runtime);

        await Assert.ThrowsAsync<InvalidOperationException>(() => change.RebuildAfterFilelessPairingAsync(actual with { Fingerprint = new string('3', 64) }));
        Assert.Empty(runtime.Events);
        Assert.Single(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task FilelessRuntimeRestartFailurePreservesCompletedPairing()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var added = await vault.AddConfirmedWithSlotAsync("new", new string('2', 64), "mac");
        var expected = vault.GetTrustedDevices().Single();
        var runtime = new FakeRuntime { FailStart = true };
        var change = new MultiPeerTrustChangeCoordinator(vault, runtime);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => change.RebuildAfterFilelessPairingAsync(expected));
        Assert.Contains("paired", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(vault.IsAuthorized("new", added.Token));
        Assert.Single(vault.GetTrustedDevices());
        Assert.Equal(new[] { "stop", "start:new" }, runtime.Events);
    }

    [Fact]
    public async Task CancelledFilelessRuntimeRebuildDoesNotInterruptCurrentSession()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedWithSlotAsync("new", new string('2', 64), "mac");
        var runtime = new FakeRuntime();
        var change = new MultiPeerTrustChangeCoordinator(vault, runtime);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change.RebuildAfterFilelessPairingAsync(vault.GetTrustedDevices().Single(), cancellation.Token));
        Assert.Empty(runtime.Events);
    }

    [Fact]
    public async Task FilelessRuntimeRebuildFinishesRestartAfterStopEvenIfCallerCancels()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedWithSlotAsync("new", new string('2', 64), "mac");
        using var cancellation = new CancellationTokenSource();
        var runtime = new CancelOnStopRuntime(cancellation);
        var change = new MultiPeerTrustChangeCoordinator(vault, runtime);

        await change.RebuildAfterFilelessPairingAsync(vault.GetTrustedDevices().Single(), cancellation.Token);
        Assert.True(runtime.Started);
        Assert.False(runtime.StartTokenCanBeCancelled);
    }

    [Fact]
    public async Task ApprovedDeviceInvitationCanBeRecoveredWithoutIssuingNewTrust()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var grant = await vault.AddConfirmedWithSlotAsync("client", new string('1', 64), "windows");
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('A', 64));
        var change = new MultiPeerTrustChangeCoordinator(vault, new FakeRuntime());
        var recovered = await change.ExportInvitationAsync(pairing, "client");
        Assert.Equal(grant.Token, recovered.Token);
        Assert.Equal(24802 + grant.Slot - 1, recovered.DataPort);
        Assert.Single(vault.GetTrustedDevices());
        await change.RevokeAsync("client");
        await Assert.ThrowsAsync<InvalidOperationException>(() => change.ExportInvitationAsync(pairing, "client"));
    }

    [Fact]
    public async Task FailedNewDeviceStartupRevokesNewCredentialAndRestoresOldRuntime()
    {
        using var folder = new Fixture();
        var now = DateTimeOffset.UtcNow;
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var oldToken = await vault.AddConfirmedAsync("old", new string('1', 64));
        var offer = await PeerIdentityOffer.CreateAsync(System.IO.Path.Combine(folder.Path, "peer"), "new", "windows", now);
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('A', 64));
        var preview = pairing.Review(offer.ToJson(now), now);
        var runtime = new FakeRuntime { FailStartsRemaining = 1 };
        var change = new MultiPeerTrustChangeCoordinator(vault, runtime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => change.AddAsync(pairing, preview.Id, offer.KeyboardFingerprint, now));
        Assert.Equal(new[] { "stop", "start:old,new", "stop", "start:old" }, runtime.Events);
        Assert.Single(vault.GetTrustedDevices());
        Assert.True(vault.IsAuthorized("old", oldToken));
    }

    [Fact]
    public async Task AddingWithWrongFingerprintDoesNotInterruptExistingSession()
    {
        using var folder = new Fixture();
        var now = DateTimeOffset.UtcNow;
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("old", new string('1', 64));
        var offer = await PeerIdentityOffer.CreateAsync(System.IO.Path.Combine(folder.Path, "peer"), "new", "windows", now);
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('A', 64));
        var preview = pairing.Review(offer.ToJson(now), now);
        var runtime = new FakeRuntime();
        var change = new MultiPeerTrustChangeCoordinator(vault, runtime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => change.AddAsync(pairing, preview.Id, new string('F', 64), now));
        Assert.Empty(runtime.Events);
        Assert.Single(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task OfferExpiringWhileStoppingDoesNotGrantTrustAndRestoresOldSession()
    {
        using var folder = new Fixture();
        var now = DateTimeOffset.UtcNow;
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("old", new string('1', 64));
        var offer = await PeerIdentityOffer.CreateAsync(System.IO.Path.Combine(folder.Path, "peer"), "new", "windows", now);
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('A', 64));
        var preview = pairing.Review(offer.ToJson(now), now);
        var runtime = new FakeRuntime();
        var clock = new FixedClock(now.AddMinutes(11));
        var change = new MultiPeerTrustChangeCoordinator(vault, runtime, clock);
        await Assert.ThrowsAsync<ArgumentException>(() => change.AddAsync(pairing, preview.Id, offer.KeyboardFingerprint, now));
        Assert.Equal(new[] { "stop", "start:old" }, runtime.Events);
        Assert.Single(vault.GetTrustedDevices());
    }

    [Fact]
    public async Task PeerInvitationIsStoredProtectedOnlyAfterFingerprintApproval()
    {
        using var folder = new Fixture();
        var invitation = new MultiPeerInvitation { Host = "192.168.42.8", Port = 24801, DataPort = 24802,
            KeyboardPort = 24800, HostName = "host", PeerName = "client", CertificateSha256 = new string('A', 64),
            Token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
        var protector = new WindowsCredentialProtector();
        await using (var store = await PeerInvitationStore.OpenAsync(folder.Path, protector))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveConfirmedAsync(invitation, new string('B', 64)));
            Assert.Null(store.Get());
            await store.SaveConfirmedAsync(invitation, invitation.CertificateSha256);
            Assert.Equal(invitation, store.Get());
        }
        var bytes = await File.ReadAllBytesAsync(System.IO.Path.Combine(folder.Path, "peer-invitation.bin"));
        Assert.DoesNotContain(invitation.Token, System.Text.Encoding.UTF8.GetString(bytes));
        await using var reopened = await PeerInvitationStore.OpenAsync(folder.Path, protector);
        Assert.Equal(invitation, reopened.Get());
    }

    [Fact]
    public async Task InvalidProtectedInvitationIsRetainedAndDoesNotSilentlyReset()
    {
        using var folder = new Fixture();
        var path = System.IO.Path.Combine(folder.Path, "peer-invitation.bin");
        byte[] corrupt = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(path, corrupt);
        await Assert.ThrowsAnyAsync<Exception>(() => PeerInvitationStore.OpenAsync(folder.Path, new WindowsCredentialProtector()));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task FailedInvitationReplacementKeepsPreviousApprovedDevice()
    {
        using var folder = new Fixture();
        var protector = new FaultyProtector();
        var original = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24801, DataPort = 24802,
            KeyboardPort = 24800, HostName = "host", PeerName = "client", CertificateSha256 = new string('A', 64),
            Token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
        await using (var store = await PeerInvitationStore.OpenAsync(folder.Path, protector))
        {
            await store.SaveConfirmedAsync(original, original.CertificateSha256);
            protector.Fail = true;
            var replacement = original with { HostName = "another-host" };
            await Assert.ThrowsAsync<CryptographicException>(() => store.SaveConfirmedAsync(replacement, replacement.CertificateSha256));
            Assert.Equal(original, store.Get());
            await Assert.ThrowsAsync<IOException>(() => PeerInvitationStore.OpenAsync(folder.Path, new WindowsCredentialProtector()));
        }
        protector.Fail = false;
        await using var reopened = await PeerInvitationStore.OpenAsync(folder.Path, protector);
        Assert.Equal(original, reopened.Get());
    }

    [Fact]
    public async Task TrustChangesSerializeEvenAcrossCoordinatorInstances()
    {
        using var folder = new Fixture();
        var now = DateTimeOffset.UtcNow;
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("old", new string('1', 64));
        var offer = await PeerIdentityOffer.CreateAsync(System.IO.Path.Combine(folder.Path, "peer"), "new", "windows", now);
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('A', 64));
        var preview = pairing.Review(offer.ToJson(now), now);
        var runtime = new BlockingRuntime();
        var first = new MultiPeerTrustChangeCoordinator(vault, runtime).RevokeAsync("old");
        await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = new MultiPeerTrustChangeCoordinator(vault, runtime).AddAsync(pairing, preview.Id, offer.KeyboardFingerprint, now);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, runtime.Stops);
        runtime.Release.TrySetResult();
        Assert.True(await first);
        var invitation = await second;
        Assert.True(vault.IsAuthorized("new", invitation.Token));
        Assert.Single(vault.GetTrustedDevices());
        Assert.Equal(2, runtime.Stops);
    }

    [Fact]
    public async Task ClientOnboardingRejectsOtherDevicesAndRequiresHostFingerprint()
    {
        using var folder = new Fixture();
        var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24801, DataPort = 24802,
            KeyboardPort = 24800, HostName = "host", PeerName = "client", CertificateSha256 = new string('A', 64),
            Token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
        await using var store = await PeerInvitationStore.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var onboarding = new MultiPeerClientOnboarding(store, "client");
        Assert.Throws<InvalidOperationException>(() => onboarding.Review((invitation with { PeerName = "other" }).ToJson()));
        var preview = onboarding.Review(invitation.ToJson());
        Assert.DoesNotContain(invitation.Token, preview.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => onboarding.ConfirmAsync(preview.Id, new string('B', 64)));
        Assert.Null(store.Get());
        await onboarding.ConfirmAsync(preview.Id, invitation.CertificateSha256);
        Assert.Equal(invitation, store.Get());
        await Assert.ThrowsAsync<InvalidOperationException>(() => onboarding.ConfirmAsync(preview.Id, invitation.CertificateSha256));
    }

    [Fact]
    public async Task InvalidNewImportClearsPreviousPairingReview()
    {
        using var folder = new Fixture();
        var now = DateTimeOffset.UtcNow;
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var offer = await PeerIdentityOffer.CreateAsync(System.IO.Path.Combine(folder.Path, "peer"), "client", "windows", now);
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", 24801, 24802, 24800, new string('A', 64));
        var old = pairing.Review(offer.ToJson(now), now);
        Assert.ThrowsAny<Exception>(() => pairing.Review("not a pairing file", now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pairing.ConfirmAsync(old.Id, offer.KeyboardFingerprint, now));
        Assert.Empty(vault.GetTrustedDevices());

        var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = 24801, DataPort = 24802,
            KeyboardPort = 24800, HostName = "host", PeerName = "client", CertificateSha256 = new string('A', 64),
            Token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
        await using var store = await PeerInvitationStore.OpenAsync(System.IO.Path.Combine(folder.Path, "client"), new WindowsCredentialProtector());
        var onboarding = new MultiPeerClientOnboarding(store, "client");
        var reviewed = onboarding.Review(invitation.ToJson());
        Assert.ThrowsAny<Exception>(() => onboarding.Review("not an invitation"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => onboarding.ConfirmAsync(reviewed.Id, invitation.CertificateSha256));
        Assert.Null(store.Get());
    }

    [Fact]
    public async Task RevokingStopsKeyboardBeforePersistingAndRestartsOnlyRemainingDevices()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var one = await vault.AddConfirmedAsync("one", new string('1', 64));
        var two = await vault.AddConfirmedAsync("two", new string('2', 64));
        var runtime = new FakeRuntime();
        var coordinator = new MultiPeerTrustChangeCoordinator(vault, runtime);
        Assert.True(await coordinator.RevokeAsync("one"));
        Assert.Equal(new[] { "stop", "start:two" }, runtime.Events);
        Assert.False(vault.IsAuthorized("one", one));
        Assert.True(vault.IsAuthorized("two", two));
    }

    [Fact]
    public async Task FailureToStopDoesNotRevokeAndFailureToRestartDoesNotRestoreRevokedTrust()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, new WindowsCredentialProtector());
        var one = await vault.AddConfirmedAsync("one", new string('1', 64));
        await vault.AddConfirmedAsync("two", new string('2', 64));
        var runtime = new FakeRuntime { FailStop = true };
        var coordinator = new MultiPeerTrustChangeCoordinator(vault, runtime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RevokeAsync("one"));
        Assert.True(vault.IsAuthorized("one", one));
        runtime.FailStop = false;
        runtime.FailStart = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RevokeAsync("one"));
        Assert.False(vault.IsAuthorized("one", one));
    }

    [Fact]
    public async Task FailedRevocationWriteRestartsOldRuntimeAndKeepsAuthorization()
    {
        using var folder = new Fixture();
        var protector = new FaultyProtector();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(folder.Path, protector);
        var one = await vault.AddConfirmedAsync("one", new string('1', 64));
        var runtime = new FakeRuntime();
        var coordinator = new MultiPeerTrustChangeCoordinator(vault, runtime);
        protector.Fail = true;
        await Assert.ThrowsAsync<CryptographicException>(() => coordinator.RevokeAsync("one"));
        Assert.True(vault.IsAuthorized("one", one));
        Assert.Equal(new[] { "stop", "start:one" }, runtime.Events);
    }

    [Fact]
    public async Task GroupRuntimeStartsSeparateChannelsAndRebuildsAfterRevocation()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var one = await vault.AddConfirmedAsync("one", new string('1', 64));
        var two = await vault.AddConfirmedAsync("two", new string('2', 64));
        var keyboards = new List<FakeKeyboard>();
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, peers => { var keyboard = new FakeKeyboard(peers); keyboards.Add(keyboard); return keyboard; });
        await runtime.StartAsync(vault.GetTrustedDevices(), CancellationToken.None);
        Assert.Equal(2, runtime.Devices.Count);
        Assert.NotEqual(runtime.Devices[0].DataPort, runtime.Devices[1].DataPort);
        var onePort = runtime.Devices.Single(x => x.Name == "one").DataPort;
        var twoPort = runtime.Devices.Single(x => x.Name == "two").DataPort;
        var bundle = new DeskLinkPairingBundle { Host = "127.0.0.1", Port = runtime.ControlPort, WindowsName = "host",
            MacName = "one", CertificateSha256 = runtime.CertificateSha256!, Token = one };
        await using var client = new CollaborationClient(bundle, true, onePort);
        Assert.NotNull(await client.GetStatusAsync());
        var inbound = System.IO.Path.Combine(folder.Path, "receive");
        await runtime.SetReceiveDirectoryAsync("one", inbound, CancellationToken.None);
        var twoInbound = System.IO.Path.Combine(folder.Path, "receive-two");
        var twoShare = System.IO.Path.Combine(folder.Path, "share-two");
        Directory.CreateDirectory(twoShare);
        await File.WriteAllTextAsync(System.IO.Path.Combine(twoShare, "shared.txt"), "two share");
        await runtime.SetReceiveDirectoryAsync("two", twoInbound, CancellationToken.None);
        await runtime.SetSharedDirectoriesAsync("two", [new SharedDirectory("share", "share", twoShare)], CancellationToken.None);
        var file = System.IO.Path.Combine(folder.Path, "group-fixture.txt");
        await File.WriteAllTextAsync(file, "group file");
        var receipt = await client.SendFilesAsync([file]);
        Assert.Equal("group file", await File.ReadAllTextAsync(Assert.Single(receipt.SavedPaths)));
        await runtime.QueueFilesAsync("one", [file], CancellationToken.None);
        Assert.Single((await client.GetStatusAsync()).PendingTransfers);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.QueueFilesAsync("unknown", [file], CancellationToken.None));
        var coordinator = new MultiPeerTrustChangeCoordinator(vault, runtime);
        Assert.True(await coordinator.RevokeAsync("one"));
        Assert.True(keyboards[0].Stopped);
        Assert.Single(runtime.Devices);
        Assert.Equal("two", runtime.Devices[0].Name);
        Assert.False(vault.IsAuthorized("one", one));
        Assert.True(vault.IsAuthorized("two", two));
        twoPort = runtime.Devices[0].DataPort;
        var twoInvitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = runtime.ControlPort, DataPort = twoPort,
            HostName = "host", PeerName = "two", CertificateSha256 = runtime.CertificateSha256!, Token = two };
        await using var after = MultiPeerCollaborationClient.Open(twoInvitation, true);
        Assert.Equal("group file", await File.ReadAllTextAsync(Assert.Single((await after.SendFilesAsync([file])).SavedPaths)));
        Assert.Equal("shared.txt", Assert.Single((await after.ListSharedAsync("share")).Items).Name);
    }

    [Fact]
    public async Task HostCanOnlySwitchToAConnectedApprovedDevice()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, peers => new FakeKeyboard(peers));
        await runtime.StartAsync(vault.GetTrustedDevices(), CancellationToken.None);
        Assert.Equal("host", (await runtime.GetKeyboardStateAsync(CancellationToken.None)).ActiveComputer);
        var result = await runtime.SwitchKeyboardAsync("one", CancellationToken.None);
        Assert.True(result.InputTransferred);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.SwitchKeyboardAsync("unknown", CancellationToken.None));
        await runtime.StopAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SwitchKeyboardAsync("one", CancellationToken.None));
    }

    [Fact]
    public async Task ProtectedPerDeviceFoldersSurviveRestartButNotARepairedGrant()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var token = await vault.AddConfirmedAsync("one", new string('1', 64));
        var root = System.IO.Path.Combine(folder.Path, "runtime");
        var settingsRoot = System.IO.Path.Combine(folder.Path, "settings");
        var receive = System.IO.Path.Combine(folder.Path, "receive");
        var shared = System.IO.Path.Combine(folder.Path, "shared");
        Directory.CreateDirectory(shared);
        await File.WriteAllTextAsync(System.IO.Path.Combine(shared, "shared.txt"), "content");
        await using (var settings = await MultiPeerSettingsStore.OpenAsync(settingsRoot, new WindowsCredentialProtector()))
        await using (var runtime = new MultiPeerRuntime(vault, root, "host", System.Net.IPAddress.Loopback, 0, 0, peers => new FakeKeyboard(peers), settings))
        {
            await runtime.StartAsync(vault.GetTrustedDevices(), CancellationToken.None);
            await runtime.SetReceiveDirectoryAsync("one", receive, CancellationToken.None);
            await runtime.SetSharedDirectoriesAsync("one", [new SharedDirectory("shared", "shared", shared)], CancellationToken.None);
            Assert.Equal(30, await runtime.GetPreviewRefreshSecondsAsync("one", CancellationToken.None));
            await runtime.SetPreviewRefreshSecondsAsync("one", 5, CancellationToken.None);
            await runtime.SetDisplayPreferenceAsync("one", new("monitor", 15, 16), CancellationToken.None);
            await Assert.ThrowsAsync<InvalidDataException>(() => runtime.SetDisplayPreferenceAsync("one", new("monitor", 15, 15), CancellationToken.None));
        }
        var protectedBytes = await File.ReadAllBytesAsync(System.IO.Path.Combine(settingsRoot, "device-settings.bin"));
        Assert.DoesNotContain(receive, System.Text.Encoding.UTF8.GetString(protectedBytes));
        await using var reopened = await MultiPeerSettingsStore.OpenAsync(settingsRoot, new WindowsCredentialProtector());
        await using var restarted = new MultiPeerRuntime(vault, root, "host", System.Net.IPAddress.Loopback, 0, 0, peers => new FakeKeyboard(peers), reopened);
        await restarted.StartAsync(vault.GetTrustedDevices(), CancellationToken.None);
        Assert.Equal(5, await restarted.GetPreviewRefreshSecondsAsync("one", CancellationToken.None));
        Assert.Equal(new MultiPeerMonitorSettings("monitor", 15, 16), await restarted.GetDisplayPreferenceAsync("one", CancellationToken.None));
        var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = restarted.ControlPort, DataPort = restarted.Devices.Single().DataPort,
            HostName = "host", PeerName = "one", CertificateSha256 = restarted.CertificateSha256!, Token = token };
        await using (var client = MultiPeerCollaborationClient.Open(invitation, true))
        {
            Assert.Equal("shared.txt", Assert.Single((await client.ListSharedAsync("shared")).Items).Name);
            var source = System.IO.Path.Combine(folder.Path, "sent.txt");
            await File.WriteAllTextAsync(source, "restored");
            Assert.Equal("restored", await File.ReadAllTextAsync(Assert.Single((await client.SendFilesAsync([source])).SavedPaths)));
        }
        await restarted.StopAsync(CancellationToken.None);
        await vault.RevokeAsync("one");
        var replacement = await vault.AddConfirmedAsync("one", new string('1', 64));
        await restarted.StartAsync(vault.GetTrustedDevices(), CancellationToken.None);
        Assert.Null(await restarted.GetDisplayPreferenceAsync("one", CancellationToken.None));
        await using var repaired = MultiPeerCollaborationClient.Open(invitation with { Token = replacement, DataPort = restarted.Devices.Single().DataPort }, true);
        Assert.Empty((await repaired.ListSharedAsync()).Items);
    }

    [Fact]
    public async Task FailedSettingsSaveLeavesPreviousReceiveLocationActive()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var token = await vault.AddConfirmedAsync("one", new string('1', 64));
        var protector = new FaultyProtector();
        await using var settings = await MultiPeerSettingsStore.OpenAsync(System.IO.Path.Combine(folder.Path, "settings"), protector);
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, peers => new FakeKeyboard(peers), settings);
        await runtime.StartAsync(vault.GetTrustedDevices(), CancellationToken.None);
        var oldLocation = System.IO.Path.Combine(folder.Path, "old");
        var newLocation = System.IO.Path.Combine(folder.Path, "new");
        await runtime.SetReceiveDirectoryAsync("one", oldLocation, CancellationToken.None);
        protector.Fail = true;
        await Assert.ThrowsAsync<CryptographicException>(() => runtime.SetReceiveDirectoryAsync("one", newLocation, CancellationToken.None));
        protector.Fail = false;
        var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = runtime.ControlPort, DataPort = runtime.Devices.Single().DataPort,
            HostName = "host", PeerName = "one", CertificateSha256 = runtime.CertificateSha256!, Token = token };
        await using var client = MultiPeerCollaborationClient.Open(invitation, true);
        var source = System.IO.Path.Combine(folder.Path, "fixture.txt");
        await File.WriteAllTextAsync(source, "still old");
        var saved = Assert.Single((await client.SendFilesAsync([source])).SavedPaths);
        Assert.StartsWith(oldLocation, saved, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(newLocation));
    }

    [Fact]
    public async Task DamagedDeviceSettingsAreRetainedAndReported()
    {
        using var folder = new Fixture();
        var file = System.IO.Path.Combine(folder.Path, "device-settings.bin");
        byte[] damaged = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(file, damaged);
        await Assert.ThrowsAnyAsync<Exception>(() => MultiPeerSettingsStore.OpenAsync(folder.Path, new WindowsCredentialProtector()));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public void ClipboardRoutesOnlyToTheActiveOrExplicitlySelectedConnectedDevice()
    {
        string[] connected = ["one", "two"];
        Assert.Null(MultiPeerClipboardTarget.Choose("host", "one", "two", connected));
        Assert.Equal("one", MultiPeerClipboardTarget.Choose("host", "one", "one", connected));
        Assert.Equal("two", MultiPeerClipboardTarget.Choose("host", "host", "two", connected));
        Assert.Null(MultiPeerClipboardTarget.Choose("host", "host", "offline", connected));
        Assert.Null(MultiPeerClipboardTarget.Choose("host", "offline", "two", connected));
        Assert.Null(MultiPeerClipboardTarget.Choose("host", null, "two", connected));
    }

    [Fact]
    public async Task MacProtectorUsesVaultKeyAndRejectsTamperingOrMissingKey()
    {
        using var folder = new Fixture();
        var vault = new SyntheticIdentityVault();
        byte[] encrypted;
        using (var protector = await DesktopToolbox.Mac.Services.MacMultiPeerProtector.OpenAsync(folder.Path, vault))
        {
            encrypted = protector.Protect(System.Text.Encoding.UTF8.GetBytes("private sample"));
            Assert.DoesNotContain("private sample", System.Text.Encoding.UTF8.GetString(encrypted));
            Assert.Equal("private sample", System.Text.Encoding.UTF8.GetString(protector.Unprotect(encrypted)));
            var tampered = encrypted.ToArray(); tampered[^1] ^= 1;
            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(tampered));
        }
        using (var restored = await DesktopToolbox.Mac.Services.MacMultiPeerProtector.OpenAsync(folder.Path, vault))
            Assert.Equal("private sample", System.Text.Encoding.UTF8.GetString(restored.Unprotect(encrypted)));
        await File.WriteAllBytesAsync(System.IO.Path.Combine(folder.Path, "peer-invitation.bin"), encrypted);
        vault.Clear();
        await Assert.ThrowsAsync<CryptographicException>(() => DesktopToolbox.Mac.Services.MacMultiPeerProtector.OpenAsync(folder.Path, vault));
        Assert.Equal(encrypted, await File.ReadAllBytesAsync(System.IO.Path.Combine(folder.Path, "peer-invitation.bin")));
    }

#if FILELESS_PAIRING_CANDIDATE
    [Fact]
    public async Task MacCandidateRetainsDamagedEvidenceAndBlocksConnectionAndReplacement()
    {
        using var folder = new Fixture();
        var identityVault = new SyntheticIdentityVault();
        var macRoot = System.IO.Path.Combine(folder.Path, "mac");
        var engine = System.IO.Path.Combine(folder.Path, "synthetic-mac-engine");
        await using (var initial = await DesktopToolbox.Mac.Services.MacMultiPeerClient.OpenAsync(macRoot, engine, identityVault)) { }
        var evidence = System.IO.Path.Combine(macRoot, "trust", "peer-promotion.bin");
        await File.WriteAllBytesAsync(evidence, [1, 2, 3]);
        await using var mac = await DesktopToolbox.Mac.Services.MacMultiPeerClient.OpenAsync(macRoot, engine, identityVault);
        Assert.Equal(PairingRecoveryPhase.EvidenceProblem, (await mac.GetPairingRecoveryStatusAsync()).Phase);
        var start = await Assert.ThrowsAsync<InvalidOperationException>(() => mac.StartAsync());
        Assert.Contains("待处理的配对", start.Message);
        var replace = await Assert.ThrowsAsync<InvalidOperationException>(() => mac.ReviewInvitationAsync("{}"));
        Assert.Contains("待处理的配对", replace.Message);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(evidence));
    }
#endif

    [Fact]
    public async Task MacCandidatePersistsPairingAndUsesGenericClientControl()
    {
        using var folder = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var identityVault = new SyntheticIdentityVault();
        var macRoot = System.IO.Path.Combine(folder.Path, "mac");
        var engine = System.IO.Path.Combine(folder.Path, "synthetic-mac-engine");
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "host-vault"), new WindowsCredentialProtector());
        var dataPort = FindAvailablePortBlock(); var controlPort = dataPort + 4;
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "host-runtime"), "host",
            System.Net.IPAddress.Loopback, controlPort, dataPort, peers => new FakeKeyboard(peers));
        var hostFingerprint = await runtime.PrepareControlIdentityAsync(CancellationToken.None);
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", controlPort, dataPort, 24800, hostFingerprint);
        await using (var mac = await DesktopToolbox.Mac.Services.MacMultiPeerClient.OpenAsync(macRoot, engine, identityVault))
        {
            var identity = await mac.CreateIdentityAsync(now);
            Assert.Equal("mac", identity.Platform);
            var review = pairing.Review(identity.ToJson(now), now);
            var invitation = await new MultiPeerTrustChangeCoordinator(vault, runtime).AddAsync(pairing, review.Id, identity.KeyboardFingerprint, now);
            var hostReview = await mac.ReviewInvitationAsync(invitation.ToJson());
            await Assert.ThrowsAsync<InvalidOperationException>(() => mac.ConfirmInvitationAsync(hostReview.Id, new string('F', 64)));
            await mac.ConfirmInvitationAsync(hostReview.Id, hostFingerprint);
        }
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        using var socket = new System.Net.Sockets.TcpClient();
        var connecting = socket.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
        using var serverSocket = await listener.AcceptTcpClientAsync(); await connecting;
        await using var server = serverSocket.GetStream();
        var process = new SyntheticProcess();
        var dependencies = new DeskflowSessionDependencies { HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process), ConnectCoreIpcAsync = (_, _) => Task.FromResult<Stream>(socket.GetStream()) };
        await using var reopened = await DesktopToolbox.Mac.Services.MacMultiPeerClient.OpenAsync(macRoot, engine, identityVault, dependencies);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.GetPreviewControlAsync());
        await reopened.StartAsync();
        Assert.True((await reopened.GetPreviewControlAsync()).PreviewHandoffSupported);
        await JsonLineProtocol.ReadAsync(server);
        await server.WriteAsync(System.Text.Encoding.UTF8.GetBytes("versionMismatch=1.27.0+synthetic\n"));
        await JsonLineProtocol.ReadAsync(server);
        var keyboardFingerprint = new string('D', 64);
        await server.WriteAsync(System.Text.Encoding.UTF8.GetBytes("hello=1.27.0+synthetic\npeerFingerprint=" + keyboardFingerprint + "\nconnectionState=Connected\n"));
        await server.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await reopened.GetStateAsync()).PendingPeerFingerprint is null) await Task.Delay(5, timeout.Token);
        await reopened.ConfirmKeyboardAsync(keyboardFingerprint);
        Assert.True(await reopened.SwitchAsync(true));
        Assert.True(await reopened.SwitchAsync(false));
        process.Exit();
    }

    [Fact]
    public async Task LocalDeviceNameIsAutomaticStableAndNeverSilentlyReplacedWhenDamaged()
    {
        using var folder = new Fixture();
        var path = System.IO.Path.Combine(folder.Path, "identity");
        var first = await LocalDeviceName.OpenAsync(path);
        Assert.Matches("^PC-[A-F0-9]{12}$", first);
        Assert.Equal(first, await LocalDeviceName.OpenAsync(path));
        var file = System.IO.Path.Combine(path, "device-name.txt");
        await File.WriteAllTextAsync(file, "bad name!");
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalDeviceName.OpenAsync(path));
        Assert.Equal("bad name!", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task ConcurrentLocalNameCreationProducesOneStableIdentity()
    {
        using var folder = new Fixture();
        var path = System.IO.Path.Combine(folder.Path, "identity");
        var names = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => LocalDeviceName.OpenAsync(path)));
        Assert.Single(names.Distinct());
    }

    [Fact]
    public async Task WindowsPairingAndDataChannelWorkTogetherThroughIsolatedHost()
    {
        using var folder = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var peerName = await LocalDeviceName.OpenAsync(System.IO.Path.Combine(folder.Path, "client-identity"));
        var peerProfile = System.IO.Path.Combine(folder.Path, "client-keyboard");
        var offer = await PeerIdentityOffer.CreateAsync(peerProfile, peerName, "windows", now);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "host-vault"), new WindowsCredentialProtector());
        var dataPort = FindAvailablePortBlock();
        var controlPort = dataPort + 4;
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "host-runtime"), "host",
            System.Net.IPAddress.Loopback, controlPort, dataPort, peers => new FakeKeyboard(peers));
        var fingerprint = await runtime.PrepareControlIdentityAsync(CancellationToken.None);
        var pairing = new MultiPeerPairingCoordinator(vault, "host", "127.0.0.1", controlPort, dataPort, 24800, fingerprint);
        var preview = pairing.Review(offer.ToJson(now), now);
        var changes = new MultiPeerTrustChangeCoordinator(vault, runtime);
        var invitation = await changes.AddAsync(pairing, preview.Id, offer.KeyboardFingerprint, now);
        Assert.Equal(runtime.Devices.Single().DataPort, invitation.DataPort);

        await using var peerStore = await PeerInvitationStore.OpenAsync(System.IO.Path.Combine(folder.Path, "client-vault"), new WindowsCredentialProtector());
        var onboarding = new MultiPeerClientOnboarding(peerStore, peerName);
        var hostPreview = onboarding.Review(invitation.ToJson());
        await onboarding.ConfirmAsync(hostPreview.Id, fingerprint);
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var socket = new System.Net.Sockets.TcpClient();
        var connect = socket.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
        using var serverSocket = await listener.AcceptTcpClientAsync();
        await connect;
        await using var server = serverSocket.GetStream();
        var process = new SyntheticProcess();
        var dependencies = new DeskflowSessionDependencies { HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process), ConnectCoreIpcAsync = (_, _) => Task.FromResult<Stream>(socket.GetStream()) };
        await using var peer = new WindowsPeerLink(peerProfile, System.IO.Path.Combine(folder.Path, "synthetic.exe"), peerStore.Get()!, true, dependencies);
        await peer.StartAsync();
        await JsonLineProtocol.ReadAsync(server);
        await server.WriteAsync(System.Text.Encoding.UTF8.GetBytes("versionMismatch=1.27.0+synthetic\n"));
        await JsonLineProtocol.ReadAsync(server);
        var keyboardFingerprint = new string('D', 64);
        await server.WriteAsync(System.Text.Encoding.UTF8.GetBytes("hello=1.27.0+synthetic\npeerFingerprint=" + keyboardFingerprint + "\nconnectionState=Connected\n"));
        await server.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await peer.GetStateAsync()).PendingPeerFingerprint is null) await Task.Delay(5, timeout.Token);
        await peer.ConfirmKeyboardPeerAsync(keyboardFingerprint);
        Assert.True(await peer.SwitchAsync(peerName));
        Assert.True(await peer.SwitchAsync("host"));
        await using var data = MultiPeerCollaborationClient.Open(peerStore.Get()!, true);
        Assert.NotNull(await data.GetStatusAsync());
        var target = System.IO.Path.Combine(folder.Path, "received");
        await runtime.SetReceiveDirectoryAsync(peerName, target, CancellationToken.None);
        var source = System.IO.Path.Combine(folder.Path, "hello.txt");
        await File.WriteAllTextAsync(source, "isolated test");
        var receipt = await data.SendFilesAsync([source]);
        Assert.Equal("isolated test", await File.ReadAllTextAsync(Assert.Single(receipt.SavedPaths)));
        await runtime.QueueFilesAsync(peerName, [source], CancellationToken.None);
        var pulls = await data.ReceivePendingAsync(System.IO.Path.Combine(folder.Path, "client-receive"));
        Assert.Equal("isolated test", await File.ReadAllTextAsync(Assert.Single(Assert.Single(pulls).SavedPaths)));
        var share = System.IO.Path.Combine(folder.Path, "host-share");
        Directory.CreateDirectory(share);
        await File.WriteAllTextAsync(System.IO.Path.Combine(share, "sample.txt"), "shared sample");
        await runtime.SetSharedDirectoriesAsync(peerName, [new SharedDirectory("sample", "测试目录", share)], CancellationToken.None);
        Assert.Equal("sample.txt", Assert.Single((await data.ListSharedAsync("sample")).Items).Name);
        await runtime.SetClipboardEnabledAsync(peerName, true, CancellationToken.None);
        await data.SetClipboardEnabledAsync(true);
        await data.PublishClipboardAsync(new(Guid.NewGuid().ToString("N"), peerName, DateTimeOffset.UtcNow, "client text"));
        Assert.Equal("client text", (await runtime.GetClipboardAsync(peerName, CancellationToken.None))?.Text);
        await runtime.SetClipboardAsync(peerName, new(Guid.NewGuid().ToString("N"), "host", DateTimeOffset.UtcNow, "host text"), CancellationToken.None);
        Assert.Equal("host text", (await data.GetLatestClipboardAsync())?.Text);
        await runtime.SetPreviewRequestedAsync(peerName, true, CancellationToken.None);
        await data.PublishPreviewAsync(SyntheticFrame());
        Assert.Equal("live", (await runtime.GetPreviewAsync(peerName, CancellationToken.None)).State);
        await data.SetPreviewRequestedAsync(true, previewRefreshSeconds: 30);
        Assert.True((await runtime.GetCaptureDemandAsync(peerName, CancellationToken.None)).Requested);
        await runtime.PublishPreviewAsync(peerName, SyntheticFrame(), CancellationToken.None);
        Assert.Equal("live", (await data.GetPreviewAsync()).State);
        await data.SetPreviewRequestedAsync(false);
        Assert.False((await runtime.GetCaptureDemandAsync(peerName, CancellationToken.None)).Requested);
        var hostRequest = Guid.NewGuid();
        var hostSource = new PreviewSourceStatus(Guid.NewGuid(), 1, "enabled");
        await runtime.SetPreviewSourceAsync(peerName, hostSource, CancellationToken.None);
        await data.SetPreviewRequestedAsync(true, requestId: hostRequest);
        Assert.Equal(hostRequest, (await runtime.GetCaptureDemandAsync(peerName, CancellationToken.None)).RequestId);
        var boundFrame = SyntheticFrame() with { RequestId = hostRequest, SourceStatus = hostSource, CaptureSequence = 1 };
        await runtime.PublishPreviewAsync(peerName, boundFrame, CancellationToken.None);
        Assert.Equal("live", (await data.GetPreviewAsync()).State);
        await runtime.SetPreviewSourceAsync(peerName, hostSource with { Revision = 2, Availability = "locked" }, CancellationToken.None);
        await runtime.PublishPreviewAsync(peerName, boundFrame with { CaptureSequence = 2 }, CancellationToken.None);
        Assert.NotEqual("live", (await data.GetPreviewAsync()).State);
        await data.SetPreviewRequestedAsync(false);
        process.Exit();
    }

    [Fact]
    public async Task FourDevicesUseTheirOwnDataTokensAcrossTheGroupRuntime()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var names = new[] { "one", "two", "three", "four" };
        var tokens = new Dictionary<string, string>();
        for (var i = 0; i < names.Length; i++)
            tokens[names[i]] = await vault.AddConfirmedAsync(names[i], (i + 1).ToString("X64"));
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, peers => new FakeKeyboard(peers));
        await runtime.StartAsync(vault.GetTrustedDevices(), CancellationToken.None);
        Assert.Equal(4, runtime.Devices.Count);
        foreach (var peer in runtime.Devices)
        {
            var invite = new MultiPeerInvitation { Host = "127.0.0.1", Port = runtime.ControlPort, DataPort = peer.DataPort,
                HostName = "host", PeerName = peer.Name, CertificateSha256 = runtime.CertificateSha256!, Token = tokens[peer.Name] };
            await using var correct = MultiPeerCollaborationClient.Open(invite, true);
            Assert.NotNull(await correct.GetStatusAsync());
            var other = names.First(x => x != peer.Name);
            await using var wrong = MultiPeerCollaborationClient.Open(invite with { Token = tokens[other] }, true);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrong.GetStatusAsync());
        }
    }

    [Fact]
    public async Task HostIdentityCanBePreparedBeforeAnyDeviceWithoutOpeningListeners()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, peers => new FakeKeyboard(peers));
        var first = await runtime.PrepareControlIdentityAsync(CancellationToken.None);
        var second = await runtime.PrepareControlIdentityAsync(CancellationToken.None);
        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(0, runtime.ControlPort);
        Assert.Empty(runtime.Devices);
    }

    [Fact]
    public async Task FailedGroupStartupCleansComponentsWithoutChangingTrust()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        var token = await vault.AddConfirmedAsync("one", new string('1', 64));
        FakeKeyboard? keyboard = null;
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, peers => keyboard = new FakeKeyboard(peers) { FailStart = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync(vault.GetTrustedDevices(), CancellationToken.None));
        Assert.True(keyboard!.Stopped);
        Assert.Empty(runtime.Devices);
        Assert.Equal(0, runtime.ControlPort);
        Assert.True(vault.IsAuthorized("one", token));
    }

    [Fact]
    public async Task GroupRejectsDuplicateOrStaleDeviceSnapshotsBeforeStartingAnything()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        await vault.AddConfirmedAsync("two", new string('2', 64));
        var approved = vault.GetTrustedDevices();
        var started = false;
        await using var runtime = new MultiPeerRuntime(vault, System.IO.Path.Combine(folder.Path, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, peers => { started = true; return new FakeKeyboard(peers); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync([approved[0], approved[0]], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync([approved[0], approved[1] with { Slot = 4 }], CancellationToken.None));
        Assert.False(started);
    }

    [Fact]
    public async Task UnavailableLanStopsGroupBeforeStartingAnyComponent()
    {
        using var folder = new Fixture();
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(System.IO.Path.Combine(folder.Path, "vault"), new WindowsCredentialProtector());
        await vault.AddConfirmedAsync("one", new string('1', 64));
        var address = System.Net.IPAddress.Parse("172.30.222.254");
        var snapshot = new LanInterfaceSnapshot("test", "ethernet", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
            System.Net.NetworkInformation.OperationalStatus.Up, [address]);
        var binding = VerifiedLanBinding.FromSnapshots(address, true, [snapshot]);
        var root = System.IO.Path.Combine(folder.Path, "runtime");
        await using var runtime = MultiPeerRuntime.CreateForLan(vault, root, "host", binding, 24801, 24802,
            System.IO.Path.Combine(folder.Path, "synthetic.exe"), MultiPeerKeyboardSession.ExperimentalEngineSha256);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync(vault.GetTrustedDevices(), CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    private sealed class CancelOnStopRuntime(CancellationTokenSource cancellation) : IMultiPeerRuntime
    {
        public bool Started { get; private set; }
        public bool StartTokenCanBeCancelled { get; private set; }
        public Task StopAsync(CancellationToken ct) { cancellation.Cancel(); return Task.CompletedTask; }
        public Task StartAsync(IReadOnlyList<TrustedDeviceIdentity> peers, CancellationToken ct)
        { Started = true; StartTokenCanBeCancelled = ct.CanBeCanceled; return Task.CompletedTask; }
    }

    private sealed class FakeRuntime : IMultiPeerRuntime
    {
        public List<string> Events { get; } = [];
        public bool FailStop { get; set; }
        public bool FailStart { get; set; }
        public int FailStartsRemaining { get; set; }
        public Task StopAsync(CancellationToken ct)
        {
            Events.Add("stop");
            if (FailStop) throw new InvalidOperationException("stop failed");
            return Task.CompletedTask;
        }
        public Task StartAsync(IReadOnlyList<TrustedDeviceIdentity> peers, CancellationToken ct)
        {
            Events.Add("start:" + string.Join(",", peers.Select(x => x.Name)));
            if (FailStart || FailStartsRemaining-- > 0) throw new InvalidOperationException("start failed");
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingRuntime : IMultiPeerRuntime
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops;
        public async Task StopAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref Stops) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
        }
        public Task StartAsync(IReadOnlyList<TrustedDeviceIdentity> peers, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SyntheticIdentityVault : Nian.Input.Security.IIdentityVault
    {
        private readonly Dictionary<string, byte[]> entries = new();
        public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(entries.TryGetValue(key, out var data) ? data.ToArray() : null);
        public Task StoreAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        { entries.Add(key, value.ToArray()); return Task.CompletedTask; }
        public void Clear() => entries.Clear();
    }

    private static int FindAvailablePortBlock()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var first = Random.Shared.Next(40000, 59000);
            var listeners = new List<System.Net.Sockets.TcpListener>();
            try
            {
                for (var i = 0; i < 5; i++)
                {
                    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, first + i);
                    listeners.Add(listener); listener.Start();
                }
                return first;
            }
            catch (System.Net.Sockets.SocketException) { }
            finally { foreach (var listener in listeners) listener.Stop(); }
        }
        throw new IOException("No free local test port block was found.");
    }

    private sealed class SyntheticProcess : IDeskflowOwnedProcess
    {
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => 456789;
        public bool HasExited => completion.Task.IsCompleted;
        public Task<int> Completion => completion.Task;
        public void Exit() => completion.TrySetResult(0);
        public Task StopAsync(CancellationToken ct = default) { Exit(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StatefulKeyboard : IMultiPeerKeyboard, IMultiPeerKeyboardFocusEvents
    {
        private readonly object sync = new();
        private string active = "host";
        private long revision = 1;
        public string? FailTarget { get; set; }
        public string? NextExternalFocus { get; set; }
        public event EventHandler<FocusChangedEvent>? FocusChanged;
        public void ExternalFocus(string target)
        {
            long changed;
            lock (sync) { active = target; changed = ++revision; }
            FocusChanged?.Invoke(this, new FocusChangedEvent(target, changed));
        }
        public Task<DeskLinkState> StartAsync(CancellationToken ct) => GetStateAsync(ct);
        public Task<DeskLinkState> GetStateAsync(CancellationToken ct)
        {
            lock (sync) return Task.FromResult(new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
                NativeControlAvailable = true, ActiveComputer = active, Revision = revision, ConnectedComputers = ["one", "two"] });
        }
        public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long expectedRevision, CancellationToken ct)
        {
            lock (sync)
            {
                if (target == FailTarget || active != expected || revision != expectedRevision)
                    return Task.FromResult(new DeskLinkSwitchResult { Error = "not transferred" });
                active = target; revision++;
                if (NextExternalFocus is { } other) { active = other; revision++; NextExternalFocus = null; }
                return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target });
            }
        }
        public Task<DeskLinkState> StopAsync(CancellationToken ct) => GetStateAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnverifiedDisplay : IUnifiedDisplayControl
    {
        public Task<DisplaySwitchOutcome> ApplyAsync(string computer, CancellationToken cancellationToken) =>
            Task.FromResult(new DisplaySwitchOutcome("acceptedUnverified", "private-device-details"));
    }

    private sealed class RecordingDisplay : IUnifiedDisplayControl
    {
        public TaskCompletionSource<string> Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DisplaySwitchOutcome> ApplyAsync(string computer, CancellationToken cancellationToken)
        { Seen.TrySetResult(computer); return Task.FromResult(new DisplaySwitchOutcome("verified")); }
    }

    private sealed class SyntheticLiveCapture : ILivePreviewCaptureProvider
    {
        public bool IsSupported => true;
        public async IAsyncEnumerable<LivePreviewCapturePacket> CaptureAsync(LivePreviewCaptureOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new() { Kind = LivePreviewCaptureKind.HealthyIdle, Sequence = 1 };
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class SyntheticImageCapture(PreviewFrame image) : ILivePreviewCaptureProvider
    {
        public bool IsSupported => true;
        public async IAsyncEnumerable<LivePreviewCapturePacket> CaptureAsync(LivePreviewCaptureOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new() { Kind = LivePreviewCaptureKind.Image, Sequence = 1, Width = image.Width, Height = image.Height,
                JpegBytes = image.JpegBytes, CapturedAtUtc = DateTimeOffset.UtcNow };
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class RemoteControlAdapter(MultiPeerControlClient client) : IDeskflowPreviewControl
    {
        public bool PreviewHandoffSupported => true;
        public string PreviewReturnShortcut => "归还按钮";
        public string PreviewHandoffHint => "";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken cancellationToken = default) => client.BeginPreviewAsync(cancellationToken);
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken cancellationToken = default) => client.RenewPreviewAsync(id, cancellationToken);
        public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken cancellationToken = default) => client.EndPreviewAsync(id, cancellationToken);
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken = default) => client.ReadPreviewAsync(cancellationToken);
    }

    private sealed class FakeKeyboard(IReadOnlyList<TrustedDeviceIdentity> peers) : IMultiPeerKeyboard
    {
        private readonly object sync = new();
        private string active = "host";
        private long revision = 1;
        public bool Stopped { get; private set; }
        public bool FailStart { get; set; }
        public Task<DeskLinkState> StartAsync(CancellationToken ct) => FailStart
            ? throw new InvalidOperationException("synthetic startup failure") : GetStateAsync(ct);
        public Task<DeskLinkState> GetStateAsync(CancellationToken ct)
        {
            lock (sync) return Task.FromResult(new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
                NativeControlAvailable = true, ActiveComputer = active, Revision = revision, ConnectedComputers = peers.Select(x => x.Name).ToArray() });
        }
        public Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long expectedRevision, CancellationToken ct)
        {
            lock (sync)
            {
                if (active != expected || revision != expectedRevision) return Task.FromResult(new DeskLinkSwitchResult { Error = "stale" });
                active = target; revision++;
                return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target });
            }
        }
        public Task<DeskLinkState> StopAsync(CancellationToken ct) { Stopped = true; return Task.FromResult(new DeskLinkState()); }
        public ValueTask DisposeAsync() { Stopped = true; return ValueTask.CompletedTask; }
    }

    private static PreviewFrame SyntheticFrame()
    {
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
            System.Windows.Media.PixelFormats.Bgr24, null, new byte[] { 20, 40, 60 }, 3);
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return new PreviewFrame { State = "live", Width = 1, Height = 1, JpegBytes = output.ToArray() };
    }

    private sealed class FaultyProtector : IMultiDeviceSecretProtector
    {
        private readonly WindowsCredentialProtector inner = new();
        public bool Fail { get; set; }
        public byte[] Protect(byte[] plaintext) => Fail ? throw new CryptographicException("synthetic failure") : inner.Protect(plaintext);
        public byte[] Unprotect(byte[] ciphertext) => inner.Unprotect(ciphertext);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../.work/multipeer-vault-tests"));
        public string Path { get; } = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));
        public Fixture() { EnsureNoLinks(Root); Directory.CreateDirectory(Path); }
        public void Dispose()
        {
            if (!Path.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Invalid test path.");
            EnsureNoLinks(Path);
            if (!Directory.Exists(Path)) return;
            var pending = new Stack<string>(); pending.Push(Path);
            while (pending.TryPop(out var directory))
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture contains a link; it was retained.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                }
            Directory.Delete(Path, true);
        }
        private static void EnsureNoLinks(string path)
        {
            for (var current = System.IO.Path.GetFullPath(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Test path contains a link.");
        }
    }
}
