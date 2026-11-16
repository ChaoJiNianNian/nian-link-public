using System.Text.Json;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Fact]
    public async Task NativeClipboardErrorDoesNotMarkIncomingTextAppliedAndRetriesIt()
    {
        using var folder = new TestDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var media = new FailedThenSuccessfulTextWrite();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        server.SetLocalClipboardEnabled(true); await service.SetClipboardEnabledAsync(true); await service.RefreshOnceAsync();
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, Text: "synthetic remote text"));
        await service.RefreshOnceAsync();
        Assert.Equal(1, media.Attempts);
        Assert.True(service.Snapshot.IsOnline);
        Assert.Contains("剪贴板", service.Snapshot.ClipboardError);
        await service.RefreshOnceAsync();
        Assert.Equal(2, media.Attempts);
        Assert.Null(service.Snapshot.ClipboardError);
        await service.RefreshOnceAsync();
        Assert.Equal(2, media.Attempts);
    }

    [Fact]
    public async Task UserCopyAfterFailedTextWriteWinsInsteadOfRetryingStaleText()
    {
        using var folder = new TestDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var media = new FailedThenSuccessfulTextWrite(failMutatesBoard: true);
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        server.SetLocalClipboardEnabled(true); await service.SetClipboardEnabledAsync(true); await service.RefreshOnceAsync();
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, Text: "synthetic remote text"));
        await service.RefreshOnceAsync();
        Assert.Equal(1, media.Attempts);
        media.SimulateLocalTextCopy();
        await service.RefreshOnceAsync();
        Assert.Equal(1, media.Attempts);
        Assert.Equal("synthetic fresh copy", server.GetRemoteClipboard()!.Text);
        Assert.Null(service.Snapshot.ClipboardError);
    }

    [Fact]
    public async Task ChangedPasteboardAfterFailedWriteNeverRetriesOldText()
    {
        using var folder = new TestDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var media = new FailedThenSuccessfulTextWrite(failMutatesBoard: true);
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        server.SetLocalClipboardEnabled(true); await service.SetClipboardEnabledAsync(true); await service.RefreshOnceAsync();
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, Text: "synthetic remote text"));
        await service.RefreshOnceAsync(); Assert.Equal(1, media.Attempts);
        await service.RefreshOnceAsync(); Assert.Equal(1, media.Attempts);
        Assert.NotNull(service.Snapshot.ClipboardError);
    }

    [Fact]
    public async Task UserCopyDuringFailedNativeWriteIsNotMistakenForInternalChange()
    {
        using var folder = new TestDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var media = new FailedThenSuccessfulTextWrite(failMutatesBoard: true, userCopyDuringFailure: true);
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        server.SetLocalClipboardEnabled(true); await service.SetClipboardEnabledAsync(true); await service.RefreshOnceAsync();
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, Text: "synthetic remote text"));
        await service.RefreshOnceAsync();
        await service.RefreshOnceAsync();
        Assert.Equal(1, media.Attempts);
        Assert.Equal("synthetic fresh copy", server.GetRemoteClipboard()!.Text);
    }

    [Fact]
    public async Task PersistentNativeWriteFailureIsBoundedAndNewRemoteCopyCanRetry()
    {
        using var folder = new TestDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var media = new AlwaysFailClipboardWrite();
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        server.SetLocalClipboardEnabled(true); await service.SetClipboardEnabledAsync(true); await service.RefreshOnceAsync();
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, "synthetic text"));
        for (var i = 0; i < 6; i++) await service.RefreshOnceAsync();
        Assert.Equal(3, media.Attempts);
        Assert.True(service.Snapshot.IsOnline); Assert.NotNull(service.Snapshot.ClipboardError);
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, "another synthetic copy"));
        await service.RefreshOnceAsync(); Assert.Equal(4, media.Attempts);
    }

    private sealed class AlwaysFailClipboardWrite : IMacMediaBridge
    {
        public int Attempts { get; private set; }
        public Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default)
        {
            using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(request));
            if (parameters.RootElement.GetProperty("action").GetString() != "clipboardWrite")
                return Task.FromResult(JsonDocument.Parse("{\"status\":\"clipboard\",\"changeCount\":1}"));
            Attempts++;
            return Task.FromResult(JsonDocument.Parse("{\"status\":\"error\",\"changeCount\":1}"));
        }
        public Task<MacScreenCaptureReply> CaptureAsync(int width=960,int height=540,uint? windowId=null,CancellationToken cancellationToken=default)=>Task.FromResult(new MacScreenCaptureReply());
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"written\"}")]
    [InlineData("{\"status\":\"written\",\"changeCount\":\"bad\"}")]
    [InlineData("{\"status\":\"error\"}")]
    public async Task InvalidNativeReplyIsNotTreatedAsASuccessfulWrite(string firstReply)
    {
        using var folder = new TestDirectory();
        await using var control = CreateControl(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        var media = new InvalidThenSuccessfulWrite(firstReply);
        await using var service = new MacCollaborationService(folder.Path, _ => Task.FromResult<DeskLinkPairingBundle?>(bundle), media, server.Port);
        server.SetLocalClipboardEnabled(true); await service.SetClipboardEnabledAsync(true); await service.RefreshOnceAsync();
        server.SetLocalClipboard(new(Guid.NewGuid().ToString("N"), "Windows", DateTimeOffset.UtcNow, "synthetic text"));
        await service.RefreshOnceAsync(); Assert.Equal(1, media.Attempts); Assert.NotNull(service.Snapshot.ClipboardError);
        await service.RefreshOnceAsync(); Assert.Equal(2, media.Attempts); Assert.Null(service.Snapshot.ClipboardError);
    }

    private sealed class InvalidThenSuccessfulWrite(string firstReply) : IMacMediaBridge
    {
        private int changeCount = 1;
        public int Attempts { get; private set; }
        public Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default)
        {
            using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(request));
            if (parameters.RootElement.GetProperty("action").GetString() != "clipboardWrite")
                return Task.FromResult(JsonDocument.Parse($"{{\"status\":\"clipboard\",\"changeCount\":{changeCount}}}"));
            if (++Attempts == 1) return Task.FromResult(JsonDocument.Parse(firstReply));
            changeCount++;
            return Task.FromResult(JsonDocument.Parse($"{{\"status\":\"written\",\"changeCount\":{changeCount}}}"));
        }
        public Task<MacScreenCaptureReply> CaptureAsync(int width = 960, int height = 540, uint? windowId = null, CancellationToken cancellationToken = default) => Task.FromResult(new MacScreenCaptureReply());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailedThenSuccessfulTextWrite(bool failMutatesBoard = false, bool userCopyDuringFailure = false) : IMacMediaBridge
    {
        private int changeCount = 1;
        private bool localText;
        public int Attempts { get; private set; }
        public void SimulateLocalTextCopy() { changeCount++; localText = true; }
        public Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default)
        {
            using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(request));
            if (parameters.RootElement.GetProperty("action").GetString() != "clipboardWrite")
                return Task.FromResult(JsonDocument.Parse(localText
                    ? $"{{\"status\":\"clipboard\",\"changeCount\":{changeCount},\"text\":\"synthetic fresh copy\"}}"
                    : $"{{\"status\":\"clipboard\",\"changeCount\":{changeCount}}}"));
            Assert.Equal(changeCount, parameters.RootElement.GetProperty("expectedChangeCount").GetInt32());
            Assert.Equal(JsonValueKind.String, parameters.RootElement.GetProperty("text").ValueKind);
            if (++Attempts == 1)
            {
                // Clearing the named board may itself advance its generation before a write fails.
                if (failMutatesBoard) changeCount++;
                if (userCopyDuringFailure) localText = true;
                return Task.FromResult(JsonDocument.Parse($"{{\"status\":\"error\",\"changeCount\":{changeCount},\"message\":\"synthetic failure\"}}"));
            }
            changeCount++;
            return Task.FromResult(JsonDocument.Parse($"{{\"status\":\"written\",\"changeCount\":{changeCount}}}"));
        }
        public Task<MacScreenCaptureReply> CaptureAsync(int width = 960, int height = 540, uint? windowId = null, CancellationToken cancellationToken = default) => Task.FromResult(new MacScreenCaptureReply());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
