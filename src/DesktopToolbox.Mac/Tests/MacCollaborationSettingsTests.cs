using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacCollaborationSettingsTests
{
    [Fact]
    public void NewInstallDoesNotStartScreenOrClipboardSharing()
    {
        var settings = new MacCollaborationSettings();
        Assert.False(settings.PreviewEnabled);
        Assert.Equal(30, settings.PreviewRefreshSeconds);
        Assert.False(settings.ClipboardEnabled);
        Assert.Empty(settings.SharedDirectories);
        Assert.Empty(settings.ReceiveDirectory);
    }

    [Fact]
    public async Task RestartRemembersIndependentChoicesWithoutChangingConnectionSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nian-collab-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MacPrivateStore(directory);
            await store.WriteAsync("connection.json", new MacConnectionSettings { WindowsHost = "paired-host" }, default);
            var settings = new MacCollaborationSettings { PreviewEnabled = true, PreviewRefreshSeconds = 60, ClipboardEnabled = false, Pinned = true, ReceiveDirectory = Path.Combine(directory, "receive") };
            await store.WriteAsync("collaboration.json", settings, default);
            var restarted = new MacPrivateStore(directory);
            var restored = await restarted.ReadAsync<MacCollaborationSettings>("collaboration.json", default);
            Assert.NotNull(restored);
            Assert.True(restored.PreviewEnabled);
            Assert.Equal(60, restored.PreviewRefreshSeconds);
            Assert.False(restored.ClipboardEnabled);
            Assert.True(restored.Pinned);
            Assert.Equal(settings.ReceiveDirectory, restored.ReceiveDirectory);
            Assert.Empty(restored.SharedDirectories);
            Assert.Equal("paired-host", (await restarted.ReadAsync<MacConnectionSettings>("connection.json", default))!.WindowsHost);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ChangingRefreshSavesOnlyTheIntervalAndRejectsUnsupportedValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nian-refresh-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var service = new MacCollaborationService(directory, _ => Task.FromResult<DesktopToolbox.DeskLink.DeskLinkPairingBundle?>(null));
            await service.SetPreviewRefreshSecondsAsync(10);
            var saved = await new MacPrivateStore(directory).ReadAsync<MacCollaborationSettings>("collaboration.json", default);
            Assert.Equal(10, saved!.PreviewRefreshSeconds); Assert.False(saved.PreviewEnabled); Assert.False(saved.ClipboardEnabled);
            await Assert.ThrowsAsync<ArgumentException>(() => service.SetPreviewRefreshSecondsAsync(2));
            Assert.Equal(10, service.Snapshot.Settings.PreviewRefreshSeconds);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(11, 180, "left", 0, 180)]
    [InlineData(1190, 180, "right", 1220, 180)]
    [InlineData(600, -80, "", 600, 0)]
    public void DragPlacementSnapsNearEdgesAndKeepsTheWindowInsideWorkArea(int x, int y, string edge, int expectedX, int expectedY)
    {
        var placement = MacMiniPlacement.Resolve(x, y, 380, 468, 0, 0, 1600, 1000, 40);
        Assert.Equal(edge, placement.Edge); Assert.Equal(expectedX, placement.X); Assert.Equal(expectedY, placement.Y);
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    public void CaptureSubscriptionRequiresManualChoiceAndAtLeastOneVisibleViewer(bool enabled, bool miniVisible, bool largeVisible, bool expected)
    {
        Assert.Equal(expected, MacPreviewPresentation.ShouldSubscribe(enabled, miniVisible, largeVisible, peerOnline: true));
        Assert.False(MacPreviewPresentation.ShouldSubscribe(enabled, miniVisible, largeVisible, peerOnline: false));
    }

    [Fact]
    public void EnlargingPreviewLeavesMiniWindowDimensionsUnchanged()
    {
        Assert.Equal(MacPreviewPresentation.MiniSize(true, false), MacPreviewPresentation.MiniSize(true, true));
        Assert.True(MacPreviewPresentation.MiniSize(true, false).Height > MacPreviewPresentation.MiniSize(false, false).Height);
    }
}
