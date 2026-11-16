using System.Text.Json;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
namespace DeskLink.Tests;
public sealed class CollaborationRecoveryTests
{
    [Fact]
    public async Task FinalMoveInterruptedBeforeReceiptIsRecoveredWithoutDuplicateFiles()
    {
        using var folder = new TestFolder(); var bytes = new byte[] { 1, 2, 3 };
        var manifest = new TransferManifest(Guid.NewGuid().ToString("N"), [new("a.txt", false, bytes.Length, CollaborationPaths.Hash(bytes))]);
        var receiver = new TransferReceiver(folder.Path); await receiver.BeginAsync(manifest);
        await receiver.WriteChunkAsync(manifest.Id, "a.txt", 0, bytes, CollaborationPaths.Hash(bytes));
        var stage = Path.Combine(folder.Path, ".desklink-partials", manifest.Id);
        await File.WriteAllTextAsync(Path.Combine(stage, "commit.json"), JsonSerializer.Serialize(new Dictionary<string,string> { ["a.txt"] = "a.txt" }));
        File.Move(Path.Combine(stage, "content", "a.txt"), Path.Combine(folder.Path, "a.txt"));
        var recovered = await new TransferReceiver(folder.Path).BeginAsync(manifest);
        Assert.NotNull(recovered.Receipt); Assert.Equal(Path.Combine(folder.Path, "a.txt"), Assert.Single(recovered.Receipt!.SavedPaths));
        Assert.Single(Directory.EnumerateFiles(folder.Path, "*.txt"));
    }
    [Fact]
    public async Task CompleteFolderTransferKeepsEmptyDirectories()
    {
        using var folder = new TestFolder(); var receive = Path.Combine(folder.Path, "receive");
        var manifest = new TransferManifest(Guid.NewGuid().ToString("N"), [new("项目", true, 0, ""), new("项目/空目录", true, 0, ""), new("项目/空.txt", false, 0, CollaborationPaths.Hash([]))]);
        var receiver = new TransferReceiver(receive); await receiver.BeginAsync(manifest); var receipt = await receiver.CompleteAsync(manifest.Id);
        Assert.True(Directory.Exists(Path.Combine(Assert.Single(receipt.SavedPaths), "空目录"))); Assert.True(File.Exists(Path.Combine(receive, "项目", "空.txt")));
    }
    [Fact]
    public async Task ReversedPreviewIsOneLeaseAndResolutionHintsFollowTheDemand()
    {
        using var folder = new TestFolder(); await using var control = Control(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port);
        server.SetPreviewRequested(true, 1920, 1080, previewRefreshSeconds: 5); var status = await client.GetStatusAsync();
        Assert.True(status.CaptureRequested); Assert.Equal(1920, status.CaptureMaxWidth); Assert.Equal(5, status.PreviewRefreshSeconds);
        await client.PublishPreviewAsync(CollaborationJpegFixture.Frame()); Assert.Equal("live", server.GetRemotePreview().State);
        server.SetPreviewRequested(false); Assert.Empty(server.GetRemotePreview().JpegBytes); Assert.False((await client.GetStatusAsync()).CaptureRequested);
        await client.SetPreviewRequestedAsync(true, maxWidth: 1920, maxHeight: 1080, previewRefreshSeconds: 60);
        Assert.Equal(1920, server.LocalCaptureMaxWidth); Assert.Equal(60, server.LocalCaptureRefreshSeconds);
    }
    [Fact]
    public async Task CertificateMismatchFailsBeforeAnyFileAccess()
    {
        using var folder = new TestFolder(); await using var control = Control(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac") with { CertificateSha256 = new string('0', 64) };
        await using var client = new CollaborationClient(bundle, true, server.Port);
        await Assert.ThrowsAnyAsync<System.Security.Authentication.AuthenticationException>(() => client.ListSharedAsync()); Assert.False(server.IsPeerConnected);
    }
    [Fact]
    public async Task ProtocolRejectsOversizedFramesBeforeAllocatingPayload()
    {
        var header = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, CollaborationProtocol.MaximumFrameBytes + 1);
        await using var input = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => CollaborationProtocol.ReadAsync<object>(input, default));
    }
    [Fact]
    public async Task CancelledUploadResumesWithSameIdentifierAndPreservesOriginal()
    {
        using var folder = new TestFolder(); var path = Path.Combine(folder.Path, "source.bin");
        var bytes = new byte[CollaborationPaths.ChunkBytes * 3 + 7]; Random.Shared.NextBytes(bytes); await File.WriteAllBytesAsync(path, bytes);
        await using var control = Control(folder.Path); await control.StartAsync();
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, ReceiveDirectory = Path.Combine(folder.Path, "receive") }); await server.StartAsync();
        var bundle = control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
        await using var client = new CollaborationClient(bundle, true, server.Port); using var cancel = new CancellationTokenSource(); string? id = null;
        var progress = new InlineProgress(value => { id = value.Id; if (value.State == "sending" && value.BytesTransferred > 0) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendFilesAsync([path], progress, cancel.Token)); Assert.NotNull(id);
        await using var restarted = new CollaborationClient(bundle, true, server.Port);
        var receipt = await restarted.ResumeSendFilesAsync([path], id!);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(receipt.SavedPaths))); Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(folder.Path, "receive"), "*.bin"));
    }
    [Fact]
    public async Task WindowsOutgoingQueueSurvivesServerRestartAndCanBeCancelled()
    {
        using var folder = new TestFolder(); var path = Path.Combine(folder.Path, "file.txt"); await File.WriteAllTextAsync(path, "persistent offer");
        await using var control = Control(folder.Path); await control.StartAsync(); var options = new CollaborationServerOptions { ProfileDirectory = folder.Path, Port = 0 };
        string id;
        await using (var first = new CollaborationServer(options)) { await first.StartAsync(); id = (await first.QueueFilesAsync([path])).Id; }
        await using var second = new CollaborationServer(options); await second.StartAsync();
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, second.Port);
        Assert.Equal(id, Assert.Single((await client.GetStatusAsync()).PendingTransfers).Id);
        second.CancelTransfer(id); Assert.Empty((await client.GetStatusAsync()).PendingTransfers); Assert.Equal("cancelled", Assert.Single(second.GetTransfers()).State);
    }
    [Fact]
    public async Task LocalShareBrowsingAndCopyNeverPretendTheMacIsOnline()
    {
        using var folder = new TestFolder(); var shared = Path.Combine(folder.Path, "shared"); Directory.CreateDirectory(shared); await File.WriteAllTextAsync(Path.Combine(shared, "a.txt"), "local");
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, SharedDirectories = [new("work", "工作", shared)] });
        Assert.Single(server.ListLocalShared("work").Items); Assert.Equal("local", System.Text.Encoding.UTF8.GetString((await server.GetLocalSharedPreviewAsync("work", "a.txt")).Bytes));
        var copied = await server.CopyLocalSharedAsync("work", "a.txt", Path.Combine(folder.Path, "copied"));
        Assert.Equal("local", await File.ReadAllTextAsync(Assert.Single(copied.SavedPaths))); Assert.False(server.IsPeerConnected); Assert.Null(server.LastPeerSeenUtc);
    }
    [Fact]
    public async Task CorruptDownloadPartialIsRestartedWithoutChangingExistingFinalFiles()
    {
        using var folder = new TestFolder(); var source = Path.Combine(folder.Path, "source.bin"); var bytes = new byte[CollaborationPaths.ChunkBytes + 37]; Random.Shared.NextBytes(bytes); await File.WriteAllBytesAsync(source, bytes);
        await using var control = Control(folder.Path); await control.StartAsync(); await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        var manifest = await server.QueueFilesAsync([source]); var destination = Path.Combine(folder.Path, "download"); var receiver = new TransferReceiver(destination); await receiver.BeginAsync(manifest);
        await receiver.WriteChunkAsync(manifest.Id, "source.bin", 0, bytes[..CollaborationPaths.ChunkBytes], CollaborationPaths.Hash(bytes[..CollaborationPaths.ChunkBytes]));
        await File.WriteAllBytesAsync(Path.Combine(destination, ".desklink-partials", manifest.Id, "content", "source.bin"), new byte[CollaborationPaths.ChunkBytes]);
        await File.WriteAllTextAsync(Path.Combine(destination, "source.bin"), "existing final");
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port);
        var receipt = Assert.Single(await client.ReceivePendingAsync(destination));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(receipt.SavedPaths))); Assert.Equal("existing final", await File.ReadAllTextAsync(Path.Combine(destination, "source.bin")));
    }
    [Fact]
    public async Task ChangingReceiveDriveMovesIncompleteTaskToNewDirectory()
    {
        using var folder = new TestFolder(); var source = Path.Combine(folder.Path, "source.bin"); var bytes = new byte[CollaborationPaths.ChunkBytes + 17]; Random.Shared.NextBytes(bytes); await File.WriteAllBytesAsync(source, bytes);
        await using var control = Control(folder.Path); await control.StartAsync(); var oldDirectory = Path.Combine(folder.Path, "old"); var newDirectory = Path.Combine(folder.Path, "new");
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, ReceiveDirectory = oldDirectory }); await server.StartAsync();
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port); using var cancel = new CancellationTokenSource(); string? id = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendFilesAsync([source], new InlineProgress(p => { id = p.Id; if (p.State == "sending") cancel.Cancel(); }), cancel.Token));
        server.UpdateReceiveDirectory(newDirectory); var receipt = await client.ResumeSendFilesAsync([source], id!);
        Assert.StartsWith(newDirectory, Assert.Single(receipt.SavedPaths)); Assert.False(File.Exists(Path.Combine(oldDirectory, "source.bin")));
    }
    [Fact]
    public async Task MissingSourceDoesNotBlockOtherPendingTransfers()
    {
        using var folder = new TestFolder(); var missing = Path.Combine(folder.Path, "missing.txt"); var available = Path.Combine(folder.Path, "available.txt"); await File.WriteAllTextAsync(missing, "missing"); await File.WriteAllTextAsync(available, "available");
        await using var control = Control(folder.Path); await control.StartAsync(); await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0 }); await server.StartAsync();
        await server.QueueFilesAsync([missing]); await server.QueueFilesAsync([available]); File.Delete(missing);
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port);
        var receipt = Assert.Single(await client.ReceivePendingAsync(Path.Combine(folder.Path, "receive")));
        Assert.Equal("available", await File.ReadAllTextAsync(Assert.Single(receipt.SavedPaths))); Assert.Empty((await client.GetStatusAsync()).PendingTransfers);
        Assert.Contains(server.GetTransfers(), p => p.State == "failed");
    }
    [Fact]
    public async Task CorruptUploadPrefixRestartsPrivatePartialForTheSameTask()
    {
        using var folder = new TestFolder(); var source = Path.Combine(folder.Path, "source.bin"); var bytes = new byte[CollaborationPaths.ChunkBytes + 17]; Random.Shared.NextBytes(bytes); await File.WriteAllBytesAsync(source, bytes);
        await using var control = Control(folder.Path); await control.StartAsync(); var receive = Path.Combine(folder.Path, "receive");
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, ReceiveDirectory = receive }); await server.StartAsync();
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port); using var cancel = new CancellationTokenSource(); string? id = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendFilesAsync([source], new InlineProgress(p => { id = p.Id; if (p.State == "sending") cancel.Cancel(); }), cancel.Token));
        await File.WriteAllBytesAsync(Path.Combine(receive, ".desklink-partials", id!, "content", "source.bin"), new byte[CollaborationPaths.ChunkBytes]);
        var receipt = await client.ResumeSendFilesAsync([source], id!); Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(receipt.SavedPaths)));
    }
    [Fact]
    public async Task CompletedReceiveRemainsIdempotentAfterReceiveDirectoryChanges()
    {
        using var folder = new TestFolder(); var source = Path.Combine(folder.Path, "source.txt"); await File.WriteAllTextAsync(source, "completed");
        await using var control = Control(folder.Path); await control.StartAsync(); var receive = Path.Combine(folder.Path, "receive"); var replacement = Path.Combine(folder.Path, "replacement");
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, Port = 0, ReceiveDirectory = receive }); await server.StartAsync();
        await using var client = new CollaborationClient(control.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac"), true, server.Port);
        var first = await client.SendFilesAsync([source]); server.UpdateReceiveDirectory(replacement); var again = await client.ResumeSendFilesAsync([source], first.Id);
        Assert.Equal(first.SavedPaths, again.SavedPaths); Assert.False(Directory.Exists(replacement));
    }
    [Fact]
    public async Task MetadataDoesNotOverwriteAnExistingPredictableTemporaryPath()
    {
        using var folder = new TestFolder(); var id = Guid.NewGuid().ToString("N"); var stage = Path.Combine(folder.Path, ".desklink-partials", id); Directory.CreateDirectory(stage);
        var sentinel = Path.Combine(stage, "manifest.json.tmp"); await File.WriteAllTextAsync(sentinel, "keep existing temporary file");
        var receiver = new TransferReceiver(folder.Path); await receiver.BeginAsync(new(id, [new("a.txt", false, 0, CollaborationPaths.Hash([]))]));
        Assert.Equal("keep existing temporary file", await File.ReadAllTextAsync(sentinel));
    }
    private sealed class InlineProgress(Action<TransferProgress> report) : IProgress<TransferProgress> { public void Report(TransferProgress value) => report(value); }
    private static PairedControlServer Control(string profile) => new(new() { ProfileDirectory = profile, Port = 0, AllowedTargets = ["Win-PC", "My-Mac"] }, _ => Task.FromResult(new DeskLinkState()), (_,_) => Task.FromResult(new DeskLinkSwitchResult()));
}
