using System.Text.Json;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Fact]
    public async Task OversizedLocalTextClearsOldSharedValueShowsWarningAndRecoversOnNextCopy()
    {
        using var folder = new TestDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var media = new TextOnlyClipboardFixture();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        server.SetLocalClipboardEnabled(true); await service.SetClipboardEnabledAsync(true); await service.RefreshOnceAsync();
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, Text: "stale"));
        media.SimulateOversizedText();
        await service.RefreshOnceAsync();
        await using var client = new CollaborationClient(bundle, true, server.Port);
        Assert.Null(await client.GetLatestClipboardAsync());
        Assert.Contains("文字过长", service.Snapshot.ClipboardError);
        Assert.Equal(0, media.WriteCount);
        media.SimulateTextCopy();
        await service.RefreshOnceAsync();
        Assert.Equal("synthetic fresh copy", server.GetRemoteClipboard()!.Text);
        Assert.Null(service.Snapshot.ClipboardError);
    }
    [Fact]
    public async Task LegacyRemoteImageNeverWritesToTheLocalClipboardAndLaterTextStillWorks()
    {
        using var folder = new TestDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var media = new TextOnlyClipboardFixture();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        server.SetLocalClipboardEnabled(true); await service.SetClipboardEnabledAsync(true); await service.RefreshOnceAsync();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+1NsQAAAAASUVORK5CYII=");
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, PngBytes: png));
        await service.RefreshOnceAsync(); await service.RefreshOnceAsync();
        Assert.Equal(0, media.WriteCount);
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, Text: "synthetic remote text"));
        await service.RefreshOnceAsync();
        Assert.Equal(1, media.WriteCount);
        Assert.Equal("synthetic remote text", media.LastWrittenText);
    }

    [Fact]
    public async Task LegacyNativeImageReplyIsNeverPublishedAndClearsOlderText()
    {
        using var folder = new TestDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var media = new TextOnlyClipboardFixture();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        server.SetLocalClipboardEnabled(true); await service.SetClipboardEnabledAsync(true); await service.RefreshOnceAsync();
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, Text: "stale"));
        media.SimulateLegacyImage();
        await service.RefreshOnceAsync();
        Assert.Null(server.GetRemoteClipboard());
        await using var client = new CollaborationClient(bundle, true, server.Port);
        Assert.Null(await client.GetLatestClipboardAsync());
        Assert.Equal(0, media.WriteCount);
    }

    [Fact]
    public async Task LocalTextCopiedWhileIgnoringLegacyImageIsPublishedNext()
    {
        using var folder = new TestDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var media = new TextOnlyClipboardFixture();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        server.SetLocalClipboardEnabled(true); await service.SetClipboardEnabledAsync(true); await service.RefreshOnceAsync();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+1NsQAAAAASUVORK5CYII=");
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, PngBytes: png));
        media.SimulateTextCopyAfterNextRead();
        await service.RefreshOnceAsync();
        Assert.Equal(0, media.WriteCount);
        await service.RefreshOnceAsync();
        Assert.Equal("synthetic fresh copy", server.GetRemoteClipboard()!.Text);
    }

    private sealed class TextOnlyClipboardFixture : IMacMediaBridge
    {
        private int count = 1;
        private bool legacyImage, oversized;
        private bool localText, copyAfterNextRead;
        public int WriteCount { get; private set; }
        public string? LastWrittenText { get; private set; }
        public void SimulateLegacyImage() { count++; legacyImage = true; }
        public void SimulateOversizedText() { count++; oversized = true; }
        public void SimulateTextCopy() { count++; oversized = false; localText = true; }
        public void SimulateTextCopyAfterNextRead() => copyAfterNextRead = true;
        public Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default)
        {
            using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(request));
            if (parameters.RootElement.GetProperty("action").GetString() == "clipboardWrite")
            {
                WriteCount++; LastWrittenText = parameters.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                count++; return Task.FromResult(JsonDocument.Parse($"{{\"status\":\"written\",\"changeCount\":{count}}}"));
            }
            var reply = JsonDocument.Parse(oversized
                ? $"{{\"status\":\"clipboard\",\"changeCount\":{count},\"oversized\":true}}"
                : legacyImage
                ? $"{{\"status\":\"clipboard\",\"changeCount\":{count},\"png\":\"invalid-dont-decode\"}}"
                : localText
                    ? $"{{\"status\":\"clipboard\",\"changeCount\":{count},\"text\":\"synthetic fresh copy\"}}"
                    : $"{{\"status\":\"clipboard\",\"changeCount\":{count}}}");
            if (copyAfterNextRead) { copyAfterNextRead = false; count++; localText = true; }
            return Task.FromResult(reply);
        }
        public Task<MacScreenCaptureReply> CaptureAsync(int width = 960, int height = 540, uint? windowId = null, CancellationToken cancellationToken = default) => Task.FromResult(new MacScreenCaptureReply());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
