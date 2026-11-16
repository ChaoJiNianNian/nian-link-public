using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Coordination;
namespace DeskLink.Tests;
public sealed class CollaborationTransportTests
{
    [Fact]
    public async Task RemoteDisplayWorkerOnlyAdvertisesAndExecutesWhileExplicitlyAllowed()
    {
        using var folder = new TestFolder(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, EnableExperimentalDisplay = true }); await server.StartAsync();
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port);
        var guard = new MultiPeerDisplayCommandGuard("shared27", [15u, 16u]);
        var device = new SimulatedDisplayDevice(); var allowed = false;
        var worker = new MultiScreenDisplayWorker(client, new MultiPeerDisplayExecutor(guard, device), () => allowed);
        var request = new MultiPeerDisplayRequest(Guid.NewGuid(), guard.Epoch, "shared27", 15, 16, 1, DateTimeOffset.UtcNow.AddSeconds(8));
        var result = server.ExperimentalDisplayQueue!.Enqueue(request);
        await worker.PollAsync();
        Assert.Null(server.ExperimentalPeerDisplayEpoch); Assert.Equal(0, device.Writes);
        allowed = true; await worker.PollAsync();
        Assert.Equal("acceptedUnverified", (await result).Status); Assert.Equal(1, device.Writes);
        await worker.PollAsync(); Assert.Equal(1, device.Writes);
    }

    [Fact]
    public async Task DisplayCapabilityIsAcceptedOnlyFromAuthenticatedPeerAndExpires()
    {
        using var folder = new TestFolder(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        var clock = new ManualClock();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, EnableExperimentalDisplay = true }, clock); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var approved = new CollaborationClient(bundle, true, server.Port);
        await using var unapproved = new CollaborationClient(bundle with { Token = Convert.ToBase64String(new byte[32]) }, true, server.Port);
        var epoch = Guid.NewGuid();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => unapproved.GetStatusAsync(displayEpoch: epoch));
        Assert.Null(server.ExperimentalPeerDisplayEpoch);
        await approved.GetStatusAsync(displayEpoch: epoch);
        Assert.Equal(epoch, server.ExperimentalPeerDisplayEpoch);
        clock.Now += TimeSpan.FromSeconds(5);
        Assert.Null(server.ExperimentalPeerDisplayEpoch);
    }

    [Fact]
    public async Task RemoteScreenRouterRoundTripsViaThePairedTlsChannel()
    {
        using var folder = new TestFolder(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, EnableExperimentalDisplay = true }); await server.StartAsync();
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port);
        var guard = new MultiPeerDisplayCommandGuard("shared27", [15u, 16u]);
        var device = new SimulatedDisplayDevice(); var executor = new MultiPeerDisplayExecutor(guard, device);
        var router = new MultiScreenDeviceRouter("desktop", _ => null,
            peer => peer == "mac" ? new RemoteScreenQueue(guard.Epoch, "mac-monitor", server.ExperimentalDisplayQueue!) : null);
        var command = new MultiScreenPendingSwitch(Guid.NewGuid(), "desktop", 1, new("shared27", "mac", "mac-monitor", 15, 16));
        var result = router.ExecuteAsync(command);
        var request = (await client.GetStatusAsync(displayEpoch: guard.Epoch)).DisplayRequest;
        Assert.NotNull(request);
        var completed = await executor.ExecuteAsync(request);
        await client.CompleteDisplayRequestAsync(request.Id, completed.Status);
        Assert.Equal("acceptedUnverified", (await result).Status);
        Assert.Equal(1, device.Writes);
    }

    [Fact]
    public async Task PairedDisplayRequestPassesLocalGuardBeforeSimulatedDriverAndAcknowledgement()
    {
        using var folder = new TestFolder(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, EnableExperimentalDisplay = true }); await server.StartAsync();
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port);
        var guard = new MultiPeerDisplayCommandGuard("screen", [15u, 16u]);
        var device = new SimulatedDisplayDevice(); var executor = new MultiPeerDisplayExecutor(guard, device);
        var request = new MultiPeerDisplayRequest(Guid.NewGuid(), guard.Epoch, "screen", 15, 16, 1, DateTimeOffset.UtcNow.AddSeconds(8));
        var pending = server.ExperimentalDisplayQueue!.Enqueue(request);
        var delivered = (await client.GetStatusAsync(displayEpoch: guard.Epoch)).DisplayRequest;
        Assert.NotNull(delivered);
        var outcome = await executor.ExecuteAsync(delivered);
        await client.CompleteDisplayRequestAsync(delivered.Id, outcome.Status);
        Assert.Equal("acceptedUnverified", (await pending).Status);
        Assert.Equal(1, device.Writes);
        Assert.Equal("failed", (await executor.ExecuteAsync(delivered)).Status);
        Assert.Equal(1, device.Writes);
    }

    private sealed class SimulatedDisplayDevice : IMultiPeerDisplayDevice
    {
        public int Writes { get; private set; }
        public Task<DisplayInputObservation> ReadAsync(string monitor, CancellationToken ct) => Task.FromResult(new DisplayInputObservation(15, true));
        public Task<DisplaySwitchOutcome> WriteAsync(string monitor, uint input, CancellationToken ct) { Writes++; return Task.FromResult(new DisplaySwitchOutcome("acceptedUnverified")); }
    }

    [Fact]
    public async Task ExperimentalDisplayCommandUsesPairedTlsAndCannotBeReplayed()
    {
        using var folder = new TestFolder(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, EnableExperimentalDisplay = true });
        await server.StartAsync();
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port);
        var epoch = Guid.NewGuid();
        var request = new DesktopToolbox.DeskLink.Coordination.MultiPeerDisplayRequest(Guid.NewGuid(), epoch, "screen", 15, 16, 1, DateTimeOffset.UtcNow.AddSeconds(8));
        var result = server.ExperimentalDisplayQueue!.Enqueue(request);
        Assert.Null((await client.GetStatusAsync(displayEpoch: Guid.NewGuid())).DisplayRequest);
        Assert.Equal(request, (await client.GetStatusAsync(displayEpoch: epoch)).DisplayRequest);
        Assert.Null((await client.GetStatusAsync(displayEpoch: epoch)).DisplayRequest);
        await client.CompleteDisplayRequestAsync(request.Id, "acceptedUnverified");
        Assert.Equal("acceptedUnverified", (await result).Status);
        await client.CompleteDisplayRequestAsync(request.Id, "acceptedUnverified");
        await Assert.ThrowsAsync<IOException>(() => client.CompleteDisplayRequestAsync(request.Id, "verified"));
    }

    [Fact]
    public async Task DisplayCommandIsDisabledByDefaultAndTokenRotationInvalidatesItsQueue()
    {
        using var folder = new TestFolder(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using (var disabled = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }))
        {
            await disabled.StartAsync();
            await using var legacy = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, disabled.Port);
            Assert.Null(disabled.ExperimentalDisplayQueue);
            Assert.Null((await legacy.GetStatusAsync(displayEpoch: Guid.NewGuid())).DisplayRequest);
            await Assert.ThrowsAsync<NotSupportedException>(() => legacy.CompleteDisplayRequestAsync(Guid.NewGuid(), "verified"));
        }
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, EnableExperimentalDisplay = true });
        await server.StartAsync();
        var oldBundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var old = new CollaborationClient(oldBundle, true, server.Port);
        var epoch = Guid.NewGuid();
        var request = new DesktopToolbox.DeskLink.Coordination.MultiPeerDisplayRequest(Guid.NewGuid(), epoch, "screen", 15, 16, 1, DateTimeOffset.UtcNow.AddSeconds(8));
        var unresolved = server.ExperimentalDisplayQueue!.Enqueue(request);
        Assert.Equal(request, (await old.GetStatusAsync(displayEpoch: epoch)).DisplayRequest);
        await control.RegeneratePairingTokenAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => old.CompleteDisplayRequestAsync(request.Id, "verified"));
        await using var replacement = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port);
        Assert.Null((await replacement.GetStatusAsync(displayEpoch: epoch)).DisplayRequest);
        Assert.Null(server.ExperimentalDisplayQueue);
        await Assert.ThrowsAsync<NotSupportedException>(() => replacement.CompleteDisplayRequestAsync(request.Id, "verified"));
        Assert.False(unresolved.IsCompleted);
    }

    [Fact]
    public async Task DisplayCommandsStayOnTheirIndividuallyPairedDeviceEndpoint()
    {
        using var folder = new TestFolder(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        var secondDirectory = Path.Combine(folder.Path, "second"); Directory.CreateDirectory(secondDirectory);
        var secondToken = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await using var first = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, EnableExperimentalDisplay = true });
        await using var second = new CollaborationServer(new() { ProfileDirectory = secondDirectory, Port = 0, EnableExperimentalDisplay = true,
            CertificateFile = Path.Combine(folder.Path, "control.pem"), TokenProvider = _ => Task.FromResult(secondToken.ToArray()) });
        await first.StartAsync(); await second.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var firstClient = new CollaborationClient(bundle, true, first.Port);
        await using var secondClient = new CollaborationClient(bundle with { Token = Convert.ToBase64String(secondToken) }, true, second.Port);
        await using var wrongToken = new CollaborationClient(bundle, true, second.Port);
        var epoch = Guid.NewGuid();
        var request = new DesktopToolbox.DeskLink.Coordination.MultiPeerDisplayRequest(Guid.NewGuid(), epoch, "screen", 15, 16, 1, DateTimeOffset.UtcNow.AddSeconds(8));
        var result = first.ExperimentalDisplayQueue!.Enqueue(request);
        Assert.Null((await secondClient.GetStatusAsync(displayEpoch: epoch)).DisplayRequest);
        await Assert.ThrowsAsync<IOException>(() => secondClient.CompleteDisplayRequestAsync(request.Id, "verified"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrongToken.GetStatusAsync(displayEpoch: epoch));
        Assert.Equal(request, (await firstClient.GetStatusAsync(displayEpoch: epoch)).DisplayRequest);
        await firstClient.CompleteDisplayRequestAsync(request.Id, "acceptedUnverified");
        Assert.Equal("acceptedUnverified", (await result).Status);
    }

    [Fact]
    public async Task PairedDataServiceSendsBothWaysAndBrowsesOnlySelectedShares()
    {
        using var folder = new TestFolder(); var receive = Path.Combine(folder.Path, "win-receive"); var macReceive = Path.Combine(folder.Path, "mac-receive");
        var files = Path.Combine(folder.Path, "files"); Directory.CreateDirectory(files); await File.WriteAllTextAsync(Path.Combine(files, "a.txt"), "hello from Mac");
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, ReceiveDirectory = receive, Port = 0, SharedDirectories = [new("work", "工作", files)] });
        await server.StartAsync(); await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port);
        var receipt = await client.SendFilesAsync([Path.Combine(files, "a.txt")]); Assert.Equal("hello from Mac", await File.ReadAllTextAsync(Assert.Single(receipt.SavedPaths)));
        await server.QueueFilesAsync([Path.Combine(files, "a.txt")]); var received = await client.ReceivePendingAsync(macReceive);
        Assert.Equal("hello from Mac", await File.ReadAllTextAsync(Assert.Single(Assert.Single(received).SavedPaths)));
        Assert.Empty((await client.GetStatusAsync()).PendingTransfers);
        Assert.Equal("a.txt", Assert.Single((await client.ListSharedAsync("work")).Items).Name);
        Assert.Equal("hello from Mac", System.Text.Encoding.UTF8.GetString((await client.GetSharedPreviewAsync("work", "a.txt")).Bytes));
        Assert.True(server.IsPeerConnected);
    }
    [Fact]
    public async Task RejectedTokenCannotReadDirectoriesAndRotatedTokenInvalidatesOldClient()
    {
        using var folder = new TestFolder(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var client = new CollaborationClient(bundle, true, server.Port); await client.GetStatusAsync();
        await control.RegeneratePairingTokenAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ListSharedAsync());
    }
    [Fact]
    public async Task PreviewAndClipboardHaveIndependentExplicitOptInAndNoOldFrameOnOff()
    {
        using var folder = new TestFolder(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port);
        await client.SetPreviewRequestedAsync(true); Assert.True(server.LocalCaptureRequested);
        server.SetLocalPreview(CollaborationJpegFixture.Frame()); Assert.Equal("live", (await client.GetPreviewAsync()).State);
        await client.SetPreviewRequestedAsync(false); Assert.Empty((await client.GetPreviewAsync()).JpegBytes);
        server.SetLocalClipboardEnabled(true); await client.SetClipboardEnabledAsync(true);
        await client.PublishClipboardAsync(new(Guid.NewGuid().ToString("N"), "Mac", DateTimeOffset.UtcNow, "Mac text"));
        Assert.Equal("Mac text", server.GetRemoteClipboard()!.Text);
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Win", DateTimeOffset.UtcNow, "Win text"));
        Assert.Null(server.GetRemoteClipboard()); Assert.Equal("Win text", (await client.GetLatestClipboardAsync())!.Text);
        await client.SetClipboardEnabledAsync(false); Assert.Null(server.GetRemoteClipboard()); Assert.Null(await client.GetLatestClipboardAsync());
    }
    [Fact]
    public async Task UnsupportedLocalCopyCanClearOlderRemoteClipboardWithoutSendingContent()
    {
        using var folder = new TestFolder(); await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port);
        server.SetLocalClipboardEnabled(true); await client.SetClipboardEnabledAsync(true);
        await client.PublishClipboardAsync(new(Guid.NewGuid().ToString("N"), "Mac", DateTimeOffset.UtcNow, Text: "old remote")); Assert.NotNull(server.GetRemoteClipboard());
        server.ClearClipboard(); Assert.Null(server.GetRemoteClipboard()); Assert.Null(await client.GetLatestClipboardAsync());
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Win", DateTimeOffset.UtcNow, Text: "another old value"));
        await client.ClearClipboardAsync(); Assert.Null(await client.GetLatestClipboardAsync()); Assert.Null(server.GetRemoteClipboard());
        await client.SetClipboardEnabledAsync(false); Assert.Throws<InvalidOperationException>(() => server.ClearClipboard());
    }
    [Fact]
    public async Task ReconnectedPeerNeedsFreshFramesButActiveFileConnectionsKeepCurrentPreview()
    {
        using var folder = new TestFolder(); var source = Path.Combine(folder.Path, "file.txt"); await File.WriteAllTextAsync(source, "independent transfer");
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        var clock = new ManualClock();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, ReceiveDirectory = Path.Combine(folder.Path, "receive") }, clock);
        await server.StartAsync(); var pairing = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var client = new CollaborationClient(pairing, true, server.Port);
        server.SetPreviewRequested(true); await client.PublishPreviewAsync(CollaborationJpegFixture.Frame());
        await client.SetPreviewRequestedAsync(true); server.SetLocalPreview(CollaborationJpegFixture.Frame());
        await using (var fileConnection = new CollaborationClient(pairing, true, server.Port))
            Assert.Single((await fileConnection.SendFilesAsync([source])).SavedPaths);
        Assert.Equal("live", server.GetRemotePreview().State); Assert.Equal("live", (await client.GetPreviewAsync()).State);
        clock.Now += TimeSpan.FromSeconds(4); server.SetPreviewRequested(true);
        clock.Now += TimeSpan.FromSeconds(4); server.SetPreviewRequested(true);
        clock.Now += TimeSpan.FromSeconds(1); Assert.False(server.IsPeerConnected);
        await client.GetStatusAsync();
        Assert.True(server.IsPeerConnected); Assert.Empty(server.GetRemotePreview().JpegBytes);
        await client.SetPreviewRequestedAsync(true); Assert.Empty((await client.GetPreviewAsync()).JpegBytes);
    }
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static PairedControlServer CreateControl(string profile) => new(new() { ProfileDirectory = profile, Port = 0, AllowedTargets = ["Win-PC", "My-Mac"] }, _ => Task.FromResult(new DeskLinkState()), (_, _) => Task.FromResult(new DeskLinkSwitchResult()));
}
