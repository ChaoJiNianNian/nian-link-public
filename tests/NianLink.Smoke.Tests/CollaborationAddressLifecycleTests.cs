using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.IO;
using System.Windows.Threading;
using DesktopToolbox.App.DeskLink;
using DesktopToolbox.Core;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.Windows.DeskSwitch;

namespace NianLink.Smoke.Tests;

public sealed class CollaborationAddressLifecycleTests
{
    [Fact]
    public async Task MissingOrFailedAddressStopsRealListenerAndRecoveryRebinds()
    {
        using var syntheticRoot = SyntheticWorkDirectory.Create("lan-e2e-");
        var root = syntheticRoot.Path;
        var profile = Path.Combine(root, "desk-link", "control");
        // Only a synthetic, randomly generated localhost identity is created.
        await using (var seed = new PairedControlServer(new() { ProfileDirectory = profile, BindAddress = IPAddress.Loopback,
            Port = 0, AllowedTargets = ["Win-PC", "My-Mac"] }, _ => Task.FromResult(new DeskLinkState()),
            (_, _) => Task.FromResult(new DeskLinkSwitchResult())))
            await seed.StartAsync();

        var paths = new AppPaths(root);
        var context = new ModuleContext(paths, new JsonFileStore(paths), new NoPrompts(), (_, _) => { });
        IPAddress? address = IPAddress.Loopback;
        var throwOnRead = false;
        await using var host = new WindowsCollaborationHost(context, new NoKeyboard(), Dispatcher.CurrentDispatcher,
            _ => { }, dataPort: 0,
            captureFrame: (_, _, _) => throw new InvalidOperationException("The fixture must never capture the screen."),
            liveCaptureProvider: new NoCapture(), desktopUnlockedProvider: () => false)
        {
            TestAddressProvider = () => throwOnRead ? throw new InvalidOperationException("synthetic network error") : address,
            DisableClipboardForTesting = true
        };

        await host.RefreshAsync();
        var firstPort = host.DataPort;
        Assert.InRange(firstPort, 1, 65535);
        await AssertListening(firstPort);

        address = null;
        await host.RefreshAsync();
        Assert.Equal(0, host.DataPort);
        await AssertNotListening(firstPort);

        address = IPAddress.Loopback;
        await host.RefreshAsync();
        var secondPort = host.DataPort;
        Assert.InRange(secondPort, 1, 65535);
        await AssertListening(secondPort);

        throwOnRead = true;
        await host.RefreshAsync();
        Assert.Equal(0, host.DataPort);
        await AssertNotListening(secondPort);
    }

    private static async Task AssertListening(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(client.Connected);
    }

    private static async Task AssertNotListening(int port)
    {
        // Some Windows network filters time out rather than returning a TCP
        // reset after Stop(). The OS listener table is authoritative for
        // whether this loopback endpoint is still listening.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(endpoint => endpoint.Address.Equals(IPAddress.Loopback) && endpoint.Port == port)) return;
            await Task.Delay(50);
        }
        Assert.Fail("The synthetic loopback endpoint remained in the TCP listener table.");
    }

    private sealed class NoPrompts : IUserPrompts
    {
        public Task<bool> ConfirmAsync(string title, string message) => throw new InvalidOperationException();
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => throw new InvalidOperationException();
        public Task<string?> PickFileAsync(string title, string filter) => throw new InvalidOperationException();
        public Task RevealAsync(string path, bool selectFile = false) => throw new InvalidOperationException();
    }
    private sealed class NoKeyboard : IIntegratedDeskSwitchService
    {
        public bool IsRunning => false;
        public IntegratedDeskSwitchStatus Snapshot => new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<IntegratedDeskSwitchStatus> StartAsync(DeskSwitchSettings value, CancellationToken token) => throw new InvalidOperationException();
        public Task<IntegratedDeskSwitchStatus> StopAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<IntegratedDeskSwitchStatus> RefreshAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<DeskLinkSwitchResult> SwitchAsync(string target, CancellationToken token) => throw new InvalidOperationException();
        public Task ConfirmPeerAsync(string fingerprint, CancellationToken token) => throw new InvalidOperationException();
        public Task RejectPeerAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<string> ExportPairingAsync(CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class NoCapture : ILivePreviewCaptureProvider
    {
        public bool IsSupported => false;
        public IAsyncEnumerable<LivePreviewCapturePacket> CaptureAsync(LivePreviewCaptureOptions options, CancellationToken token = default) =>
            throw new InvalidOperationException("The fixture must never capture the screen.");
    }
}
